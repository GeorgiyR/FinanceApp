using System.Reflection;
using FinanceApp.API.Models;
using FinanceApp.API.Services;
using FinanceApp.Core.Models;
using FinanceApp.Data.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FinanceApp.Core.Tests;

public class StockQuoteRefreshHostedServiceTests
{
    [Fact]
    public async Task Cycle_UpdatesCurrentSnapshotFields()
    {
        await using var harness = await QuoteRefreshHarness.CreateAsync();
        await harness.SeedStockAsync(1, "AAPL", StockTrackingStatus.Tracked, currentPrice: 10m);

        harness.QuoteService.Behavior = (ticker, _, _, _) => Task.FromResult(StockQuoteFetchResult.Success(new StockQuoteResponse
        {
            Symbol = ticker,
            CurrentPriceEur = 123.456m,
            ChangeEur = 1.23456m,
            PercentChange = 2.34567m,
            PriceTimestampUtc = new DateTime(2026, 8, 24, 10, 0, 0, DateTimeKind.Utc),
            IsStale = false,
            DelayWarning = null,
        }));

        var result = await harness.Service.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, result.SuccessfullyApplied);
        await using var scope = harness.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stock = await db.Stocks.SingleAsync(x => x.Id == 1);
        Assert.Equal(123.46m, stock.CurrentPrice);
        Assert.Equal(1.2346m, stock.CurrentPriceChange);
        Assert.Equal(2.3457m, stock.CurrentPriceChangePercent);
        Assert.Equal(new DateTime(2026, 8, 24, 10, 0, 0, DateTimeKind.Utc), stock.CurrentPriceAt);
    }

    [Fact]
    public async Task Cycle_SelectsTrackedAndPortfolioStocks_AndDeduplicates()
    {
        await using var harness = await QuoteRefreshHarness.CreateAsync();
        await harness.SeedStockAsync(1, "TRK", StockTrackingStatus.Tracked);
        await harness.SeedStockAsync(2, "PORT", StockTrackingStatus.CatalogOnly);
        await harness.SeedStockAsync(3, "BOTH", StockTrackingStatus.Tracked);
        await harness.SeedStockAsync(4, "CAT", StockTrackingStatus.CatalogOnly);
        await harness.SeedPortfolioAsync(1, 2, 3, 3);

        harness.QuoteService.Behavior = (ticker, exchange, slug, ct) => Task.FromResult(QuoteRefreshHarness.SuccessQuote(ticker, exchange, slug, ct));

        var result = await harness.Service.RunCycleAsync(CancellationToken.None);

        Assert.Equal(3, result.UniqueStocksSelected);
        Assert.Equal(new[] { "TRK", "PORT", "BOTH" }, harness.QuoteService.Calls.ToArray());
        Assert.DoesNotContain("CAT", harness.QuoteService.Calls);
    }

    [Fact]
    public async Task Cycle_OlderSnapshot_CannotOverwriteNewerStoredSnapshot()
    {
        await using var harness = await QuoteRefreshHarness.CreateAsync();
        await harness.SeedStockAsync(
            10,
            "NEWER",
            StockTrackingStatus.Tracked,
            currentPrice: 200m,
            currentPriceAt: new DateTime(2026, 8, 24, 9, 30, 0, DateTimeKind.Utc));

        harness.QuoteService.Behavior = (ticker, _, _, _) => Task.FromResult(StockQuoteFetchResult.Success(new StockQuoteResponse
        {
            Symbol = ticker,
            CurrentPriceEur = 150m,
            ChangeEur = -1m,
            PercentChange = -1m,
            PriceTimestampUtc = new DateTime(2026, 8, 24, 9, 0, 0, DateTimeKind.Utc),
        }));

        var result = await harness.Service.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, result.SkippedOrRejectedAsStale);
        await using var scope = harness.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stock = await db.Stocks.SingleAsync(x => x.Id == 10);
        Assert.Equal(200m, stock.CurrentPrice);
        Assert.Equal(new DateTime(2026, 8, 24, 9, 30, 0, DateTimeKind.Utc), stock.CurrentPriceAt);
    }

    [Fact]
    public async Task Cycle_EqualTimestamp_NonDelayedWins()
    {
        await using var harness = await QuoteRefreshHarness.CreateAsync();
        var ts = new DateTime(2026, 8, 24, 10, 0, 0, DateTimeKind.Utc);
        await harness.SeedStockAsync(
            11,
            "EQ",
            StockTrackingStatus.Tracked,
            currentPrice: 100m,
            currentPriceAt: ts,
            currentPriceIsDelayed: true,
            delayWarning: "old delay");

        harness.QuoteService.Behavior = (ticker, _, _, _) => Task.FromResult(StockQuoteFetchResult.Success(new StockQuoteResponse
        {
            Symbol = ticker,
            CurrentPriceEur = 101m,
            ChangeEur = 1m,
            PercentChange = 1m,
            PriceTimestampUtc = ts,
            IsStale = false,
            DelayWarning = null,
        }));

        var result = await harness.Service.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, result.SuccessfullyApplied);
        await using var scope = harness.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stock = await db.Stocks.SingleAsync(x => x.Id == 11);
        Assert.False(stock.CurrentPriceIsDelayed);
        Assert.Null(stock.CurrentPriceDelayWarning);
        Assert.Equal(101m, stock.CurrentPrice);
    }

    [Fact]
    public async Task Cycle_MissingEurConversion_SkipsWithoutDamagingStoredValues()
    {
        await using var harness = await QuoteRefreshHarness.CreateAsync();
        await harness.SeedStockAsync(
            12,
            "NOEUR",
            StockTrackingStatus.Tracked,
            currentPrice: 88m,
            currentPriceAt: new DateTime(2026, 8, 24, 9, 0, 0, DateTimeKind.Utc));

        harness.QuoteService.Behavior = (ticker, _, _, _) => Task.FromResult(StockQuoteFetchResult.Success(new StockQuoteResponse
        {
            Symbol = ticker,
            CurrentPriceEur = null,
            ChangeEur = null,
            PercentChange = 0m,
            PriceTimestampUtc = new DateTime(2026, 8, 24, 10, 0, 0, DateTimeKind.Utc),
        }));

        var result = await harness.Service.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, result.SkippedNoEurConversion);
        await using var scope = harness.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stock = await db.Stocks.SingleAsync(x => x.Id == 12);
        Assert.Equal(88m, stock.CurrentPrice);
        Assert.Equal(new DateTime(2026, 8, 24, 9, 0, 0, DateTimeKind.Utc), stock.CurrentPriceAt);
    }

    [Fact]
    public async Task Cycle_OneStockFailure_DoesNotStopRemainingStocks()
    {
        await using var harness = await QuoteRefreshHarness.CreateAsync();
        await harness.SeedStockAsync(20, "FAIL", StockTrackingStatus.Tracked);
        await harness.SeedStockAsync(21, "OK", StockTrackingStatus.Tracked);

        harness.QuoteService.Behavior = (ticker, _, _, _) =>
        {
            if (ticker == "FAIL") throw new InvalidOperationException("boom");
            return Task.FromResult(QuoteRefreshHarness.SuccessQuote(ticker, string.Empty, null, CancellationToken.None));
        };

        var result = await harness.Service.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, result.Failed);
        Assert.Equal(1, result.SuccessfullyApplied);
        Assert.Contains("FAIL", harness.QuoteService.Calls);
        Assert.Contains("OK", harness.QuoteService.Calls);
    }

    [Fact]
    public async Task Cycle_RateLimited_IsCounted_AndProcessingContinues()
    {
        await using var harness = await QuoteRefreshHarness.CreateAsync(new StockQuoteRefreshOptions
        {
            Enabled = true,
            IntervalMinutes = 30,
            InitialDelaySeconds = 0,
            MaxStocksPerRun = 100,
            DelayBetweenRequestsMilliseconds = 0,
            MaxRateLimitRetries = 0,
        });

        await harness.SeedStockAsync(30, "RL", StockTrackingStatus.Tracked);
        await harness.SeedStockAsync(31, "NEXT", StockTrackingStatus.Tracked);

        harness.QuoteService.Behavior = (ticker, _, _, _) =>
        {
            if (ticker == "RL")
            {
                return Task.FromResult(StockQuoteFetchResult.RateLimit("limited", TimeSpan.FromSeconds(1)));
            }

            return Task.FromResult(QuoteRefreshHarness.SuccessQuote(ticker, string.Empty, null, CancellationToken.None));
        };

        var result = await harness.Service.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, result.RateLimited);
        Assert.Equal(1, result.SuccessfullyApplied);
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public async Task Cycle_DisabledConfiguration_PerformsNoRefresh()
    {
        await using var harness = await QuoteRefreshHarness.CreateAsync(new StockQuoteRefreshOptions { Enabled = false });
        await harness.SeedStockAsync(40, "AAA", StockTrackingStatus.Tracked);

        var result = await harness.Service.RunCycleAsync(CancellationToken.None);

        Assert.True(result.Disabled);
        Assert.Empty(harness.QuoteService.Calls);
    }

    [Fact]
    public async Task Cycle_OverlappingRuns_ArePrevented()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await QuoteRefreshHarness.CreateAsync();
        await harness.SeedStockAsync(50, "AAA", StockTrackingStatus.Tracked);
        harness.QuoteService.Behavior = async (ticker, exchange, slug, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            return QuoteRefreshHarness.SuccessQuote(ticker, exchange, slug, ct);
        };

        var first = harness.Service.RunCycleAsync(CancellationToken.None);
        await harness.QuoteService.WaitUntilCalledAsync(TimeSpan.FromSeconds(2));

        var second = await harness.Service.RunCycleAsync(CancellationToken.None);
        Assert.True(second.OverlapSkipped);

        gate.TrySetResult();
        await first;
    }

    [Fact]
    public async Task Cycle_MaxStocksPerRun_UsesDeterministicCursorFairness()
    {
        await using var harness = await QuoteRefreshHarness.CreateAsync(new StockQuoteRefreshOptions
        {
            Enabled = true,
            IntervalMinutes = 30,
            InitialDelaySeconds = 0,
            MaxStocksPerRun = 2,
            DelayBetweenRequestsMilliseconds = 0,
        });

        await harness.SeedStockAsync(1, "A", StockTrackingStatus.Tracked);
        await harness.SeedStockAsync(2, "B", StockTrackingStatus.Tracked);
        await harness.SeedStockAsync(3, "C", StockTrackingStatus.Tracked);

        harness.QuoteService.Behavior = (ticker, exchange, slug, ct) => Task.FromResult(QuoteRefreshHarness.SuccessQuote(ticker, exchange, slug, ct));

        await harness.Service.RunCycleAsync(CancellationToken.None);
        var firstCycle = harness.QuoteService.Calls.ToArray();

        harness.QuoteService.ResetCalls();
        await harness.Service.RunCycleAsync(CancellationToken.None);
        var secondCycle = harness.QuoteService.Calls.ToArray();

        Assert.Equal(new[] { "A", "B" }, firstCycle);
        Assert.Equal(new[] { "C", "A" }, secondCycle);
    }

    [Fact]
    public void NormalizeOptions_AppliesDefaultsAndBounds()
    {
        var normalized = StockQuoteRefreshHostedService.NormalizeOptions(new StockQuoteRefreshOptions
        {
            Enabled = true,
            IntervalMinutes = -10,
            InitialDelaySeconds = -1,
            MaxStocksPerRun = 0,
            DelayBetweenRequestsMilliseconds = -5,
            MaxRateLimitRetries = -3,
            InitialRateLimitBackoffSeconds = 0,
            MaxAcceptedRetryAfterSeconds = 0,
        });

        Assert.Equal(30, normalized.IntervalMinutes);
        Assert.Equal(60, normalized.InitialDelaySeconds);
        Assert.Equal(100, normalized.MaxStocksPerRun);
        Assert.Equal(1000, normalized.DelayBetweenRequestsMilliseconds);
        Assert.Equal(2, normalized.MaxRateLimitRetries);
        Assert.Equal(15, normalized.InitialRateLimitBackoffSeconds);
        Assert.Equal(300, normalized.MaxAcceptedRetryAfterSeconds);
    }

    [Fact]
    public async Task StockHistoryRefreshHostedService_RemainsHistoryOnly()
    {
        var history = new RecordingHistoryService();
        var services = new ServiceCollection();
        services.AddScoped<IStockHistoryService>(_ => history);
        var provider = services.BuildServiceProvider();

        var service = new StockHistoryRefreshHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<StockHistoryRefreshHostedService>.Instance);

        var method = typeof(StockHistoryRefreshHostedService)
            .GetMethod("RefreshAllStocksAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = (Task?)method!.Invoke(service, new object[] { CancellationToken.None });
        Assert.NotNull(task);
        await task!;

        Assert.True(history.AllStocksCalled);
        Assert.Equal(1, history.AllStocksCallCount);
    }

    private sealed class RecordingHistoryService : IStockHistoryService
    {
        public bool AllStocksCalled { get; private set; }
        public int AllStocksCallCount { get; private set; }

        public Task<StockHistoryResponse> GetHistoryAsync(Stock stock, string range, CancellationToken cancellationToken = default)
            => Task.FromResult(new StockHistoryResponse());

        public Task SyncHistoricalDataForAllStocksAsync(CancellationToken cancellationToken = default)
        {
            AllStocksCalled = true;
            AllStocksCallCount++;
            return Task.CompletedTask;
        }

        public Task SyncHistoricalDataForStockAsync(Stock stock, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<StockHistoryRefreshResponse> RefreshHistoryAsync(Stock stock, StockHistoryRefreshTrigger trigger, CancellationToken cancellationToken = default)
            => Task.FromResult(new StockHistoryRefreshResponse());

        public Task<StockHistoryRefreshResponse> RefreshHistoryAsync(Stock stock, CancellationToken cancellationToken = default)
            => Task.FromResult(new StockHistoryRefreshResponse());
    }

    private sealed class QuoteRefreshHarness : IAsyncDisposable
    {
        public IServiceProvider Services { get; }
        public RecordingQuoteFetchService QuoteService { get; }
        public StockQuoteRefreshHostedService Service { get; }

        private QuoteRefreshHarness(IServiceProvider services, RecordingQuoteFetchService quoteService, StockQuoteRefreshHostedService service)
        {
            Services = services;
            QuoteService = quoteService;
            Service = service;
        }

        public static StockQuoteFetchResult SuccessQuote(string ticker, string exchange, string? finanzenNetSlug, CancellationToken ct)
            => StockQuoteFetchResult.Success(new StockQuoteResponse
            {
                Symbol = ticker,
                CurrentPriceEur = 100m,
                ChangeEur = 1m,
                PercentChange = 1m,
                PriceTimestampUtc = new DateTime(2026, 8, 24, 10, 0, 0, DateTimeKind.Utc),
            });

        public static async Task<QuoteRefreshHarness> CreateAsync(StockQuoteRefreshOptions? options = null)
        {
            options ??= new StockQuoteRefreshOptions
            {
                Enabled = true,
                IntervalMinutes = 30,
                InitialDelaySeconds = 0,
                MaxStocksPerRun = 100,
                DelayBetweenRequestsMilliseconds = 0,
                MaxRateLimitRetries = 1,
            };

            var quoteService = new RecordingQuoteFetchService
            {
                Behavior = (ticker, exchange, slug, ct) => Task.FromResult(SuccessQuote(ticker, exchange, slug, ct)),
            };

            var timeProvider = new FixedUtcTimeProvider(new DateTimeOffset(2026, 8, 24, 10, 0, 0, TimeSpan.Zero));
            var dbName = Guid.NewGuid().ToString("N");

            var serviceCollection = new ServiceCollection();
            serviceCollection.AddDbContext<AppDbContext>(db => db.UseInMemoryDatabase(dbName));
            serviceCollection.AddSingleton<TimeProvider>(timeProvider);
            serviceCollection.AddScoped(sp => new StockQuoteSnapshotPersistenceService(
                sp.GetRequiredService<AppDbContext>(),
                sp.GetRequiredService<TimeProvider>(),
                NullLogger<StockQuoteSnapshotPersistenceService>.Instance));
            serviceCollection.AddScoped<IStockQuoteFetchService>(_ => quoteService);

            var services = serviceCollection.BuildServiceProvider();
            await using (var scope = services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.EnsureCreatedAsync();
            }

            var service = new StockQuoteRefreshHostedService(
                services.GetRequiredService<IServiceScopeFactory>(),
                services.GetRequiredService<TimeProvider>(),
                Options.Create(options),
                NullLogger<StockQuoteRefreshHostedService>.Instance,
                static (_, _) => Task.CompletedTask);

            return new QuoteRefreshHarness(services, quoteService, service);
        }

        public async Task SeedStockAsync(
            int stockId,
            string ticker,
            StockTrackingStatus trackingStatus,
            decimal currentPrice = 1m,
            DateTime? currentPriceAt = null,
            bool currentPriceIsDelayed = false,
            string? delayWarning = null)
        {
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Stocks.Add(new Stock
            {
                Id = stockId,
                Ticker = ticker,
                Name = ticker,
                Exchange = StockExchanges.Nyse,
                TrackingStatus = trackingStatus,
                CurrentPrice = currentPrice,
                CurrentPriceAt = currentPriceAt,
                CurrentPriceIsDelayed = currentPriceIsDelayed,
                CurrentPriceDelayWarning = delayWarning,
                UpdatedAt = new DateTime(2026, 8, 24, 9, 0, 0, DateTimeKind.Utc),
            });
            await db.SaveChangesAsync();
        }

        public async Task SeedPortfolioAsync(int portfolioId, params int[] stockIds)
        {
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            if (!await db.Users.AnyAsync(x => x.Id == 900))
            {
                db.Users.Add(new User
                {
                    Id = 900,
                    Username = "u",
                    NormalizedUsername = "U",
                    Email = "u@test.local",
                    NormalizedEmail = "U@TEST.LOCAL",
                    PasswordHash = "hash",
                    CreatedAt = new DateTime(2026, 8, 24, 8, 0, 0, DateTimeKind.Utc),
                });
            }

            var portfolio = new Portfolio
            {
                Id = portfolioId,
                Name = $"P{portfolioId}",
                UserId = 900,
                CreatedAt = new DateTime(2026, 8, 24, 8, 0, 0, DateTimeKind.Utc),
            };
            db.Portfolios.Add(portfolio);

            var nextItemId = await db.PortfolioItems.Select(x => (int?)x.Id).MaxAsync() ?? 0;
            foreach (var stockId in stockIds)
            {
                nextItemId++;
                db.PortfolioItems.Add(new PortfolioItem
                {
                    Id = nextItemId,
                    PortfolioId = portfolioId,
                    StockId = stockId,
                    Quantity = 1m,
                    BuyPrice = 1m,
                    BoughtAt = new DateTime(2026, 8, 24, 8, 0, 0, DateTimeKind.Utc),
                });
            }

            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (Services is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else if (Services is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private sealed class RecordingQuoteFetchService : IStockQuoteFetchService
    {
        private readonly TaskCompletionSource _called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _sync = new();

        public Func<string, string, string?, CancellationToken, Task<StockQuoteFetchResult>> Behavior { get; set; }
            = (ticker, exchange, slug, ct) => Task.FromResult(QuoteRefreshHarness.SuccessQuote(ticker, exchange, slug, ct));

        public List<string> Calls { get; } = new();

        public async Task<StockQuoteFetchResult> FetchAsync(
            string ticker,
            string exchange,
            string? finanzenNetSlug,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                Calls.Add(ticker);
            }

            _called.TrySetResult();
            return await Behavior(ticker, exchange, finanzenNetSlug, cancellationToken);
        }

        public Task WaitUntilCalledAsync(TimeSpan timeout)
            => _called.Task.WaitAsync(timeout);

        public void ResetCalls()
        {
            lock (_sync)
            {
                Calls.Clear();
            }
        }
    }

    private sealed class FixedUtcTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
