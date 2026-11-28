using FinanceApp.API.Services;
using FinanceApp.Core.Models;
using FinanceApp.Data.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FinanceApp.Core.Tests;

public class FundamentalsServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 08, 14, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RefreshFundamentalsAsync_SameIsinPrefersNyseCanonicalSymbol()
    {
        await using var context = CreateContext();
        context.Stocks.AddRange(
            new Stock { Id = 1, Ticker = "847", Exchange = StockExchanges.Frankfurt, Name = "Seagate FRA", CommonName = "Seagate", Isin = "IE00BKVD2N49" },
            new Stock { Id = 2, Ticker = "STX", Exchange = StockExchanges.Nyse, Name = "Seagate NYSE", CommonName = "Seagate", Isin = "IE00BKVD2N49" });
        await context.SaveChangesAsync();

        var provider = new StubYahooFundamentalsService(symbol => SuccessSnapshot(symbol, FixedNow.UtcDateTime, 100m));
        var service = CreateService(context, provider);

        var result = await service.RefreshFundamentalsAsync(1);

        Assert.Equal("STX", provider.RequestedSymbols.Single());
        Assert.Equal(FundamentalsState.Fresh, result.State);
        Assert.Equal("STX", result.Snapshot!.SourceSymbol);
    }

    [Fact]
    public async Task GetFundamentalsAsync_FreshCache_PreventsProviderCall()
    {
        await using var context = CreateContext();
        context.Stocks.Add(new Stock { Id = 1, Ticker = "AAPL", Exchange = StockExchanges.Nyse, Name = "Apple", CommonName = "Apple" });
        context.FundamentalsSnapshots.Add(CreateSnapshotEntity("AAPL", FixedNow.AddHours(-1).UtcDateTime, 111m, stockId: 1));
        await context.SaveChangesAsync();

        var provider = new StubYahooFundamentalsService(_ => throw new InvalidOperationException("Provider should not be called."));
        var service = CreateService(context, provider);

        var result = await service.GetFundamentalsAsync(1);

        Assert.Equal(FundamentalsState.Fresh, result.State);
        Assert.Empty(provider.RequestedSymbols);
        Assert.Equal(111m, result.Snapshot!.MarketCap);
    }

    [Fact]
    public async Task RefreshFundamentalsAsync_UpdatesCachedSnapshot()
    {
        await using var context = CreateContext();
        context.Stocks.Add(new Stock { Id = 1, Ticker = "AAPL", Exchange = StockExchanges.Nyse, Name = "Apple", CommonName = "Apple" });
        context.FundamentalsSnapshots.Add(CreateSnapshotEntity("AAPL", FixedNow.AddDays(-2).UtcDateTime, 100m, stockId: 1));
        await context.SaveChangesAsync();

        var provider = new StubYahooFundamentalsService(symbol => SuccessSnapshot(symbol, FixedNow.UtcDateTime, 222m));
        var service = CreateService(context, provider);

        var result = await service.RefreshFundamentalsAsync(1);
        var persisted = await context.FundamentalsSnapshots.Include(x => x.Periods).SingleAsync(x => x.StockId == 1);

        Assert.Equal(FundamentalsState.Fresh, result.State);
        Assert.Equal(222m, persisted.MarketCap);
        Assert.Equal(FixedNow.UtcDateTime, persisted.FetchedAtUtc);
    }

    [Fact]
    public async Task RefreshFundamentalsAsync_ProviderFailure_ReturnsStaleSnapshot()
    {
        await using var context = CreateContext();
        context.Stocks.Add(new Stock { Id = 1, Ticker = "AAPL", Exchange = StockExchanges.Nyse, Name = "Apple", CommonName = "Apple" });
        context.FundamentalsSnapshots.Add(CreateSnapshotEntity("AAPL", FixedNow.AddDays(-3).UtcDateTime, 100m, stockId: 1));
        await context.SaveChangesAsync();

        var provider = new StubYahooFundamentalsService(_ => YahooFundamentalsResult.Failure(502, "boom"));
        var service = CreateService(context, provider);

        var result = await service.RefreshFundamentalsAsync(1);
        var persisted = await context.FundamentalsSnapshots.SingleAsync(x => x.StockId == 1);

        Assert.Equal(FundamentalsState.Stale, result.State);
        Assert.Equal(100m, result.Snapshot!.MarketCap);
        Assert.NotNull(result.WarningMessage);
        Assert.Equal(FundamentalsRefreshFailureCategory.ProviderFailure, result.FailureCategory);
        Assert.Equal(100m, persisted.MarketCap);
    }

    [Fact]
    public async Task RefreshFundamentalsAsync_NoCacheAndProviderFailure_ReturnsUnavailable()
    {
        await using var context = CreateContext();
        context.Stocks.Add(new Stock { Id = 1, Ticker = "AAPL", Exchange = StockExchanges.Nyse, Name = "Apple", CommonName = "Apple" });
        await context.SaveChangesAsync();

        var provider = new StubYahooFundamentalsService(_ => YahooFundamentalsResult.Failure(502, "boom"));
        var service = CreateService(context, provider);

        var result = await service.RefreshFundamentalsAsync(1);

        Assert.Equal(FundamentalsState.Unavailable, result.State);
        Assert.Null(result.Snapshot);
        Assert.NotNull(result.WarningMessage);
        Assert.Equal(FundamentalsRefreshFailureCategory.ProviderFailure, result.FailureCategory);
    }

    [Fact]
    public async Task RefreshFundamentalsAsync_RateLimitedFailure_ExposesRateLimitCategory()
    {
        await using var context = CreateContext();
        context.Stocks.Add(new Stock { Id = 1, Ticker = "AAPL", Exchange = StockExchanges.Nyse, Name = "Apple", CommonName = "Apple" });
        context.FundamentalsSnapshots.Add(CreateSnapshotEntity("AAPL", FixedNow.AddDays(-2).UtcDateTime, 100m, stockId: 1));
        await context.SaveChangesAsync();

        var provider = new StubYahooFundamentalsService(_ => YahooFundamentalsResult.Failure(
            429,
            "rate limited",
            YahooFundamentalsFailureCategory.ProviderRateLimited));
        var service = CreateService(context, provider);

        var result = await service.RefreshFundamentalsAsync(1);

        Assert.Equal(FundamentalsState.Stale, result.State);
        Assert.Equal(FundamentalsRefreshFailureCategory.ProviderRateLimited, result.FailureCategory);
        Assert.Equal(100m, result.Snapshot!.MarketCap);
    }

    [Fact]
    public async Task RefreshFundamentalsAsync_PreservesKnownReportDateWhenProviderDropsIt()
    {
        await using var context = CreateContext();
        context.Stocks.Add(new Stock { Id = 1, Ticker = "AAPL", Exchange = StockExchanges.Nyse, Name = "Apple", CommonName = "Apple" });

        var existing = CreateSnapshotEntity("AAPL", FixedNow.AddDays(-2).UtcDateTime, 100m, stockId: 1);
        existing.EarningsEvents.Clear();
        existing.EarningsEvents.Add(new EarningsEvent
        {
            FiscalPeriodEnd = new DateTime(2026, 10, 31),
            FiscalPeriod = "2026-10-31",
            ReportDate = new DateTime(2026, 11, 18),
            DateStatus = EarningsDateStatus.Confirmed,
            Source = "Yahoo Finance",
            FetchedAtUtc = FixedNow.AddDays(-2).UtcDateTime,
        });
        context.FundamentalsSnapshots.Add(existing);
        await context.SaveChangesAsync();

        var providerSnapshot = CreateSnapshotEntity("AAPL", FixedNow.UtcDateTime, 222m, stockId: 1);
        providerSnapshot.EarningsEvents.Clear();
        providerSnapshot.EarningsEvents.Add(new EarningsEvent
        {
            FiscalPeriodEnd = new DateTime(2026, 10, 31),
            FiscalPeriod = "2026-10-31",
            DateStatus = EarningsDateStatus.Confirmed,
            Source = "Yahoo Finance",
            FetchedAtUtc = FixedNow.UtcDateTime,
        });

        var provider = new StubYahooFundamentalsService(_ => YahooFundamentalsResult.Success(providerSnapshot));
        var service = CreateService(context, provider);

        await service.RefreshFundamentalsAsync(1);

        var persisted = await context.FundamentalsSnapshots
            .Include(x => x.EarningsEvents)
            .SingleAsync(x => x.StockId == 1);
        var earningsEvent = Assert.Single(persisted.EarningsEvents);
        Assert.Equal(new DateTime(2026, 11, 18), earningsEvent.ReportDate);
        Assert.Equal(new DateTime(2026, 10, 31), earningsEvent.FiscalPeriodEnd);
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new AppDbContext(options);
    }

    private static FundamentalsService CreateService(AppDbContext context, IYahooFundamentalsService provider) =>
        new(
            context,
            provider,
            Options.Create(new YahooFinanceOptions
            {
                FundamentalsCacheDuration = TimeSpan.FromHours(24),
                EarningsCacheDuration = TimeSpan.FromHours(6)
            }),
            new FixedTimeProvider(FixedNow),
            NullLogger<FundamentalsService>.Instance);

    private static YahooFundamentalsResult SuccessSnapshot(string symbol, DateTime fetchedAtUtc, decimal marketCap, int stockId = 0)
    {
        var snapshot = CreateSnapshotEntity(symbol, fetchedAtUtc, marketCap, stockId);
        return YahooFundamentalsResult.Success(snapshot);
    }

    private static CompanyFundamentalsSnapshot CreateSnapshotEntity(string symbol, DateTime fetchedAtUtc, decimal marketCap, int stockId = 0)
    {
        return new CompanyFundamentalsSnapshot
        {
            StockId = stockId,
            SourceSymbol = symbol,
            MarketCap = marketCap,
            Currency = "USD",
            Source = "Yahoo Finance",
            AsOfDate = fetchedAtUtc.Date,
            FetchedAtUtc = fetchedAtUtc,
            Periods =
            [
                new FinancialPeriod
                {
                    PeriodType = PeriodType.Quarterly,
                    PeriodEndDate = fetchedAtUtc.Date.AddMonths(-3),
                    Revenue = 10m,
                    Source = "Yahoo Finance",
                    AsOfDate = fetchedAtUtc.Date.AddMonths(-3),
                    FetchedAtUtc = fetchedAtUtc
                }
            ],
            EarningsEvents =
            [
                new EarningsEvent
                {
                    ReportDate = fetchedAtUtc.Date.AddDays(10),
                    DateStatus = EarningsDateStatus.Estimated,
                    Source = "Yahoo Finance",
                    FetchedAtUtc = fetchedAtUtc
                }
            ]
        };
    }

    private sealed class StubYahooFundamentalsService : IYahooFundamentalsService
    {
        private readonly Func<string, YahooFundamentalsResult> _resultFactory;

        public StubYahooFundamentalsService(Func<string, YahooFundamentalsResult> resultFactory)
        {
            _resultFactory = resultFactory;
        }

        public List<string> RequestedSymbols { get; } = new();

        public Task<YahooFundamentalsResult> GetFundamentalsAsync(string symbol, CancellationToken cancellationToken = default)
        {
            RequestedSymbols.Add(symbol);
            return Task.FromResult(_resultFactory(symbol));
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
