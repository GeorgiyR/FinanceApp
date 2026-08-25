using System.Net;
using System.Text;
using FinanceApp.API.Models;
using FinanceApp.API.Services;
using FinanceApp.Core.Models;
using FinanceApp.Data.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FinanceApp.Core.Tests;

/// <summary>
/// Tests proving daily Yahoo history requests use at least a 2-year lookback
/// and that weekly behavior remains at 1y.
/// </summary>
public class StockHistoryLookbackTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _dbContext;

    public StockHistoryLookbackTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.AmbientTransactionWarning))
            .Options;
        _dbContext = new AppDbContext(options);
        _dbContext.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    private static string MinimalYahooChartJson(string interval) => """
        {"chart":{"result":[{"meta":{"currency":"USD","financialCurrency":"USD"},"timestamp":[1700000000],"indicators":{"quote":[{"open":[100.0],"high":[101.0],"low":[99.0],"close":[100.0],"volume":[1000]}]}}],"error":null}}
        """;

    [Fact]
    public async Task RefreshHistoryAsync_DailyInterval_Uses2YearRange()
    {
        var capturedUrls = new List<string>();

        var stock = new Stock { Id = 1, Ticker = "AAPL", Name = "Apple", CommonName = "Apple", Exchange = "NASDAQ" };
        _dbContext.Stocks.Add(stock);
        await _dbContext.SaveChangesAsync();

        // Track all URLs requested
        var handler = new TrackingHandler(url =>
        {
            capturedUrls.Add(url);
            var interval = url.Contains("interval=1d") ? "1d" :
                           url.Contains("interval=1wk") ? "1wk" :
                           url.Contains("interval=1mo") ? "1mo" :
                           url.Contains("interval=1h") ? "1h" : "5m";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(MinimalYahooChartJson(interval), Encoding.UTF8, "application/json")
            };
        });

        var httpClient = new HttpClient(handler);
        var coordinator = new YahooRequestCoordinator(
            new FixedHttpClientFactory(httpClient),
            NullLogger<YahooRequestCoordinator>.Instance,
            Options.Create(new YahooFinanceOptions
            {
                MinRequestInterval = TimeSpan.Zero,
                CooldownDuration = TimeSpan.FromMinutes(30),
                QuoteCacheDuration = TimeSpan.Zero,
                RequestTimeout = TimeSpan.FromSeconds(10)
            }));
        var service = new StockHistoryService(
            _dbContext,
            coordinator,
            new StubStockQuoteConversionService(),
            TimeProvider.System,
            Options.Create(new StockHistoryRefreshOptions()),
            NullLogger<StockHistoryService>.Instance);

        await service.RefreshHistoryAsync(stock, CancellationToken.None);

        // Assert daily uses 2y
        Assert.Contains(capturedUrls, u => u.Contains("interval=1d") && u.Contains("range=2y"));
        // Assert weekly still uses 1y
        Assert.Contains(capturedUrls, u => u.Contains("interval=1wk") && u.Contains("range=1y"));
        // Assert daily does NOT use 1y
        Assert.DoesNotContain(capturedUrls, u => u.Contains("interval=1d") && u.Contains("range=1y"));
    }

    [Fact]
    public async Task RefreshHistoryAsync_FrankfurtDailyInterval_Uses5YearRange()
    {
        var capturedUrls = new List<string>();

        var stock = new Stock { Id = 2, Ticker = "AMZN", Name = "Amazon FRA", CommonName = "Amazon", Exchange = StockExchanges.Frankfurt };
        _dbContext.Stocks.Add(stock);
        await _dbContext.SaveChangesAsync();

        var handler = new TrackingHandler(url =>
        {
            capturedUrls.Add(url);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(MinimalYahooChartJson("1d"), Encoding.UTF8, "application/json")
            };
        });

        var coordinator = new YahooRequestCoordinator(
            new FixedHttpClientFactory(new HttpClient(handler)),
            NullLogger<YahooRequestCoordinator>.Instance,
            Options.Create(new YahooFinanceOptions
            {
                MinRequestInterval = TimeSpan.Zero,
                CooldownDuration = TimeSpan.FromMinutes(30),
                QuoteCacheDuration = TimeSpan.Zero,
                RequestTimeout = TimeSpan.FromSeconds(10)
            }));
        var service = new StockHistoryService(
            _dbContext,
            coordinator,
            new StubStockQuoteConversionService(),
            TimeProvider.System,
            Options.Create(new StockHistoryRefreshOptions()),
            NullLogger<StockHistoryService>.Instance);

        await service.RefreshHistoryAsync(stock, CancellationToken.None);

        Assert.Contains(capturedUrls, u => u.Contains("interval=1d") && u.Contains("range=5y"));
    }

    private sealed class TrackingHandler : HttpMessageHandler
    {
        private readonly Func<string, HttpResponseMessage> _handler;
        public TrackingHandler(Func<string, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_handler(request.RequestUri!.ToString()));
    }

    private sealed class FixedHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public FixedHttpClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class StubStockQuoteConversionService : IStockQuoteConversionService
    {
        public Task<CurrencyConversionContext> GetConversionContextAsync(
            string? quoteCurrency,
            string? financialCurrency,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CurrencyConversionContext(
                QuoteCurrencyMetadata.Parse(quoteCurrency, financialCurrency),
                new ExchangeRateResult(quoteCurrency, 1m, DateTime.UtcNow, "stub", null),
                null));
        }

        public StockQuoteResponse BuildQuoteResponse(
            string symbol,
            decimal rawCurrentPrice,
            decimal rawPreviousClose,
            decimal percentChange,
            string marketState,
            CurrencyConversionContext conversionContext,
            string priceSession = "REGULAR",
            DateTime? priceTimestampUtc = null,
            string? priceSource = null,
            string? delayWarning = null,
            decimal? rawDayHigh = null,
            decimal? rawDayLow = null)
        {
            throw new NotSupportedException();
        }

        public StockHistoryPointResponse BuildHistoryPointResponse(StockHistoricalPrice historicalPrice, CurrencyConversionContext conversionContext)
        {
            return new StockHistoryPointResponse
            {
                Timestamp = historicalPrice.Timestamp,
                Interval = historicalPrice.Interval,
                OpenRaw = historicalPrice.Open,
                HighRaw = historicalPrice.High,
                LowRaw = historicalPrice.Low,
                CloseRaw = historicalPrice.Close,
                OpenNormalized = historicalPrice.Open,
                HighNormalized = historicalPrice.High,
                LowNormalized = historicalPrice.Low,
                CloseNormalized = conversionContext.Normalize(historicalPrice.Close),
                OpenEur = conversionContext.ConvertToEur(historicalPrice.Open),
                HighEur = conversionContext.ConvertToEur(historicalPrice.High),
                LowEur = conversionContext.ConvertToEur(historicalPrice.Low),
                CloseEur = conversionContext.ConvertToEur(historicalPrice.Close),
                Volume = historicalPrice.Volume
            };
        }
    }
}
