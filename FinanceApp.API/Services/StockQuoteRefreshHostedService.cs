using System.Diagnostics;
using FinanceApp.Core.Models;
using FinanceApp.Data.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinanceApp.API.Services;

public sealed class StockQuoteRefreshOptions
{
    public bool Enabled { get; init; } = true;
    public int IntervalMinutes { get; init; } = 30;
    public int InitialDelaySeconds { get; init; } = 60;
    public int MaxStocksPerRun { get; init; } = 100;
    public int DelayBetweenRequestsMilliseconds { get; init; } = 1000;
    public int MaxRateLimitRetries { get; init; } = 2;
    public int InitialRateLimitBackoffSeconds { get; init; } = 15;
    public int MaxAcceptedRetryAfterSeconds { get; init; } = 300;
}

internal sealed record StockQuoteRefreshCycleResult
{
    public bool Disabled { get; init; }
    public bool OverlapSkipped { get; init; }
    public int UniqueStocksSelected { get; init; }
    public int Attempted { get; init; }
    public int SuccessfullyApplied { get; init; }
    public int SkippedNoEurConversion { get; init; }
    public int SkippedOrRejectedAsStale { get; init; }
    public int DelayedQuotes { get; init; }
    public int RateLimited { get; init; }
    public int Failed { get; init; }
    public TimeSpan Duration { get; init; }
}

