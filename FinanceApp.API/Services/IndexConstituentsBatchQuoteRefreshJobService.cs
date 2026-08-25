using System.Threading.Channels;
using FinanceApp.API.Models;
using FinanceApp.Core.Models;
using FinanceApp.Data.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinanceApp.API.Services;

public sealed class IndexConstituentsBatchQuoteRefreshJobOptions
{
    public int QueueCapacity { get; init; } = 32;
    public int RegistryCapacity { get; init; } = 128;
    public TimeSpan CompletedJobTtl { get; init; } = TimeSpan.FromMinutes(30);
    public int MaxErrorMessageLength { get; init; } = 240;
    public int BatchSize { get; init; } = 5;
    public TimeSpan DelayBetweenRequests { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan DelayBetweenBatches { get; init; } = TimeSpan.FromSeconds(1);
    public int MaxRateLimitRetries { get; init; } = 3;
    public TimeSpan InitialRateLimitBackoff { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan MaxRateLimitBackoff { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan MaxAcceptedRetryAfter { get; init; } = TimeSpan.FromMinutes(5);
}

public interface IIndexConstituentsBatchQuoteRefreshJobService
{
    IndexConstituentsBatchQuoteRefreshJobEnqueueResult Enqueue(int marketIndexId);
    bool TryGetJob(int marketIndexId, string jobId, out IndexConstituentsBatchQuoteRefreshJobResponse? job);
}

public sealed class IndexConstituentsBatchQuoteRefreshJobService
    : BackgroundService, IIndexConstituentsBatchQuoteRefreshJobService
{
    private const string RateLimitMessage = "Поставщик временно ограничил запросы. Часть цен не обновлена.";
    private const string InterruptedMessage = "Обновление прервано из-за перезапуска приложения. Повторите попытку.";
    private const string GenericFailureMessage = "Не удалось завершить пакетное обновление цен. Попробуйте позже.";

    private readonly object _sync = new();
    private readonly Dictionary<string, JobEntry> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _activeJobsByIndexId = new();
    private readonly Channel<string> _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IndexConstituentsBatchQuoteRefreshJobOptions _options;
    private readonly ILogger<IndexConstituentsBatchQuoteRefreshJobService> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly ISystemProcessJournalService? _processJournalService;

    public IndexConstituentsBatchQuoteRefreshJobService(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        IOptions<IndexConstituentsBatchQuoteRefreshJobOptions> options,
        ILogger<IndexConstituentsBatchQuoteRefreshJobService> logger,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        ISystemProcessJournalService? processJournalService = null)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
        _processJournalService = processJournalService;
        _delayAsync = delayAsync ?? ((delay, cancellationToken) => Task.Delay(delay, cancellationToken));

        var raw = options.Value;
        _options = new IndexConstituentsBatchQuoteRefreshJobOptions
        {
            QueueCapacity = raw.QueueCapacity > 0 ? raw.QueueCapacity : 32,
            RegistryCapacity = raw.RegistryCapacity > 0 ? raw.RegistryCapacity : 128,
            CompletedJobTtl = raw.CompletedJobTtl > TimeSpan.Zero ? raw.CompletedJobTtl : TimeSpan.FromMinutes(30),
            MaxErrorMessageLength = raw.MaxErrorMessageLength > 0 ? raw.MaxErrorMessageLength : 240,
            BatchSize = raw.BatchSize > 0 ? raw.BatchSize : 5,
            DelayBetweenRequests = raw.DelayBetweenRequests > TimeSpan.Zero
                ? raw.DelayBetweenRequests
                : TimeSpan.FromMilliseconds(250),
            DelayBetweenBatches = raw.DelayBetweenBatches > TimeSpan.Zero
                ? raw.DelayBetweenBatches
                : TimeSpan.FromSeconds(1),
            MaxRateLimitRetries = raw.MaxRateLimitRetries >= 0 ? raw.MaxRateLimitRetries : 3,
            InitialRateLimitBackoff = raw.InitialRateLimitBackoff > TimeSpan.Zero
                ? raw.InitialRateLimitBackoff
                : TimeSpan.FromSeconds(15),
            MaxRateLimitBackoff = raw.MaxRateLimitBackoff > TimeSpan.Zero
                ? raw.MaxRateLimitBackoff
                : TimeSpan.FromSeconds(60),
            MaxAcceptedRetryAfter = raw.MaxAcceptedRetryAfter > TimeSpan.Zero
                ? raw.MaxAcceptedRetryAfter
                : TimeSpan.FromMinutes(5),
        };

        _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public IndexConstituentsBatchQuoteRefreshJobEnqueueResult Enqueue(int marketIndexId)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        JobEntry entry;
        lock (_sync)
        {
            CleanupLocked(now);

            if (_activeJobsByIndexId.TryGetValue(marketIndexId, out var activeId)
                && _jobs.TryGetValue(activeId, out var active)
                && active.State is IndexConstituentsBatchQuoteRefreshJobState.Queued
                    or IndexConstituentsBatchQuoteRefreshJobState.Running)
            {
                return new IndexConstituentsBatchQuoteRefreshJobEnqueueResult
                {
                    Status = IndexConstituentsBatchQuoteRefreshJobEnqueueStatus.ReusedActiveJob,
                    Job = ToResponse(active, reusedActiveJob: true),
                };
            }

            if (_jobs.Count >= _options.RegistryCapacity && !TryEvictOneTerminalJobLocked())
            {
                return new IndexConstituentsBatchQuoteRefreshJobEnqueueResult
                {
                    Status = IndexConstituentsBatchQuoteRefreshJobEnqueueStatus.QueueFull,
                };
            }

            entry = new JobEntry
            {
                JobId = Guid.NewGuid().ToString("N"),
                MarketIndexId = marketIndexId,
                State = IndexConstituentsBatchQuoteRefreshJobState.Queued,
                CreatedAtUtc = now,
            };
            _jobs[entry.JobId] = entry;
            _activeJobsByIndexId[marketIndexId] = entry.JobId;
        }

        if (!_queue.Writer.TryWrite(entry.JobId))
        {
            lock (_sync) { RemoveJobLocked(entry.JobId); }
            return new IndexConstituentsBatchQuoteRefreshJobEnqueueResult
            {
                Status = IndexConstituentsBatchQuoteRefreshJobEnqueueStatus.QueueFull,
            };
        }

        return new IndexConstituentsBatchQuoteRefreshJobEnqueueResult
        {
            Status = IndexConstituentsBatchQuoteRefreshJobEnqueueStatus.Enqueued,
            Job = ToResponse(entry, reusedActiveJob: false),
        };
    }

    public bool TryGetJob(int marketIndexId, string jobId, out IndexConstituentsBatchQuoteRefreshJobResponse? job)
    {
        job = null;
        if (string.IsNullOrWhiteSpace(jobId)) return false;

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        lock (_sync)
        {
            CleanupLocked(now);
            if (!_jobs.TryGetValue(jobId, out var entry)) return false;
            if (entry.MarketIndexId != marketIndexId) return false;
            job = ToResponse(entry, reusedActiveJob: false);
            return true;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken))
            {
                while (_queue.Reader.TryRead(out var jobId))
                {
                    await ProcessJobAsync(jobId, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            InterruptActiveJobs(InterruptedMessage);
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        InterruptActiveJobs(InterruptedMessage);
        return base.StopAsync(cancellationToken);
    }

    private async Task ProcessJobAsync(string jobId, CancellationToken stoppingToken)
    {
        if (!TryMarkRunning(jobId)) return;

        int marketIndexId;
        lock (_sync)
        {
            if (!_jobs.TryGetValue(jobId, out var e)) return;
            marketIndexId = e.MarketIndexId;
        }

        var processRunId = await (_processJournalService?.CreateOrGetAsync(new CreateSystemProcessRunRequest
        {
            ProcessType = SystemProcessTypes.IndexConstituentsBatchQuoteRefresh,
            Trigger = SystemProcessTrigger.Automatic,
            ExternalRunKey = jobId,
            CorrelationId = jobId,
            InitialStatus = SystemProcessRunStatus.Running,
            Details = new { MarketIndexId = marketIndexId, JobId = jobId },
        }, stoppingToken) ?? Task.FromResult(0L));

        _logger.LogInformation(
            "Batch quote refresh started: jobId={JobId} indexId={IndexId}", jobId, marketIndexId);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var marketIndex = await context.MarketIndices
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == marketIndexId, stoppingToken);

            if (marketIndex is null)
            {
                MarkCompleted(jobId, IndexConstituentsBatchQuoteRefreshJobState.Failed, error: "Индекс не найден.");
                await CompleteJournalForJobAsync(processRunId, IndexConstituentsBatchQuoteRefreshJobState.Failed, 0, 0, new Counters(), "Индекс не найден.");
                return;
            }

            if (marketIndex.IsArchived)
            {
                MarkCompleted(jobId, IndexConstituentsBatchQuoteRefreshJobState.Failed,
                    error: "Нельзя обновлять цены для архивного индекса.");
                await CompleteJournalForJobAsync(processRunId, IndexConstituentsBatchQuoteRefreshJobState.Failed, 0, 0, new Counters(), "Нельзя обновлять цены для архивного индекса.");
                return;
            }

            var stocks = await context.StockMarketIndices
                .Include(x => x.Stock)
                .Where(x => x.MarketIndexId == marketIndexId && x.EffectiveTo == null)
                .OrderBy(x => x.StockId)
                .Select(x => x.Stock!)
                .AsNoTracking()
                .ToListAsync(stoppingToken);

            var total = stocks.Count;
            SetTotal(jobId, total);

            _logger.LogInformation(
                "Batch quote refresh: jobId={JobId} indexId={IndexId} total={Total}",
                jobId, marketIndexId, total);

            var counters = new Counters();

            var processed = 0;
            var batchSize = _options.BatchSize;
            for (var chunkStart = 0; chunkStart < stocks.Count; chunkStart += batchSize)
            {
                var chunkEndExclusive = Math.Min(chunkStart + batchSize, stocks.Count);
                for (var index = chunkStart; index < chunkEndExclusive; index++)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    var stock = stocks[index];
                    await ProcessSingleStockWithRetriesAsync(
                        jobId,
                        marketIndexId,
                        stock,
                        counters,
                        stoppingToken);

                    processed++;
                    UpdateProgress(jobId, processed, counters);

                    if (index < chunkEndExclusive - 1)
                    {
                        await DelayIfNeededAsync(_options.DelayBetweenRequests, stoppingToken);
                    }
                }

                if (chunkEndExclusive < stocks.Count)
                {
                    await DelayIfNeededAsync(_options.DelayBetweenBatches, stoppingToken);
                }
            }

            MarkCompleted(jobId, IndexConstituentsBatchQuoteRefreshJobState.Succeeded,
                processed: processed, total: total, counters: counters,
                error: counters.RateLimitedSkipped > 0 ? RateLimitMessage : null);
            await CompleteJournalForJobAsync(
                processRunId,
                counters.RateLimitedSkipped > 0
                    ? IndexConstituentsBatchQuoteRefreshJobState.RateLimited
                    : IndexConstituentsBatchQuoteRefreshJobState.Succeeded,
                processed,
                total,
                counters,
                counters.RateLimitedSkipped > 0 ? RateLimitMessage : null);

            _logger.LogInformation(
                "Batch quote refresh completed: jobId={JobId} indexId={IndexId} total={Total} succeeded={Succeeded} delayed={Delayed} noEur={NoEur} staleRejected={StaleRejected} providerFailed={ProviderFailed} persistFailed={PersistFailed} rateLimited={RateLimited} rateLimitRetries={RateLimitRetries} rateLimitedSkipped={RateLimitedSkipped}",
                jobId, marketIndexId, total, counters.Succeeded, counters.Delayed, counters.NoEurConversion,
                counters.StaleRejected, counters.ProviderFailed, counters.PersistFailed, counters.RateLimited,
                counters.RateLimitRetries, counters.RateLimitedSkipped);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            MarkCompleted(jobId, IndexConstituentsBatchQuoteRefreshJobState.Interrupted,
                error: InterruptedMessage);
            await CompleteJournalForJobAsync(processRunId, IndexConstituentsBatchQuoteRefreshJobState.Interrupted, 0, 0, new Counters(), InterruptedMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unexpected failure in batch quote refresh job {JobId} (indexId={IndexId})", jobId, marketIndexId);
            MarkCompleted(jobId, IndexConstituentsBatchQuoteRefreshJobState.Failed,
                error: GenericFailureMessage);
            await CompleteJournalForJobAsync(processRunId, IndexConstituentsBatchQuoteRefreshJobState.Failed, 0, 0, new Counters(), GenericFailureMessage);
        }
    }

    private async Task CompleteJournalForJobAsync(
        long processRunId,
        IndexConstituentsBatchQuoteRefreshJobState state,
        int processed,
        int total,
        Counters counters,
        string? error)
    {
        if (_processJournalService is null || processRunId <= 0)
        {
            return;
        }

        var status = state switch
        {
            IndexConstituentsBatchQuoteRefreshJobState.Succeeded => SystemProcessRunStatus.Succeeded,
            IndexConstituentsBatchQuoteRefreshJobState.RateLimited => SystemProcessRunStatus.CompletedWithErrors,
            IndexConstituentsBatchQuoteRefreshJobState.Interrupted => SystemProcessRunStatus.Interrupted,
            _ => SystemProcessRunStatus.Failed,
        };

        await _processJournalService.CompleteAsync(
            processRunId,
            status,
            new UpdateSystemProcessRunRequest
            {
                TotalItems = total,
                ProcessedItems = processed,
                SucceededItems = counters.Succeeded,
                FailedItems = counters.ProviderFailed + counters.PersistFailed + counters.RateLimitedSkipped,
                SkippedItems = counters.NoEurConversion + counters.StaleRejected,
                ResultSummary = $"Обработано {processed} из {total}; успешно {counters.Succeeded}; rate-limit {counters.RateLimited}.",
                ErrorSummary = error,
            },
            CancellationToken.None);
    }

    private async Task ProcessSingleStockWithRetriesAsync(
        string jobId,
        int marketIndexId,
        Stock stock,
        Counters counters,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(stock.Ticker))
        {
            counters.ProviderFailed++;
            return;
        }

        var retryAttempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StockQuoteFetchResult fetchResult;
            try
            {
                fetchResult = await ProcessSingleStockFetchAsync(stock, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Batch quote fetch exception: jobId={JobId} stockId={StockId} ticker={Ticker}",
                    jobId, stock.Id, stock.Ticker);
                counters.ProviderFailed++;
                return;
            }

            if (!fetchResult.IsRateLimited)
            {
                await ApplyFetchResultAsync(jobId, stock, fetchResult, counters, cancellationToken);
                return;
            }

            counters.RateLimited++;
            if (retryAttempt >= _options.MaxRateLimitRetries)
            {
                counters.RateLimitedSkipped++;
                _logger.LogWarning(
                    "Batch quote rate limit retries exhausted: jobId={JobId} indexId={IndexId} stockId={StockId} ticker={Ticker} retries={Retries}",
                    jobId, marketIndexId, stock.Id, stock.Ticker, retryAttempt);
                return;
            }

            retryAttempt++;
            counters.RateLimitRetries++;
            var retryDelay = SelectRetryDelay(fetchResult.RetryAfterDelay, retryAttempt);
            var nextRetryAtUtc = _timeProvider.GetUtcNow().Add(retryDelay).UtcDateTime;
            SetWaitingForRetry(jobId, true, nextRetryAtUtc);

            _logger.LogWarning(
                "Batch quote rate limited: jobId={JobId} indexId={IndexId} stockId={StockId} ticker={Ticker} retryAttempt={RetryAttempt}/{MaxRetries} retryAfterMs={DelayMs} providerRetryAfterMs={ProviderRetryAfterMs} nextRetryAtUtc={NextRetryAtUtc}",
                jobId,
                marketIndexId,
                stock.Id,
                stock.Ticker,
                retryAttempt,
                _options.MaxRateLimitRetries,
                (int)retryDelay.TotalMilliseconds,
                fetchResult.RetryAfterDelay.HasValue ? (int)fetchResult.RetryAfterDelay.Value.TotalMilliseconds : 0,
                nextRetryAtUtc);

            try
            {
                await DelayIfNeededAsync(retryDelay, cancellationToken);
            }
            finally
            {
                SetWaitingForRetry(jobId, false, null);
            }
        }
    }

