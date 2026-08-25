using FinanceApp.API.Models;
using FinanceApp.Core.Models;
using FinanceApp.Data.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinanceApp.API.Services;

public sealed class CatalogFundamentalsRefreshJobOptions
{
    public bool Enabled { get; init; } = true;
    public DayOfWeek Weekday { get; init; } = DayOfWeek.Sunday;
    public TimeSpan LocalScheduleTime { get; init; } = new(2, 30, 0);
    public string TimeZoneId { get; init; } = "Europe/Berlin";
    public bool RunCatchUpOnStartup { get; init; } = true;
    public TimeSpan FreshnessThreshold { get; init; } = TimeSpan.FromDays(7);
    public int BatchSize { get; init; } = 40;
    public int MaxConcurrency { get; init; } = 1;
    public TimeSpan InterRequestDelay { get; init; } = TimeSpan.FromMilliseconds(250);
    public int RetryLimit { get; init; } = 2;
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan ProviderRateLimitCooldown { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan LeaseRenewInterval { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan SharedLeaseRetryDelay { get; init; } = TimeSpan.FromSeconds(30);
    public int ProgressLogEveryStocks { get; init; } = 25;
}

public interface ICatalogFundamentalsRefreshStatusService
{
    Task<CatalogFundamentalsRefreshStatusResponse> GetStatusAsync(CancellationToken cancellationToken = default);
}

public sealed class CatalogFundamentalsRefreshHostedService : BackgroundService, ICatalogFundamentalsRefreshStatusService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly CatalogFundamentalsRefreshJobOptions _options;
    private readonly ILogger<CatalogFundamentalsRefreshHostedService> _logger;
    private readonly ICatalogMaintenanceLeaseService _maintenanceLeaseService;
    private readonly ISystemProcessJournalService? _processJournalService;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly string _instanceId = $"{Environment.MachineName}-{Guid.NewGuid():N}";

    public CatalogFundamentalsRefreshHostedService(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        IOptions<CatalogFundamentalsRefreshJobOptions> options,
        ILogger<CatalogFundamentalsRefreshHostedService> logger,
        ICatalogMaintenanceLeaseService maintenanceLeaseService,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        ISystemProcessJournalService? processJournalService = null)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
        _maintenanceLeaseService = maintenanceLeaseService;
        _processJournalService = processJournalService;
        _delayAsync = delayAsync ?? ((delay, cancellationToken) => Task.Delay(delay, cancellationToken));

        var raw = options.Value;
        _options = new CatalogFundamentalsRefreshJobOptions
        {
            Enabled = raw.Enabled,
            Weekday = raw.Weekday,
            LocalScheduleTime = raw.LocalScheduleTime >= TimeSpan.Zero && raw.LocalScheduleTime < TimeSpan.FromDays(1)
                ? raw.LocalScheduleTime
                : new TimeSpan(2, 30, 0),
            TimeZoneId = string.IsNullOrWhiteSpace(raw.TimeZoneId) ? "Europe/Berlin" : raw.TimeZoneId.Trim(),
            RunCatchUpOnStartup = raw.RunCatchUpOnStartup,
            FreshnessThreshold = raw.FreshnessThreshold > TimeSpan.Zero ? raw.FreshnessThreshold : TimeSpan.FromDays(7),
            BatchSize = raw.BatchSize > 0 ? raw.BatchSize : 40,
            MaxConcurrency = raw.MaxConcurrency > 0 ? raw.MaxConcurrency : 1,
            InterRequestDelay = raw.InterRequestDelay >= TimeSpan.Zero ? raw.InterRequestDelay : TimeSpan.FromMilliseconds(250),
            RetryLimit = raw.RetryLimit >= 0 ? raw.RetryLimit : 2,
            RetryBaseDelay = raw.RetryBaseDelay > TimeSpan.Zero ? raw.RetryBaseDelay : TimeSpan.FromSeconds(2),
            ProviderRateLimitCooldown = raw.ProviderRateLimitCooldown > TimeSpan.Zero ? raw.ProviderRateLimitCooldown : TimeSpan.FromMinutes(2),
            LeaseDuration = raw.LeaseDuration > TimeSpan.Zero ? raw.LeaseDuration : TimeSpan.FromMinutes(10),
            LeaseRenewInterval = raw.LeaseRenewInterval > TimeSpan.Zero ? raw.LeaseRenewInterval : TimeSpan.FromMinutes(2),
            SharedLeaseRetryDelay = raw.SharedLeaseRetryDelay > TimeSpan.Zero ? raw.SharedLeaseRetryDelay : TimeSpan.FromSeconds(30),
            ProgressLogEveryStocks = raw.ProgressLogEveryStocks > 0 ? raw.ProgressLogEveryStocks : 25
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var startupCheckCompleted = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_options.Enabled)
            {
                startupCheckCompleted = true;
                await _delayAsync(TimeSpan.FromMinutes(5), stoppingToken);
                continue;
            }

            var timeZone = ResolveTimeZone();
            var nowUtc = _timeProvider.GetUtcNow();
            var schedule = CatalogStockRefreshScheduleCalculator.WeeklySnapshot(nowUtc, timeZone, _options.Weekday, _options.LocalScheduleTime);

            if (!startupCheckCompleted)
            {
                startupCheckCompleted = true;
                if (_options.RunCatchUpOnStartup && nowUtc >= schedule.CurrentWeekScheduledRunUtc)
                {
                    await RunUntilNotDeferredAsync(
                        schedule.CurrentWeekScheduledDate,
                        schedule.CurrentWeekScheduledRunUtc.UtcDateTime,
                        "startup-catch-up",
                        stoppingToken);
                }
            }

            var delay = schedule.NextScheduledRunUtc - _timeProvider.GetUtcNow();
            if (delay < TimeSpan.Zero)
            {
                delay = TimeSpan.Zero;
            }

            await _delayAsync(delay, stoppingToken);
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            var runSchedule = CatalogStockRefreshScheduleCalculator.WeeklySnapshot(
                _timeProvider.GetUtcNow(),
                timeZone,
                _options.Weekday,
                _options.LocalScheduleTime);
            await RunUntilNotDeferredAsync(
                runSchedule.CurrentWeekScheduledDate,
                runSchedule.CurrentWeekScheduledRunUtc.UtcDateTime,
                "scheduled",
                stoppingToken);
        }
    }

    internal Task TriggerRunAsync(
        DateOnly businessWeek,
        DateTime scheduledAtUtc,
        string trigger,
        CancellationToken cancellationToken = default)
        => RunUntilNotDeferredAsync(businessWeek, scheduledAtUtc, trigger, cancellationToken);

    public async Task<CatalogFundamentalsRefreshStatusResponse> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        var timeZone = ResolveTimeZone();
        var schedule = CatalogStockRefreshScheduleCalculator.WeeklySnapshot(nowUtc, timeZone, _options.Weekday, _options.LocalScheduleTime);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var latest = await db.CatalogFundamentalsRefreshRuns
            .AsNoTracking()
            .OrderByDescending(x => x.BusinessWeek)
            .ThenByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        var recentFailures = await db.CatalogFundamentalsRefreshRuns
            .AsNoTracking()
            .Where(x => x.LastError != null && x.LastError != string.Empty)
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Select(x => x.LastError!)
            .Take(10)
            .ToListAsync(cancellationToken);

        return new CatalogFundamentalsRefreshStatusResponse
        {
            GeneratedAtUtc = nowUtc.UtcDateTime,
            Enabled = _options.Enabled,
            TimeZoneId = timeZone.Id,
            ScheduledWeekday = _options.Weekday,
            LocalScheduleTime = _options.LocalScheduleTime,
            NextScheduledRunUtc = schedule.NextScheduledRunUtc.UtcDateTime,
            CurrentOrLatestRun = latest is null ? null : MapRun(latest),
            RecentFailures = recentFailures
        };
    }

    private async Task RunUntilNotDeferredAsync(
        DateOnly businessWeek,
        DateTime scheduledAtUtc,
        string trigger,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var outcome = await TryRunBusinessWeekAsync(businessWeek, scheduledAtUtc, trigger, cancellationToken);
            if (outcome != RunAttemptOutcome.DeferredForSharedLease)
            {
                return;
            }

            await _delayAsync(_options.SharedLeaseRetryDelay, cancellationToken);
        }
    }

    private async Task<RunAttemptOutcome> TryRunBusinessWeekAsync(
        DateOnly businessWeek,
        DateTime scheduledAtUtc,
        string trigger,
        CancellationToken cancellationToken)
    {
        if (!_runGate.Wait(0))
        {
            return RunAttemptOutcome.NotStarted;
        }

        var runId = 0;
        var hasRunLease = false;
        var hasSharedLease = false;
        var processRunId = 0L;
        try
        {
            var timeZone = ResolveTimeZone();
            var runKey = BuildRunKey(businessWeek, timeZone.Id);
            var run = await GetOrCreateRunAsync(runKey, businessWeek, timeZone.Id, scheduledAtUtc, cancellationToken);
            if (run is null)
            {
                return RunAttemptOutcome.NotStarted;
            }

            runId = run.Id;
            if (run.Status is CatalogFundamentalsRefreshRunStatus.Completed or CatalogFundamentalsRefreshRunStatus.CompletedWithErrors)
            {
                return RunAttemptOutcome.NotStarted;
            }

            processRunId = await (_processJournalService?.CreateOrGetAsync(new CreateSystemProcessRunRequest
            {
                ProcessType = SystemProcessTypes.CatalogFundamentalsRefresh,
                Trigger = trigger.Equals("scheduled", StringComparison.OrdinalIgnoreCase)
                    ? SystemProcessTrigger.Scheduled
                    : trigger.Equals("startup-catch-up", StringComparison.OrdinalIgnoreCase)
                        ? SystemProcessTrigger.StartupCatchUp
                        : SystemProcessTrigger.Automatic,
                ExternalRunKey = run.RunKey,
                CorrelationId = run.RunKey,
                InitialStatus = SystemProcessRunStatus.Pending,
                Details = new
                {
                    run.BusinessWeek,
                    run.TimeZoneId,
                    run.ScheduledAtUtc,
                    trigger,
                },
            }, cancellationToken) ?? Task.FromResult(0L));

            if (!await TryAcquireLeaseAsync(runId, cancellationToken))
            {
                return RunAttemptOutcome.NotStarted;
            }

            hasRunLease = true;
            if (!await _maintenanceLeaseService.TryAcquireAsync(
                    CatalogMaintenanceLeaseNames.AllCatalogDataRefresh,
                    _instanceId,
                    _options.LeaseDuration,
                    cancellationToken))
            {
                await ReleaseRunLeaseAsync(runId, cancellationToken);
                return RunAttemptOutcome.DeferredForSharedLease;
            }

            hasSharedLease = true;

            var startedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
            await UpdateRunAsync(runId, dbRun =>
            {
                dbRun.Status = CatalogFundamentalsRefreshRunStatus.Running;
                dbRun.StartedAtUtc ??= startedAtUtc;
                dbRun.UpdatedAtUtc = startedAtUtc;
            }, cancellationToken);
            if (processRunId > 0)
            {
                await _processJournalService!.MarkRunningAsync(processRunId, cancellationToken);
            }

            _logger.LogInformation(
                "Weekly catalog fundamentals refresh started: runKey={RunKey} businessWeek={BusinessWeek} trigger={Trigger} weekday={Weekday} localTime={LocalScheduleTime} timeZone={TimeZoneId}",
                runKey,
                businessWeek,
                trigger,
                _options.Weekday,
                _options.LocalScheduleTime,
                timeZone.Id);

            await ProcessRunAsync(runId, cancellationToken);
            await FinalizeRunAsync(runId, cancellationToken);
            if (processRunId > 0)
            {
                var final = await GetRunAsync(runId, cancellationToken);
                if (final is not null)
                {
                    await _processJournalService!.CompleteAsync(
                        processRunId,
                        final.Status == CatalogFundamentalsRefreshRunStatus.Completed
                            ? SystemProcessRunStatus.Succeeded
                            : SystemProcessRunStatus.CompletedWithErrors,
                        new UpdateSystemProcessRunRequest
                        {
                            TotalItems = final.TotalDiscovered,
                            ProcessedItems = final.Processed,
                            SucceededItems = final.Succeeded,
                            FailedItems = final.Failed + final.RateLimited,
                            SkippedItems = final.Skipped,
                            ResultSummary = $"Обработано {final.Processed} из {final.TotalDiscovered}; успешно {final.Succeeded}; ошибок {final.Failed}.",
                            ErrorSummary = final.FailureSummary ?? final.LastError,
                            LastProcessedEntity = final.LastProcessedStockId.HasValue ? $"stockId={final.LastProcessedStockId.Value}" : null,
                        },
                        cancellationToken);
                }
            }
            return RunAttemptOutcome.Completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (processRunId > 0)
            {
                await _processJournalService!.CompleteAsync(
                    processRunId,
                    SystemProcessRunStatus.Interrupted,
                    new UpdateSystemProcessRunRequest
                    {
                        ErrorSummary = "Недельное обновление фундаментальных данных прервано остановкой приложения.",
                    },
                    CancellationToken.None);
            }
            return RunAttemptOutcome.NotStarted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Weekly catalog fundamentals refresh failed for businessWeek={BusinessWeek}", businessWeek);
            if (runId != 0)
            {
                await UpdateRunAsync(runId, dbRun =>
                {
                    dbRun.Status = CatalogFundamentalsRefreshRunStatus.Failed;
                    AppendFailure(dbRun, ex.Message);
                    dbRun.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
                }, CancellationToken.None);
            }
            if (processRunId > 0)
            {
                await _processJournalService!.CompleteAsync(
                    processRunId,
                    SystemProcessRunStatus.Failed,
                    new UpdateSystemProcessRunRequest
                    {
                        ErrorSummary = ex.Message,
                    },
                    CancellationToken.None);
            }

            return RunAttemptOutcome.NotStarted;
        }
        finally
        {
            if (hasSharedLease)
            {
                await _maintenanceLeaseService.ReleaseAsync(
                    CatalogMaintenanceLeaseNames.AllCatalogDataRefresh,
                    _instanceId,
                    CancellationToken.None);
            }

            if (hasRunLease && runId != 0)
            {
                await ReleaseRunLeaseAsync(runId, CancellationToken.None);
            }

            _runGate.Release();
        }
    }

    private async Task ProcessRunAsync(int runId, CancellationToken cancellationToken)
    {
        var lastLeaseRenewedAtUtc = DateTime.MinValue;
        var lastSharedLeaseRenewedAtUtc = DateTime.MinValue;

        await EnsureTotalDiscoveredAsync(runId, cancellationToken);
        await TryProcessPendingStockAsync(runId, cancellationToken);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var runLease = await TryRenewLeaseIfNeededAsync(runId, lastLeaseRenewedAtUtc, cancellationToken);
            lastLeaseRenewedAtUtc = runLease.LastRenewedAtUtc;
            if (!runLease.Success)
            {
                await MarkLeaseLostAsync(runId, "Run lease was lost to another instance.", cancellationToken);
                return;
            }

            var sharedLease = await TryRenewSharedLeaseIfNeededAsync(lastSharedLeaseRenewedAtUtc, cancellationToken);
            lastSharedLeaseRenewedAtUtc = sharedLease.LastRenewedAtUtc;
            if (!sharedLease.Success)
            {
                await MarkLeaseLostAsync(runId, "Shared maintenance lease was lost to another instance.", cancellationToken);
                return;
            }

            var cursor = await GetLastProcessedStockIdAsync(runId, cancellationToken);
            var batch = await LoadBatchAsync(cursor, cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var stock in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await ProcessSingleStockAsync(runId, stock, cancellationToken);
                if (result.CooldownDelay > TimeSpan.Zero)
                {
                    await _delayAsync(result.CooldownDelay, cancellationToken);
                }
                else if (_options.InterRequestDelay > TimeSpan.Zero)
                {
                    await _delayAsync(_options.InterRequestDelay, cancellationToken);
                }
            }
        }
    }

    private async Task<List<RefreshStockCandidate>> LoadBatchAsync(int lastProcessedStockId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Stocks
            .AsNoTracking()
            .Where(s =>
                s.Id > lastProcessedStockId &&
                (s.TrackingStatus == StockTrackingStatus.Tracked || s.TrackingStatus == StockTrackingStatus.CatalogOnly))
            .OrderBy(s => s.Id)
            .Select(s => new RefreshStockCandidate(s.Id, s.Ticker, s.Exchange))
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);
    }

    private async Task<StockProcessResult> ProcessSingleStockAsync(int runId, RefreshStockCandidate stock, CancellationToken cancellationToken)
    {
        await UpdateRunAsync(runId, run =>
        {
            run.PendingStockId = stock.StockId;
            run.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        }, cancellationToken);

        var outcome = await ExecuteWithRetriesAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(stock.Ticker))
            {
                return StepOutcome.Skipped("Missing ticker.");
            }

            if (!StockExchanges.TryNormalize(stock.Exchange, out _))
            {
                return StepOutcome.Skipped("Invalid exchange.");
            }

            if (await IsFreshEnoughAsync(stock.StockId, cancellationToken))
            {
                return StepOutcome.Skipped("Fundamentals are fresh.");
            }

            await using var scope = _scopeFactory.CreateAsyncScope();
            var fundamentalsService = scope.ServiceProvider.GetRequiredService<IFundamentalsService>();
            var refreshed = await fundamentalsService.RefreshFundamentalsAsync(stock.StockId, cancellationToken);
            if (refreshed.State == FundamentalsState.Fresh)
            {
                return StepOutcome.Succeeded();
            }

            if (refreshed.FailureCategory == FundamentalsRefreshFailureCategory.ProviderRateLimited)
            {
                return StepOutcome.RateLimited(refreshed.WarningMessage);
            }

            return StepOutcome.Failed(refreshed.WarningMessage ?? "Fundamentals refresh failed.");
        }, cancellationToken);

        await ApplyResultAsync(runId, stock.StockId, outcome, cancellationToken);

        return new StockProcessResult(outcome.GetCooldown(_options.ProviderRateLimitCooldown));
    }

    private async Task TryProcessPendingStockAsync(int runId, CancellationToken cancellationToken)
    {
        var pendingStockId = await GetPendingStockIdAsync(runId, cancellationToken);
        if (!pendingStockId.HasValue)
        {
            return;
        }

        var stock = await LoadStockByIdAsync(pendingStockId.Value, cancellationToken);
        if (stock is null)
        {
            await ApplyResultAsync(runId, pendingStockId.Value, StepOutcome.Skipped("Stock no longer exists."), cancellationToken);
            return;
        }

        await ProcessSingleStockAsync(runId, stock.Value, cancellationToken);
    }

    private async Task<RefreshStockCandidate?> LoadStockByIdAsync(int stockId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stock = await db.Stocks
            .AsNoTracking()
            .Where(s => s.Id == stockId && (s.TrackingStatus == StockTrackingStatus.Tracked || s.TrackingStatus == StockTrackingStatus.CatalogOnly))
            .Select(s => (RefreshStockCandidate?)new RefreshStockCandidate(s.Id, s.Ticker, s.Exchange))
            .FirstOrDefaultAsync(cancellationToken);
        return stock;
    }

    private async Task<StepOutcome> ExecuteWithRetriesAsync(
        Func<Task<StepOutcome>> action,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StepOutcome outcome;
            try
            {
                outcome = await action();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                outcome = StepOutcome.Failed(ex.Message);
            }

            if (outcome.Kind is StepOutcomeKind.Succeeded or StepOutcomeKind.Skipped)
            {
                return outcome;
            }

            if (outcome.Kind == StepOutcomeKind.RateLimited)
            {
                return outcome;
            }

            if (attempt >= _options.RetryLimit)
            {
                return outcome;
            }

            attempt++;
            var baseDelay = _options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
            var jitterMs = Random.Shared.Next(0, 250);
            await _delayAsync(TimeSpan.FromMilliseconds(baseDelay + jitterMs), cancellationToken);
        }
    }

    private async Task<bool> IsFreshEnoughAsync(int stockId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var fetchedAt = await db.FundamentalsSnapshots
            .AsNoTracking()
            .Where(x => x.StockId == stockId)
            .Select(x => (DateTime?)x.FetchedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (!fetchedAt.HasValue)
        {
            return false;
        }

        return _timeProvider.GetUtcNow().UtcDateTime - fetchedAt.Value <= _options.FreshnessThreshold;
    }

    private async Task ApplyResultAsync(int runId, int stockId, StepOutcome outcome, CancellationToken cancellationToken)
    {
        await UpdateRunAsync(runId, run =>
        {
            switch (outcome.Kind)
            {
                case StepOutcomeKind.Succeeded:
                    run.Succeeded++;
                    break;
                case StepOutcomeKind.Skipped:
                    run.Skipped++;
                    if (!string.IsNullOrWhiteSpace(outcome.Reason))
                    {
                        AppendFailure(run, $"stockId={stockId} skipped: {outcome.Reason}");
                    }
                    break;
                case StepOutcomeKind.RateLimited:
                    run.RateLimited++;
                    run.Skipped++;
                    run.Status = CatalogFundamentalsRefreshRunStatus.PausedRateLimited;
                    AppendFailure(run, $"stockId={stockId} rate-limited");
                    break;
                case StepOutcomeKind.Failed:
                    run.Failed++;
                    AppendFailure(run, $"stockId={stockId} failed: {outcome.Reason}");
                    break;
            }

            run.LastProcessedStockId = stockId;
            run.PendingStockId = null;
            run.Processed++;
            run.Remaining = Math.Max(0, run.TotalDiscovered - run.Processed);
            run.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        }, cancellationToken);

        if (_options.ProgressLogEveryStocks > 0)
        {
            var projection = await GetRunProjectionAsync(runId, cancellationToken);
            if (projection.HasValue && projection.Value.Processed % _options.ProgressLogEveryStocks == 0)
            {
                var value = projection.Value;
                _logger.LogInformation(
                    "Weekly catalog fundamentals refresh progress: runKey={RunKey} processed={Processed}/{Total} succeeded={Succeeded} failed={Failed} skipped={Skipped} rateLimited={RateLimited}",
                    value.RunKey,
                    value.Processed,
                    value.TotalDiscovered,
                    value.Succeeded,
                    value.Failed,
                    value.Skipped,
                    value.RateLimited);
            }
        }
    }

    private static void AppendFailure(CatalogFundamentalsRefreshRun run, string message)
    {
        var safe = message.Trim();
        if (safe.Length > 400)
        {
            safe = safe[..400];
        }

        run.LastError = safe;
        var current = string.IsNullOrWhiteSpace(run.FailureSummary) ? string.Empty : run.FailureSummary + Environment.NewLine;
        var combined = current + safe;
        if (combined.Length > 4000)
        {
            combined = combined[^4000..];
        }

        run.FailureSummary = combined;
    }

    private async Task EnsureTotalDiscoveredAsync(int runId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.CatalogFundamentalsRefreshRuns.FirstAsync(x => x.Id == runId, cancellationToken);
        if (run.TotalDiscovered > 0)
        {
            return;
        }

        run.TotalDiscovered = await db.Stocks
            .Where(s => s.TrackingStatus == StockTrackingStatus.Tracked || s.TrackingStatus == StockTrackingStatus.CatalogOnly)
            .CountAsync(cancellationToken);
        run.Remaining = Math.Max(0, run.TotalDiscovered - run.Processed);
        run.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<int> GetLastProcessedStockIdAsync(int runId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.CatalogFundamentalsRefreshRuns
            .AsNoTracking()
            .Where(x => x.Id == runId)
            .Select(x => new { x.LastProcessedStockId })
            .FirstAsync(cancellationToken);
        return run.LastProcessedStockId ?? 0;
    }

    private async Task<int?> GetPendingStockIdAsync(int runId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.CatalogFundamentalsRefreshRuns
            .AsNoTracking()
            .Where(x => x.Id == runId)
            .Select(x => x.PendingStockId)
            .FirstAsync(cancellationToken);
    }

    private async Task<RunProjection?> GetRunProjectionAsync(int runId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.CatalogFundamentalsRefreshRuns
            .AsNoTracking()
            .Where(x => x.Id == runId)
            .Select(x => new RunProjection(
                x.RunKey,
                x.Processed,
                x.TotalDiscovered,
                x.Succeeded,
                x.Failed,
                x.Skipped,
                x.RateLimited))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<bool> TryAcquireLeaseAsync(int runId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var leaseUntil = now.Add(_options.LeaseDuration);

        if (!db.Database.IsRelational())
        {
            var run = await db.CatalogFundamentalsRefreshRuns.FirstOrDefaultAsync(x => x.Id == runId, cancellationToken);
            if (run is null)
            {
                return false;
            }

            var leaseAvailable = string.IsNullOrWhiteSpace(run.LeaseOwner)
                                 || string.Equals(run.LeaseOwner, _instanceId, StringComparison.Ordinal)
                                 || !run.LeaseExpiresAtUtc.HasValue
                                 || run.LeaseExpiresAtUtc.Value < now;
            if (!leaseAvailable)
            {
                return false;
            }

            run.LeaseOwner = _instanceId;
            run.LeaseExpiresAtUtc = leaseUntil;
            run.UpdatedAtUtc = now;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE CatalogFundamentalsRefreshRuns
            SET LeaseOwner = {_instanceId},
                LeaseExpiresAtUtc = {leaseUntil},
                UpdatedAtUtc = {now}
            WHERE Id = {runId}
              AND (LeaseOwner IS NULL OR LeaseOwner = {_instanceId} OR LeaseExpiresAtUtc IS NULL OR LeaseExpiresAtUtc < {now})
            """, cancellationToken);
        return affected == 1;
    }

    private async Task ReleaseRunLeaseAsync(int runId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        if (!db.Database.IsRelational())
        {
            var run = await db.CatalogFundamentalsRefreshRuns.FirstOrDefaultAsync(x => x.Id == runId, cancellationToken);
            if (run is null || !string.Equals(run.LeaseOwner, _instanceId, StringComparison.Ordinal))
            {
                return;
            }

            run.LeaseOwner = null;
            run.LeaseExpiresAtUtc = null;
            run.UpdatedAtUtc = now;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE CatalogFundamentalsRefreshRuns
            SET LeaseOwner = NULL,
                LeaseExpiresAtUtc = NULL,
                UpdatedAtUtc = {now}
            WHERE Id = {runId}
              AND LeaseOwner = {_instanceId}
            """, cancellationToken);
    }

    private async Task<(bool Success, DateTime LastRenewedAtUtc)> TryRenewLeaseIfNeededAsync(
        int runId,
        DateTime lastLeaseRenewedAtUtc,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (lastLeaseRenewedAtUtc != DateTime.MinValue && now - lastLeaseRenewedAtUtc < _options.LeaseRenewInterval)
        {
            return (true, lastLeaseRenewedAtUtc);
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var leaseUntil = now.Add(_options.LeaseDuration);

        if (!db.Database.IsRelational())
        {
            var run = await db.CatalogFundamentalsRefreshRuns.FirstOrDefaultAsync(x => x.Id == runId, cancellationToken);
            if (run is null || !string.Equals(run.LeaseOwner, _instanceId, StringComparison.Ordinal))
            {
                return (false, lastLeaseRenewedAtUtc);
            }

            run.LeaseExpiresAtUtc = leaseUntil;
            run.UpdatedAtUtc = now;
            await db.SaveChangesAsync(cancellationToken);
            return (true, now);
        }

        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE CatalogFundamentalsRefreshRuns
            SET LeaseExpiresAtUtc = {leaseUntil},
                UpdatedAtUtc = {now}
            WHERE Id = {runId}
              AND LeaseOwner = {_instanceId}
            """, cancellationToken);
        return affected == 1 ? (true, now) : (false, lastLeaseRenewedAtUtc);
    }

    private async Task<(bool Success, DateTime LastRenewedAtUtc)> TryRenewSharedLeaseIfNeededAsync(
        DateTime lastLeaseRenewedAtUtc,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (lastLeaseRenewedAtUtc != DateTime.MinValue && now - lastLeaseRenewedAtUtc < _options.LeaseRenewInterval)
        {
            return (true, lastLeaseRenewedAtUtc);
        }

        var renewed = await _maintenanceLeaseService.TryRenewAsync(
            CatalogMaintenanceLeaseNames.AllCatalogDataRefresh,
            _instanceId,
            _options.LeaseDuration,
            cancellationToken);
        return renewed ? (true, now) : (false, lastLeaseRenewedAtUtc);
    }

    private async Task MarkLeaseLostAsync(int runId, string reason, CancellationToken cancellationToken)
    {
        await UpdateRunAsync(runId, run =>
        {
            run.Status = CatalogFundamentalsRefreshRunStatus.Failed;
            AppendFailure(run, reason);
            run.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        }, cancellationToken);
    }

    private async Task FinalizeRunAsync(int runId, CancellationToken cancellationToken)
    {
        await UpdateRunAsync(runId, run =>
        {
            run.CompletedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
            run.PendingStockId = null;
            run.Remaining = Math.Max(0, run.TotalDiscovered - run.Processed);
            run.Status = run.Failed > 0 || run.RateLimited > 0
                ? CatalogFundamentalsRefreshRunStatus.CompletedWithErrors
                : CatalogFundamentalsRefreshRunStatus.Completed;
            run.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        }, cancellationToken);

        var final = await GetRunAsync(runId, cancellationToken);
        _logger.LogInformation(
            "Weekly catalog fundamentals refresh completed: runKey={RunKey} status={Status} total={Total} processed={Processed} succeeded={Succeeded} failed={Failed} skipped={Skipped} rateLimited={RateLimited}",
            final?.RunKey,
            final?.Status,
            final?.TotalDiscovered,
            final?.Processed,
            final?.Succeeded,
            final?.Failed,
            final?.Skipped,
            final?.RateLimited);
    }

    private async Task<CatalogFundamentalsRefreshRun?> GetRunAsync(int runId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.CatalogFundamentalsRefreshRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == runId, cancellationToken);
    }

    private async Task<CatalogFundamentalsRefreshRun?> GetOrCreateRunAsync(
        string runKey,
        DateOnly businessWeek,
        string timeZoneId,
        DateTime scheduledAtUtc,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var existing = await db.CatalogFundamentalsRefreshRuns.FirstOrDefaultAsync(x => x.RunKey == runKey, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var run = new CatalogFundamentalsRefreshRun
        {
            RunKey = runKey,
            BusinessWeek = businessWeek,
            TimeZoneId = timeZoneId,
            ScheduledAtUtc = scheduledAtUtc,
            Status = CatalogFundamentalsRefreshRunStatus.Pending,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        db.CatalogFundamentalsRefreshRuns.Add(run);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return run;
        }
        catch (DbUpdateException)
        {
            return await db.CatalogFundamentalsRefreshRuns.FirstOrDefaultAsync(x => x.RunKey == runKey, cancellationToken);
        }
    }

    private async Task UpdateRunAsync(int runId, Action<CatalogFundamentalsRefreshRun> update, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.CatalogFundamentalsRefreshRuns.FirstAsync(x => x.Id == runId, cancellationToken);
        update(run);
        await db.SaveChangesAsync(cancellationToken);
    }

    private TimeZoneInfo ResolveTimeZone()
    {
        var configuredId = _options.TimeZoneId;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(configuredId);
        }
        catch (TimeZoneNotFoundException)
        {
            _logger.LogWarning("Configured timezone {TimeZoneId} not found. Falling back to Europe/Berlin.", configuredId);
        }
        catch (InvalidTimeZoneException)
        {
            _logger.LogWarning("Configured timezone {TimeZoneId} is invalid. Falling back to Europe/Berlin.", configuredId);
        }

        return TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    }

    private static string BuildRunKey(DateOnly businessWeek, string timeZoneId)
        => $"{timeZoneId}:{businessWeek:yyyy-MM-dd}";

    private static CatalogFundamentalsRefreshRunDetails MapRun(CatalogFundamentalsRefreshRun run)
        => new()
        {
            RunKey = run.RunKey,
            BusinessWeek = run.BusinessWeek,
            Status = run.Status,
            ScheduledAtUtc = run.ScheduledAtUtc,
            StartedAtUtc = run.StartedAtUtc,
            CompletedAtUtc = run.CompletedAtUtc,
            LastProcessedStockId = run.LastProcessedStockId ?? 0,
            TotalDiscovered = run.TotalDiscovered,
            Processed = run.Processed,
            Remaining = run.Remaining,
            Succeeded = run.Succeeded,
            Failed = run.Failed,
            Skipped = run.Skipped,
            RateLimited = run.RateLimited,
            LastError = run.LastError,
            FailureSummary = run.FailureSummary
        };

    private enum RunAttemptOutcome
    {
        NotStarted,
        DeferredForSharedLease,
        Completed
    }

    private readonly record struct RefreshStockCandidate(int StockId, string Ticker, string Exchange);
    private readonly record struct StockProcessResult(TimeSpan CooldownDelay);
    private readonly record struct RunProjection(string RunKey, int Processed, int TotalDiscovered, int Succeeded, int Failed, int Skipped, int RateLimited);

    private enum StepOutcomeKind
    {
        Succeeded,
        Failed,
        Skipped,
        RateLimited
    }

    private readonly record struct StepOutcome(StepOutcomeKind Kind, string? Reason)
    {
        public static StepOutcome Succeeded() => new(StepOutcomeKind.Succeeded, null);
        public static StepOutcome Failed(string? reason) => new(StepOutcomeKind.Failed, reason);
        public static StepOutcome Skipped(string? reason) => new(StepOutcomeKind.Skipped, reason);
        public static StepOutcome RateLimited(string? reason) => new(StepOutcomeKind.RateLimited, reason);

        public TimeSpan GetCooldown(TimeSpan defaultDelay) => Kind == StepOutcomeKind.RateLimited ? defaultDelay : TimeSpan.Zero;
    }
}