public sealed class StockQuoteRefreshHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<StockQuoteRefreshHostedService> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly StockQuoteRefreshOptions _options;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly object _cursorSync = new();

    private int _nextStartAfterStockId;

    public StockQuoteRefreshHostedService(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        IOptions<StockQuoteRefreshOptions> options,
        ILogger<StockQuoteRefreshHostedService> logger,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
        _delayAsync = delayAsync ?? ((delay, cancellationToken) => Task.Delay(delay, cancellationToken));
        _options = NormalizeOptions(options.Value);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Periodic stock quote refresh is disabled by configuration.");
            return;
        }

        try
        {
            if (_options.InitialDelaySeconds > 0)
            {
                await _delayAsync(TimeSpan.FromSeconds(_options.InitialDelaySeconds), stoppingToken);
            }

            await RunCycleAsync(stoppingToken);

            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_options.IntervalMinutes));
            while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunCycleAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal async Task<StockQuoteRefreshCycleResult> RunCycleAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return new StockQuoteRefreshCycleResult { Disabled = true };
        }

        if (!await _runGate.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            _logger.LogInformation("Skipping periodic stock quote refresh cycle because a previous cycle is still running.");
            return new StockQuoteRefreshCycleResult { OverlapSkipped = true };
        }

        var startedAt = _timeProvider.GetUtcNow();
        var watch = Stopwatch.StartNew();
        var result = new StockQuoteRefreshCycleResult();

        try
        {
            List<StockQuoteRefreshCandidate> selected;
            await using (var selectionScope = _scopeFactory.CreateAsyncScope())
            {
                var context = selectionScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var allCandidates = await SelectCandidatesAsync(context, cancellationToken);
                selected = SelectCandidatesForThisRun(allCandidates);
            }

            var attempted = 0;
            var applied = 0;
            var skippedNoEur = 0;
            var staleRejected = 0;
            var delayed = 0;
            var rateLimited = 0;
            var failed = 0;

            for (var index = 0; index < selected.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = selected[index];
                attempted++;

                var outcome = await RefreshSingleStockAsync(candidate, cancellationToken);

                if (outcome.Applied)
                {
                    applied++;
                }

                if (outcome.SkippedNoEur)
                {
                    skippedNoEur++;
                }

                if (outcome.SkippedOrRejectedAsStale)
                {
                    staleRejected++;
                }

                if (outcome.Delayed)
                {
                    delayed++;
                }

                if (outcome.RateLimited)
                {
                    rateLimited++;
                }

                if (outcome.Failed)
                {
                    failed++;
                }

                if (index < selected.Count - 1 && _options.DelayBetweenRequestsMilliseconds > 0)
                {
                    await _delayAsync(TimeSpan.FromMilliseconds(_options.DelayBetweenRequestsMilliseconds), cancellationToken);
                }
            }

            watch.Stop();
            result = new StockQuoteRefreshCycleResult
            {
                UniqueStocksSelected = selected.Count,
                Attempted = attempted,
                SuccessfullyApplied = applied,
                SkippedNoEurConversion = skippedNoEur,
                SkippedOrRejectedAsStale = staleRejected,
                DelayedQuotes = delayed,
                RateLimited = rateLimited,
                Failed = failed,
                Duration = watch.Elapsed,
            };

            _logger.LogInformation(
                "Periodic stock quote refresh completed: selected={Selected} attempted={Attempted} applied={Applied} skippedNoEur={SkippedNoEur} staleRejected={StaleRejected} delayed={Delayed} rateLimited={RateLimited} failed={Failed} durationMs={DurationMs}",
                result.UniqueStocksSelected,
                result.Attempted,
                result.SuccessfullyApplied,
                result.SkippedNoEurConversion,
                result.SkippedOrRejectedAsStale,
                result.DelayedQuotes,
                result.RateLimited,
                result.Failed,
                (int)result.Duration.TotalMilliseconds);

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            watch.Stop();
            _logger.LogError(ex,
                "Periodic stock quote refresh cycle failed unexpectedly after {DurationMs} ms.",
                (int)watch.Elapsed.TotalMilliseconds);
            return result with { Failed = result.Failed + 1, Duration = watch.Elapsed };
        }
        finally
        {
            _runGate.Release();
            var completedAt = _timeProvider.GetUtcNow();
            _logger.LogDebug(
                "Periodic stock quote refresh cycle window: startedAtUtc={StartedAtUtc} completedAtUtc={CompletedAtUtc}",
                startedAt.UtcDateTime,
                completedAt.UtcDateTime);
        }
    }

    internal static StockQuoteRefreshOptions NormalizeOptions(StockQuoteRefreshOptions raw)
    {
        static int Clamp(int value, int min, int max) => Math.Min(max, Math.Max(min, value));

        var normalizedInterval = raw.IntervalMinutes <= 0 ? 30 : raw.IntervalMinutes;
        var normalizedInitialDelay = raw.InitialDelaySeconds < 0 ? 60 : raw.InitialDelaySeconds;
        var normalizedMaxStocks = raw.MaxStocksPerRun <= 0 ? 100 : raw.MaxStocksPerRun;
        var normalizedRequestDelay = raw.DelayBetweenRequestsMilliseconds < 0 ? 1000 : raw.DelayBetweenRequestsMilliseconds;
        var normalizedMaxRateLimitRetries = raw.MaxRateLimitRetries < 0 ? 2 : raw.MaxRateLimitRetries;
        var normalizedInitialRateLimitBackoff = raw.InitialRateLimitBackoffSeconds <= 0 ? 15 : raw.InitialRateLimitBackoffSeconds;
        var normalizedMaxRetryAfter = raw.MaxAcceptedRetryAfterSeconds <= 0 ? 300 : raw.MaxAcceptedRetryAfterSeconds;

        return new StockQuoteRefreshOptions
        {
            Enabled = raw.Enabled,
            IntervalMinutes = Clamp(normalizedInterval, 1, 24 * 60),
            InitialDelaySeconds = Clamp(normalizedInitialDelay, 0, 60 * 60),
            MaxStocksPerRun = Clamp(normalizedMaxStocks, 1, 2000),
            DelayBetweenRequestsMilliseconds = Clamp(normalizedRequestDelay, 0, 60_000),
            MaxRateLimitRetries = Clamp(normalizedMaxRateLimitRetries, 0, 10),
            InitialRateLimitBackoffSeconds = Clamp(normalizedInitialRateLimitBackoff, 1, 600),
            MaxAcceptedRetryAfterSeconds = Clamp(normalizedMaxRetryAfter, 1, 1800),
        };
    }

    private async Task<List<StockQuoteRefreshCandidate>> SelectCandidatesAsync(
        AppDbContext context,
        CancellationToken cancellationToken)
    {
        var portfolioStockIds = context.PortfolioItems
            .Select(x => x.StockId)
            .Distinct();

        return await context.Stocks
            .AsNoTracking()
            .Where(x => x.TrackingStatus == StockTrackingStatus.Tracked || portfolioStockIds.Contains(x.Id))
            .OrderBy(x => x.Id)
            .Select(x => new StockQuoteRefreshCandidate(x.Id, x.Ticker, x.Exchange, x.FinanzenNetSlug))
            .ToListAsync(cancellationToken);
    }

    private List<StockQuoteRefreshCandidate> SelectCandidatesForThisRun(List<StockQuoteRefreshCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return candidates;
        }

        var take = Math.Min(_options.MaxStocksPerRun, candidates.Count);
        if (take == candidates.Count)
        {
            lock (_cursorSync)
            {
                _nextStartAfterStockId = candidates[^1].StockId;
            }

            return candidates;
        }

        var result = new List<StockQuoteRefreshCandidate>(take);
        lock (_cursorSync)
        {
            var startIndex = candidates.FindIndex(x => x.StockId > _nextStartAfterStockId);
            if (startIndex < 0)
            {
                startIndex = 0;
            }

            for (var i = 0; i < take; i++)
            {
                result.Add(candidates[(startIndex + i) % candidates.Count]);
            }

            _nextStartAfterStockId = result[^1].StockId;
        }

        return result;
    }

    private async Task<StockQuoteRefreshStockOutcome> RefreshSingleStockAsync(
        StockQuoteRefreshCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(candidate.Ticker))
        {
            _logger.LogWarning(
                "Periodic stock quote refresh skipped stockId={StockId}: ticker is missing.",
                candidate.StockId);
            return StockQuoteRefreshStockOutcome.FailedOutcome;
        }

        var rateLimitedEncountered = false;

        for (var retryAttempt = 0; ; retryAttempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StockQuoteFetchResult fetchResult;

            try
            {
                await using var fetchScope = _scopeFactory.CreateAsyncScope();
                var quoteFetchService = fetchScope.ServiceProvider.GetRequiredService<IStockQuoteFetchService>();
                fetchResult = await quoteFetchService.FetchAsync(
                    candidate.Ticker,
                    candidate.Exchange ?? string.Empty,
                    candidate.FinanzenNetSlug,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Periodic stock quote refresh fetch failed for stockId={StockId} ticker={Ticker}",
                    candidate.StockId,
                    candidate.Ticker);
                return new StockQuoteRefreshStockOutcome(Failed: true, RateLimited: rateLimitedEncountered);
            }

            if (fetchResult.IsRateLimited)
            {
                rateLimitedEncountered = true;
                if (retryAttempt >= _options.MaxRateLimitRetries)
                {
                    _logger.LogWarning(
                        "Periodic stock quote refresh rate-limit retries exhausted for stockId={StockId} ticker={Ticker} retries={Retries}",
                        candidate.StockId,
                        candidate.Ticker,
                        retryAttempt);
                    return new StockQuoteRefreshStockOutcome(Failed: true, RateLimited: true);
                }

                var retryDelay = SelectRetryDelay(fetchResult.RetryAfterDelay, retryAttempt + 1);
                _logger.LogInformation(
                    "Periodic stock quote refresh rate limited for stockId={StockId} ticker={Ticker} retryAttempt={RetryAttempt}/{MaxRetries} retryAfterMs={RetryAfterMs}",
                    candidate.StockId,
                    candidate.Ticker,
                    retryAttempt + 1,
                    _options.MaxRateLimitRetries,
                    (int)retryDelay.TotalMilliseconds);
                await _delayAsync(retryDelay, cancellationToken);
                continue;
            }

            if (!fetchResult.IsSuccess || fetchResult.Quote is null)
            {
                _logger.LogWarning(
                    "Periodic stock quote refresh quote fetch unsuccessful for stockId={StockId} ticker={Ticker} status={StatusCode}",
                    candidate.StockId,
                    candidate.Ticker,
                    fetchResult.StatusCode);
                return new StockQuoteRefreshStockOutcome(Failed: true, RateLimited: rateLimitedEncountered);
            }

            var quote = fetchResult.Quote;
            var delayed = quote.IsStale || !string.IsNullOrWhiteSpace(quote.DelayWarning);

            if (quote.CurrentPriceEur is null)
            {
                return new StockQuoteRefreshStockOutcome(SkippedNoEur: true, Delayed: delayed, RateLimited: rateLimitedEncountered);
            }

            var persistenceRequest = new PersistStockQuoteSnapshotRequest
            {
                CurrentPrice = Math.Round(quote.CurrentPriceEur.Value, 2),
                CurrentPriceChange = quote.ChangeEur.HasValue ? Math.Round(quote.ChangeEur.Value, 4) : null,
                CurrentPriceChangePercent = Math.Round(quote.PercentChange, 4),
                CurrentPriceAt = quote.PriceTimestampUtc,
                CurrentPriceIsDelayed = delayed,
                CurrentPriceDelayWarning = quote.DelayWarning,
                QuoteCurrency = "EUR",
                FinancialCurrency = "EUR",
                NormalizedQuoteCurrency = "EUR",
                QuoteUnitMultiplier = 1m,
            };

            try
            {
                await using var persistScope = _scopeFactory.CreateAsyncScope();
                var persistenceService = persistScope.ServiceProvider.GetRequiredService<StockQuoteSnapshotPersistenceService>();
                var persistenceResult = await persistenceService.ApplyAsync(
                    candidate.StockId,
                    persistenceRequest,
                    cancellationToken);

                if (!persistenceResult.StockFound)
                {
                    _logger.LogWarning(
                        "Periodic stock quote refresh skipped missing stock row for stockId={StockId} ticker={Ticker}",
                        candidate.StockId,
                        candidate.Ticker);
                    return new StockQuoteRefreshStockOutcome(Failed: true, Delayed: delayed, RateLimited: rateLimitedEncountered);
                }

                if (!persistenceResult.Applied)
                {
                    _logger.LogDebug(
                        "Periodic stock quote refresh rejected snapshot for stockId={StockId} ticker={Ticker} reason={Reason}",
                        candidate.StockId,
                        candidate.Ticker,
                        persistenceResult.Reason);
                    return new StockQuoteRefreshStockOutcome(
                        SkippedOrRejectedAsStale: true,
                        Delayed: delayed,
                        RateLimited: rateLimitedEncountered);
                }

                return new StockQuoteRefreshStockOutcome(
                    Applied: true,
                    Delayed: delayed,
                    RateLimited: rateLimitedEncountered);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Periodic stock quote persistence failed for stockId={StockId} ticker={Ticker}",
                    candidate.StockId,
                    candidate.Ticker);
                return new StockQuoteRefreshStockOutcome(Failed: true, Delayed: delayed, RateLimited: rateLimitedEncountered);
            }
        }
    }

    private TimeSpan SelectRetryDelay(TimeSpan? providerRetryAfter, int retryAttempt)
    {
        if (providerRetryAfter is { } providerDelay && providerDelay > TimeSpan.Zero)
        {
            var maxRetryAfter = TimeSpan.FromSeconds(_options.MaxAcceptedRetryAfterSeconds);
            return providerDelay > maxRetryAfter ? maxRetryAfter : providerDelay;
        }

        var initialDelay = TimeSpan.FromSeconds(_options.InitialRateLimitBackoffSeconds);
        var delayMs = initialDelay.TotalMilliseconds * Math.Pow(2, retryAttempt - 1);
        var maxDelayMs = TimeSpan.FromSeconds(_options.MaxAcceptedRetryAfterSeconds).TotalMilliseconds;
        return TimeSpan.FromMilliseconds(Math.Min(delayMs, maxDelayMs));
    }

    private readonly record struct StockQuoteRefreshCandidate(
        int StockId,
        string Ticker,
        string? Exchange,
        string? FinanzenNetSlug);

    private readonly record struct StockQuoteRefreshStockOutcome(
        bool Applied = false,
        bool SkippedNoEur = false,
        bool SkippedOrRejectedAsStale = false,
        bool Delayed = false,
        bool RateLimited = false,
        bool Failed = false)
    {
        public static StockQuoteRefreshStockOutcome FailedOutcome => new(Failed: true);
    }
}