    private async Task<StockQuoteFetchResult> ProcessSingleStockFetchAsync(Stock stock, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var quoteFetchService = scope.ServiceProvider.GetRequiredService<IStockQuoteFetchService>();
        return await quoteFetchService.FetchAsync(
            stock.Ticker,
            stock.Exchange ?? string.Empty,
            stock.FinanzenNetSlug,
            cancellationToken);
    }

    private async Task ApplyFetchResultAsync(
        string jobId,
        Stock stock,
        StockQuoteFetchResult fetchResult,
        Counters counters,
        CancellationToken cancellationToken)
    {
        if (!fetchResult.IsSuccess || fetchResult.Quote is null)
        {
            _logger.LogDebug(
                "Batch quote fetch failed: jobId={JobId} stockId={StockId} ticker={Ticker} status={Status}",
                jobId, stock.Id, stock.Ticker, fetchResult.StatusCode);
            counters.ProviderFailed++;
            return;
        }

        var quote = fetchResult.Quote;
        var isDelayed = quote.IsStale || !string.IsNullOrWhiteSpace(quote.DelayWarning);

        if (quote.CurrentPriceEur is null)
        {
            counters.NoEurConversion++;
            return;
        }

        var incomingPrice = Math.Round(quote.CurrentPriceEur.Value, 2);
        var incomingChange = quote.ChangeEur.HasValue ? Math.Round(quote.ChangeEur.Value, 4) : (decimal?)null;
        var incomingPercent = Math.Round(quote.PercentChange, 4);
        var incomingAt = quote.PriceTimestampUtc;

        try
        {
            using var persistScope = _scopeFactory.CreateScope();
            var quoteSnapshotPersistenceService = persistScope.ServiceProvider.GetRequiredService<StockQuoteSnapshotPersistenceService>();
            var persistenceResult = await quoteSnapshotPersistenceService.ApplyAsync(
                stock.Id,
                new PersistStockQuoteSnapshotRequest
                {
                    CurrentPrice = incomingPrice,
                    CurrentPriceChange = incomingChange,
                    CurrentPriceChangePercent = incomingPercent,
                    CurrentPriceAt = incomingAt,
                    CurrentPriceIsDelayed = isDelayed,
                    CurrentPriceDelayWarning = quote.DelayWarning,
                    QuoteCurrency = "EUR",
                    FinancialCurrency = "EUR",
                    NormalizedQuoteCurrency = "EUR",
                    QuoteUnitMultiplier = 1m,
                },
                cancellationToken);

            if (!persistenceResult.StockFound)
            {
                counters.PersistFailed++;
                return;
            }

            if (isDelayed)
            {
                counters.Delayed++;
            }

            if (!persistenceResult.Applied)
            {
                counters.StaleRejected++;

                _logger.LogDebug(
                    "Batch quote: snapshot rejected for stockId={StockId} ticker={Ticker} reason={Reason}",
                    stock.Id,
                    stock.Ticker,
                    persistenceResult.Reason);

                return;
            }

            counters.Succeeded++;

            _logger.LogDebug(
                "Batch quote: persisted stockId={StockId} ticker={Ticker} priceEur={Price}",
                stock.Id, stock.Ticker, incomingPrice);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Batch quote persist failed: jobId={JobId} stockId={StockId} ticker={Ticker}",
                jobId, stock.Id, stock.Ticker);
            counters.PersistFailed++;
        }
    }

    private TimeSpan SelectRetryDelay(TimeSpan? providerRetryAfterDelay, int retryAttempt)
    {
        if (providerRetryAfterDelay is { } providerDelay && providerDelay > TimeSpan.Zero)
        {
            return providerDelay > _options.MaxAcceptedRetryAfter ? _options.MaxAcceptedRetryAfter : providerDelay;
        }

        var initialMs = _options.InitialRateLimitBackoff.TotalMilliseconds;
        var maxMs = Math.Max(initialMs, _options.MaxRateLimitBackoff.TotalMilliseconds);
        var delayMs = Math.Min(initialMs * Math.Pow(2, retryAttempt - 1), maxMs);
        return TimeSpan.FromMilliseconds(delayMs);
    }

    private async Task DelayIfNeededAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        await _delayAsync(delay, cancellationToken);
    }

    private bool TryMarkRunning(string jobId)
    {
        lock (_sync)
        {
            if (!_jobs.TryGetValue(jobId, out var entry)) return false;
            if (entry.State != IndexConstituentsBatchQuoteRefreshJobState.Queued) return false;
            entry.State = IndexConstituentsBatchQuoteRefreshJobState.Running;
            entry.StartedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
            return true;
        }
    }

    private void SetTotal(string jobId, int total)
    {
        lock (_sync)
        {
            if (_jobs.TryGetValue(jobId, out var entry))
            {
                entry.Total = total;
            }
        }
    }

    private void UpdateProgress(string jobId, int processed, Counters counters)
    {
        lock (_sync)
        {
            if (_jobs.TryGetValue(jobId, out var entry))
            {
                entry.Processed = processed;
                ApplyCounters(entry, counters);
            }
        }
    }

    private void SetWaitingForRetry(string jobId, bool isWaitingForRetry, DateTime? nextRetryAtUtc)
    {
        lock (_sync)
        {
            if (_jobs.TryGetValue(jobId, out var entry))
            {
                entry.IsWaitingForRetry = isWaitingForRetry;
                entry.NextRetryAtUtc = nextRetryAtUtc;
            }
        }
    }

    private void MarkCompleted(
        string jobId,
        IndexConstituentsBatchQuoteRefreshJobState state,
        int processed = 0,
        int total = 0,
        Counters? counters = null,
        string? error = null)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        lock (_sync)
        {
            if (!_jobs.TryGetValue(jobId, out var entry)) return;

            entry.State = state;
            entry.Processed = processed > 0 ? processed : entry.Processed;
            if (total > 0) entry.Total = total;
            if (counters != null) ApplyCounters(entry, counters);
            entry.Error = ToSafeError(error);
            entry.IsWaitingForRetry = false;
            entry.NextRetryAtUtc = null;
            entry.CompletedAtUtc = now;
            entry.ExpiresAtUtc = now.Add(_options.CompletedJobTtl);

            if (_activeJobsByIndexId.TryGetValue(entry.MarketIndexId, out var currentJobId)
                && string.Equals(currentJobId, entry.JobId, StringComparison.Ordinal))
            {
                _activeJobsByIndexId.Remove(entry.MarketIndexId);
            }

            CleanupLocked(now);
        }
    }

    private static void ApplyCounters(JobEntry entry, Counters counters)
    {
        entry.Succeeded = counters.Succeeded;
        entry.Delayed = counters.Delayed;
        entry.NoEurConversion = counters.NoEurConversion;
        entry.StaleRejected = counters.StaleRejected;
        entry.ProviderFailed = counters.ProviderFailed;
        entry.PersistFailed = counters.PersistFailed;
        entry.RateLimited = counters.RateLimited;
        entry.RateLimitRetries = counters.RateLimitRetries;
        entry.RateLimitedSkipped = counters.RateLimitedSkipped;
    }

    private void InterruptActiveJobs(string error)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        lock (_sync)
        {
            foreach (var entry in _jobs.Values.Where(x =>
                         x.State is IndexConstituentsBatchQuoteRefreshJobState.Queued
                             or IndexConstituentsBatchQuoteRefreshJobState.Running))
            {
                entry.State = IndexConstituentsBatchQuoteRefreshJobState.Interrupted;
                entry.Error = ToSafeError(error);
                entry.IsWaitingForRetry = false;
                entry.NextRetryAtUtc = null;
                entry.CompletedAtUtc = now;
                entry.ExpiresAtUtc = now.Add(_options.CompletedJobTtl);
            }

            _activeJobsByIndexId.Clear();
            CleanupLocked(now);
        }
    }

    private void CleanupLocked(DateTime now)
    {
        foreach (var entry in _jobs.Values
                     .Where(x => x.ExpiresAtUtc.HasValue && x.ExpiresAtUtc.Value <= now)
                     .ToList())
        {
            RemoveJobLocked(entry.JobId);
        }

        while (_jobs.Count > _options.RegistryCapacity && TryEvictOneTerminalJobLocked())
        {
        }
    }

    private bool TryEvictOneTerminalJobLocked()
    {
        var candidate = _jobs.Values
            .Where(x => x.ExpiresAtUtc.HasValue)
            .OrderBy(x => x.CompletedAtUtc ?? x.CreatedAtUtc)
            .FirstOrDefault();
        if (candidate is null) return false;
        RemoveJobLocked(candidate.JobId);
        return true;
    }

    private void RemoveJobLocked(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var entry)) return;
        _jobs.Remove(jobId);
        if (_activeJobsByIndexId.TryGetValue(entry.MarketIndexId, out var cur)
            && string.Equals(cur, jobId, StringComparison.Ordinal))
        {
            _activeJobsByIndexId.Remove(entry.MarketIndexId);
        }
    }

    private string? ToSafeError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return null;
        var t = error.Trim();
        return t.Length <= _options.MaxErrorMessageLength ? t : t[.._options.MaxErrorMessageLength];
    }

    private IndexConstituentsBatchQuoteRefreshJobResponse ToResponse(JobEntry entry, bool reusedActiveJob)
        => new()
        {
            JobId = entry.JobId,
            MarketIndexId = entry.MarketIndexId,
            State = entry.State,
            ReusedActiveJob = reusedActiveJob,
            CreatedAtUtc = entry.CreatedAtUtc,
            StartedAtUtc = entry.StartedAtUtc,
            CompletedAtUtc = entry.CompletedAtUtc,
            ExpiresAtUtc = entry.ExpiresAtUtc,
            Total = entry.Total,
            Processed = entry.Processed,
            Remaining = Math.Max(0, entry.Total - entry.Processed),
            Succeeded = entry.Succeeded,
            Delayed = entry.Delayed,
            NoEurConversion = entry.NoEurConversion,
            StaleRejected = entry.StaleRejected,
            ProviderFailed = entry.ProviderFailed,
            PersistFailed = entry.PersistFailed,
            RateLimited = entry.RateLimited,
            RateLimitRetries = entry.RateLimitRetries,
            RateLimitedSkipped = entry.RateLimitedSkipped,
            IsWaitingForRetry = entry.IsWaitingForRetry,
            NextRetryAtUtc = entry.NextRetryAtUtc,
            Error = entry.Error,
        };

    private sealed class JobEntry
    {
        public required string JobId { get; init; }
        public required int MarketIndexId { get; init; }
        public required DateTime CreatedAtUtc { get; init; }
        public IndexConstituentsBatchQuoteRefreshJobState State { get; set; }
        public DateTime? StartedAtUtc { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
        public DateTime? ExpiresAtUtc { get; set; }
        public int Total { get; set; }
        public int Processed { get; set; }
        public int Succeeded { get; set; }
        public int Delayed { get; set; }
        public int NoEurConversion { get; set; }
        public int StaleRejected { get; set; }
        public int ProviderFailed { get; set; }
        public int PersistFailed { get; set; }
        public int RateLimited { get; set; }
        public int RateLimitRetries { get; set; }
        public int RateLimitedSkipped { get; set; }
        public bool IsWaitingForRetry { get; set; }
        public DateTime? NextRetryAtUtc { get; set; }
        public string? Error { get; set; }
    }

    private sealed class Counters
    {
        public int Succeeded { get; set; }
        public int Delayed { get; set; }
        public int NoEurConversion { get; set; }
        public int StaleRejected { get; set; }
        public int ProviderFailed { get; set; }
        public int PersistFailed { get; set; }
        public int RateLimited { get; set; }
        public int RateLimitRetries { get; set; }
        public int RateLimitedSkipped { get; set; }
    }
}
