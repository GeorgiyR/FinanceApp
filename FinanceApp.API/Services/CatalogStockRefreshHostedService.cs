using FinanceApp.API.Models;
using FinanceApp.Core.Models;
using FinanceApp.Data.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinanceApp.API.Services;

public sealed class CatalogStockRefreshJobOptions
{
    public bool Enabled { get; init; } = true;
    public bool RunCatchUpOnStartup { get; init; } = true;
    public string TimeZoneId { get; init; } = "Europe/Berlin";
    public TimeSpan LocalScheduleTime { get; init; } = new(22, 30, 0);
    public int BatchSize { get; init; } = 40;
    public int MaxConcurrency { get; init; } = 1;
    public TimeSpan InterRequestDelay { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan RateLimitCooldown { get; init; } = TimeSpan.FromMinutes(2);
    public int RetryLimit { get; init; } = 2;
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan LeaseRenewInterval { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan SharedLeaseRetryDelay { get; init; } = TimeSpan.FromSeconds(30);
    public int ProgressLogEveryStocks { get; init; } = 25;
}

public interface ICatalogStockRefreshStatusService
{
    Task<CatalogStockRefreshStatusResponse> GetStatusAsync(CancellationToken cancellationToken = default);
}

public static class CatalogStockRefreshScheduleCalculator
{
    public static CatalogStockRefreshScheduleSnapshot Snapshot(DateTimeOffset nowUtc, TimeZoneInfo timeZone, TimeSpan localScheduleTime)
    {
        if (localScheduleTime < TimeSpan.Zero || localScheduleTime >= TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(localScheduleTime), "Local schedule time must be within [00:00, 24:00).");
        }

        var localNow = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
        var businessDate = DateOnly.FromDateTime(localNow.DateTime);
        var todayScheduledUtc = ToUtc(businessDate, localScheduleTime, timeZone);
        var nextScheduledUtc = nowUtc < todayScheduledUtc
            ? todayScheduledUtc
            : ToUtc(businessDate.AddDays(1), localScheduleTime, timeZone);

        return new CatalogStockRefreshScheduleSnapshot(businessDate, localNow, todayScheduledUtc, nextScheduledUtc);
    }

    public static DateTimeOffset ToUtc(DateOnly businessDate, TimeSpan localScheduleTime, TimeZoneInfo timeZone)
    {
        var localDateTime = businessDate.ToDateTime(TimeOnly.FromTimeSpan(localScheduleTime), DateTimeKind.Unspecified);
        while (timeZone.IsInvalidTime(localDateTime))
        {
            localDateTime = localDateTime.AddMinutes(1);
        }

        TimeSpan offset;
        if (timeZone.IsAmbiguousTime(localDateTime))
        {
            offset = timeZone.GetAmbiguousTimeOffsets(localDateTime).Max();
        }
        else
        {
            offset = timeZone.GetUtcOffset(localDateTime);
        }

        return new DateTimeOffset(localDateTime, offset).ToUniversalTime();
    }

    public static CatalogStockWeeklyScheduleSnapshot WeeklySnapshot(
        DateTimeOffset nowUtc,
        TimeZoneInfo timeZone,
        DayOfWeek scheduledWeekday,
        TimeSpan localScheduleTime)
    {
        if (localScheduleTime < TimeSpan.Zero || localScheduleTime >= TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(localScheduleTime), "Local schedule time must be within [00:00, 24:00).");
        }

        var localNow = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
        var localDate = DateOnly.FromDateTime(localNow.DateTime);
        var currentWeekScheduledDate = GetScheduledDateForWeek(localDate, scheduledWeekday);
        var currentWeekScheduledRunUtc = ToUtc(currentWeekScheduledDate, localScheduleTime, timeZone);
        var nextScheduledRunUtc = nowUtc < currentWeekScheduledRunUtc
            ? currentWeekScheduledRunUtc
            : ToUtc(currentWeekScheduledDate.AddDays(7), localScheduleTime, timeZone);

        return new CatalogStockWeeklyScheduleSnapshot(
            localNow,
            currentWeekScheduledDate,
            currentWeekScheduledRunUtc,
            nextScheduledRunUtc);
    }

    private static DateOnly GetScheduledDateForWeek(DateOnly localDate, DayOfWeek scheduledWeekday)
    {
        var dayDiff = ((int)localDate.DayOfWeek - (int)scheduledWeekday + 7) % 7;
        return localDate.AddDays(-dayDiff);
    }
}

public sealed record CatalogStockRefreshScheduleSnapshot(
    DateOnly BusinessDate,
    DateTimeOffset LocalNow,
    DateTimeOffset TodayScheduledRunUtc,
    DateTimeOffset NextScheduledRunUtc);

public sealed record CatalogStockWeeklyScheduleSnapshot(
    DateTimeOffset LocalNow,
    DateOnly CurrentWeekScheduledDate,
    DateTimeOffset CurrentWeekScheduledRunUtc,
    DateTimeOffset NextScheduledRunUtc);

public sealed class CatalogStockRefreshHostedService : BackgroundService, ICatalogStockRefreshStatusService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly CatalogStockRefreshJobOptions _options;
    private readonly ILogger<CatalogStockRefreshHostedService> _logger;
    private readonly ICatalogMaintenanceLeaseService _maintenanceLeaseService;
    private readonly ISystemProcessJournalService? _processJournalService;
    private readonly IHostApplicationLifetime? _hostApplicationLifetime;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly string _instanceId = $"{Environment.MachineName}-{Guid.NewGuid():N}";

    public CatalogStockRefreshHostedService(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        IOptions<CatalogStockRefreshJobOptions> options,
        ILogger<CatalogStockRefreshHostedService> logger,
        ICatalogMaintenanceLeaseService maintenanceLeaseService,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        ISystemProcessJournalService? processJournalService = null,
        IHostApplicationLifetime? hostApplicationLifetime = null)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
        _maintenanceLeaseService = maintenanceLeaseService;
        _processJournalService = processJournalService;
        _hostApplicationLifetime = hostApplicationLifetime;
        _delayAsync = delayAsync ?? ((delay, cancellationToken) => Task.Delay(delay, cancellationToken));

        var raw = options.Value;
        _options = new CatalogStockRefreshJobOptions
        {
            Enabled = raw.Enabled,
            RunCatchUpOnStartup = raw.RunCatchUpOnStartup,
            TimeZoneId = string.IsNullOrWhiteSpace(raw.TimeZoneId) ? "Europe/Berlin" : raw.TimeZoneId.Trim(),
            LocalScheduleTime = raw.LocalScheduleTime >= TimeSpan.Zero && raw.LocalScheduleTime < TimeSpan.FromDays(1)
                ? raw.LocalScheduleTime
                : new TimeSpan(22, 30, 0),
            BatchSize = raw.BatchSize > 0 ? raw.BatchSize : 40,
            MaxConcurrency = raw.MaxConcurrency > 0 ? raw.MaxConcurrency : 1,
            InterRequestDelay = raw.InterRequestDelay >= TimeSpan.Zero ? raw.InterRequestDelay : TimeSpan.FromMilliseconds(250),
            RateLimitCooldown = raw.RateLimitCooldown > TimeSpan.Zero ? raw.RateLimitCooldown : TimeSpan.FromMinutes(2),
            RetryLimit = raw.RetryLimit >= 0 ? raw.RetryLimit : 2,
            RetryBaseDelay = raw.RetryBaseDelay > TimeSpan.Zero ? raw.RetryBaseDelay : TimeSpan.FromSeconds(2),
            LeaseDuration = raw.LeaseDuration > TimeSpan.Zero ? raw.LeaseDuration : TimeSpan.FromMinutes(10),
            LeaseRenewInterval = raw.LeaseRenewInterval > TimeSpan.Zero ? raw.LeaseRenewInterval : TimeSpan.FromMinutes(2),
            SharedLeaseRetryDelay = raw.SharedLeaseRetryDelay > TimeSpan.Zero ? raw.SharedLeaseRetryDelay : TimeSpan.FromSeconds(30),
            ProgressLogEveryStocks = raw.ProgressLogEveryStocks > 0 ? raw.ProgressLogEveryStocks : 25,
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var startupCheckCompleted = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_options.Enabled)
            {
                await _delayAsync(TimeSpan.FromMinutes(5), stoppingToken);
                startupCheckCompleted = true;
                continue;
            }

            var timeZone = ResolveTimeZone();
            var nowUtc = _timeProvider.GetUtcNow();
            var snapshot = CatalogStockRefreshScheduleCalculator.Snapshot(nowUtc, timeZone, _options.LocalScheduleTime);

            if (!startupCheckCompleted)
            {
                startupCheckCompleted = true;
                if (_options.RunCatchUpOnStartup)
                {
                    // Select the most recent past scheduled occurrence.
                    // If we are before today's 22:30, the previous occurrence is yesterday's.
                    // If we are at or after today's 22:30, the previous occurrence is today's.
                    // Never attempt to catch up a future occurrence.
                    DateTimeOffset catchUpOccurrenceUtc;
                    DateOnly catchUpBusinessDate;
                    if (nowUtc >= snapshot.TodayScheduledRunUtc)
                    {
                        catchUpOccurrenceUtc = snapshot.TodayScheduledRunUtc;
                        catchUpBusinessDate = snapshot.BusinessDate;
                    }
                    else
                    {
                        catchUpBusinessDate = snapshot.BusinessDate.AddDays(-1);
                        catchUpOccurrenceUtc = CatalogStockRefreshScheduleCalculator.ToUtc(
                            catchUpBusinessDate, _options.LocalScheduleTime, timeZone);
                    }

                    _logger.LogInformation(
                        "Startup catch-up: occurrence={OccurrenceUtc} businessDate={BusinessDate} trigger=startup-catch-up timeZone={TimeZoneId}",
                        catchUpOccurrenceUtc.UtcDateTime,
                        catchUpBusinessDate,
                        timeZone.Id);

                    await RunUntilNotDeferredAsync(catchUpBusinessDate, catchUpOccurrenceUtc.UtcDateTime, "startup-catch-up", stoppingToken);
                }
            }

            var delay = snapshot.NextScheduledRunUtc - _timeProvider.GetUtcNow();
            if (delay < TimeSpan.Zero)
            {
                delay = TimeSpan.Zero;
            }

            await _delayAsync(delay, stoppingToken);
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            var runSnapshot = CatalogStockRefreshScheduleCalculator.Snapshot(_timeProvider.GetUtcNow(), timeZone, _options.LocalScheduleTime);
            await RunUntilNotDeferredAsync(runSnapshot.BusinessDate, runSnapshot.TodayScheduledRunUtc.UtcDateTime, "scheduled", stoppingToken);
        }
    }

    internal Task TriggerRunAsync(
        DateOnly businessDate,
        DateTime scheduledAtUtc,
        string trigger,
        CancellationToken cancellationToken = default)
        => RunUntilNotDeferredAsync(businessDate, scheduledAtUtc, trigger, cancellationToken);

    public async Task<CatalogStockRefreshStatusResponse> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        var timeZone = ResolveTimeZone();
        var schedule = CatalogStockRefreshScheduleCalculator.Snapshot(nowUtc, timeZone, _options.LocalScheduleTime);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var latest = await db.CatalogStockRefreshRuns
            .AsNoTracking()
            .OrderByDescending(x => x.BusinessDate)
            .ThenByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        return new CatalogStockRefreshStatusResponse
        {
            GeneratedAtUtc = nowUtc.UtcDateTime,
            Enabled = _options.Enabled,
            TimeZoneId = timeZone.Id,
            LocalScheduleTime = _options.LocalScheduleTime,
            NextScheduledRunUtc = schedule.NextScheduledRunUtc.UtcDateTime,
            CurrentOrLatestRun = latest is null ? null : MapRun(latest),
        };
    }

    private async Task RunUntilNotDeferredAsync(
        DateOnly businessDate,
        DateTime scheduledAtUtc,
        string trigger,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var outcome = await TryRunBusinessDateAsync(businessDate, scheduledAtUtc, trigger, cancellationToken);
            if (outcome != RunAttemptOutcome.DeferredForSharedLease)
            {
                return;
            }

            await _delayAsync(_options.SharedLeaseRetryDelay, cancellationToken);
        }
    }

    private async Task<RunAttemptOutcome> TryRunBusinessDateAsync(
        DateOnly businessDate,
        DateTime scheduledAtUtc,
        string trigger,
        CancellationToken cancellationToken)
    {
        if (!_runGate.Wait(0))
        {
            return RunAttemptOutcome.NotStarted;
        }

        var hasRunLease = false;
        var hasSharedLease = false;
        var runId = 0;
        var processRunId = 0L;
        string? runKeyForLogs = null;
        try
        {
            var timeZone = ResolveTimeZone();
            var runKey = BuildRunKey(timeZone.Id, scheduledAtUtc);
            runKeyForLogs = runKey;
            var legacyRunKey = BuildLegacyRunKey(businessDate, timeZone.Id);
            var run = await GetOrCreateRunAsync(runKey, legacyRunKey, businessDate, timeZone.Id, scheduledAtUtc, cancellationToken);
            if (run is null)
            {
                return RunAttemptOutcome.NotStarted;
            }

            runId = run.Id;
            if (run.Status is CatalogStockRefreshRunStatus.Completed or CatalogStockRefreshRunStatus.CompletedWithErrors)
            {
                _logger.LogInformation(
                    "Nightly catalog refresh skipped: runKey={RunKey} businessDate={BusinessDate} trigger={Trigger} status={Status} (already completed)",
                    run.RunKey,
                    businessDate,
                    trigger,
                    run.Status);
                return RunAttemptOutcome.NotStarted;
            }

            processRunId = await (_processJournalService?.CreateOrGetAsync(new CreateSystemProcessRunRequest
            {
                ProcessType = SystemProcessTypes.CatalogStockRefresh,
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
                    run.BusinessDate,
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
                _logger.LogInformation(
                    "Nightly catalog refresh deferred: runKey={RunKey} businessDate={BusinessDate} trigger={Trigger} (shared maintenance lease held by another instance, will retry)",
                    runKey,
                    businessDate,
                    trigger);
                return RunAttemptOutcome.DeferredForSharedLease;
            }

            hasSharedLease = true;

            var startedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
            await UpdateRunAsync(runId, dbRun =>
            {
                dbRun.Status = CatalogStockRefreshRunStatus.Running;
                dbRun.StartedAtUtc ??= startedAtUtc;
                dbRun.UpdatedAtUtc = startedAtUtc;
            }, cancellationToken);
            if (processRunId > 0)
            {
                await _processJournalService!.MarkRunningAsync(processRunId, cancellationToken);
            }

            _logger.LogInformation(
                "Nightly catalog refresh started: runKey={RunKey} processRunId={ProcessRunId} businessDate={BusinessDate} trigger={Trigger} localTime={LocalScheduleTime} timeZone={TimeZoneId}",
                runKey,
                processRunId,
                businessDate,
                trigger,
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
                        final.Status == CatalogStockRefreshRunStatus.Completed
                            ? SystemProcessRunStatus.Succeeded
                            : SystemProcessRunStatus.CompletedWithErrors,
                        BuildProcessRunUpdate(final, final.FailureSummary ?? final.LastError),
                        cancellationToken);
                }
            }
            return RunAttemptOutcome.Completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var hostStopping = _hostApplicationLifetime?.ApplicationStopping.IsCancellationRequested ?? true;
            var cancellationReason = hostStopping
                ? "Остановка/перезапуск приложения."
                : "Отмена токена выполнения (возможна ручная отмена/timeout/linked cancellation).";
            _logger.LogWarning(
                "Nightly catalog refresh cancelled: runKey={RunKey} runId={RunId} processRunId={ProcessRunId} trigger={Trigger} hostStopping={HostStopping}",
                runKeyForLogs,
                runId,
                processRunId,
                trigger,
                hostStopping);

            if (processRunId > 0)
            {
                var final = runId > 0
                    ? await GetRunAsync(runId, CancellationToken.None)
                    : null;
                await _processJournalService!.CompleteAsync(
                    processRunId,
                    hostStopping ? SystemProcessRunStatus.Interrupted : SystemProcessRunStatus.Cancelled,
                    final is null
                        ? new UpdateSystemProcessRunRequest { ErrorSummary = $"Ночное обновление каталога прервано. Причина: {cancellationReason}" }
                        : BuildProcessRunUpdate(final, $"Ночное обновление каталога прервано. Причина: {cancellationReason}"),
                    CancellationToken.None);
            }
            return RunAttemptOutcome.NotStarted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nightly catalog refresh failed for businessDate={BusinessDate}", businessDate);
            if (processRunId > 0)
            {
                var final = runId > 0
                    ? await GetRunAsync(runId, CancellationToken.None)
                    : null;
                await _processJournalService!.CompleteAsync(
                    processRunId,
                    SystemProcessRunStatus.Failed,
                    final is null
                        ? new UpdateSystemProcessRunRequest { ErrorSummary = ex.Message }
                        : BuildProcessRunUpdate(final, ex.Message),
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

            var leaseRenewal = await TryRenewLeaseIfNeededAsync(runId, lastLeaseRenewedAtUtc, cancellationToken);
            lastLeaseRenewedAtUtc = leaseRenewal.LastRenewedAtUtc;
            if (!leaseRenewal.Success)
            {
                await UpdateRunAsync(runId, run =>
                {
                    run.Status = CatalogStockRefreshRunStatus.Failed;
                    run.LastError = "Lease was lost to another instance.";
                    run.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
                }, cancellationToken);
                return;
            }

            var sharedLeaseRenewal = await TryRenewSharedLeaseIfNeededAsync(lastSharedLeaseRenewedAtUtc, cancellationToken);
            lastSharedLeaseRenewedAtUtc = sharedLeaseRenewal.LastRenewedAtUtc;
            if (!sharedLeaseRenewal.Success)
            {
                await UpdateRunAsync(runId, run =>
                {
                    run.Status = CatalogStockRefreshRunStatus.Failed;
                    run.LastError = "Shared maintenance lease was lost to another instance.";
                    run.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
                }, cancellationToken);
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
            .Select(s => new RefreshStockCandidate(s.Id, s.Ticker, s.Exchange, s.FinanzenNetSlug))
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);
    }

    private async Task<StockProcessResult> ProcessSingleStockAsync(int runId, RefreshStockCandidate stock, CancellationToken cancellationToken)
    {
        await UpdateRunAsync(runId, run =>
        {
            run.PendingStockId = stock.StockId;
            run.PendingHistoryCompleted = false;
            run.PendingQuoteCompleted = false;
            run.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        }, cancellationToken);

        if (string.IsNullOrWhiteSpace(stock.Ticker))
        {
            await ApplyResultAsync(runId, stock.StockId, StepOutcome.Skipped("Missing ticker."), StepOutcome.Skipped("Missing ticker."), cancellationToken);
            return StockProcessResult.None;
        }

        if (!StockExchanges.TryNormalize(stock.Exchange, out var normalizedExchange))
        {
            await ApplyResultAsync(
                runId,
                stock.StockId,
                StepOutcome.Skipped("Invalid exchange."),
                StepOutcome.Skipped("Invalid exchange."),
                cancellationToken);
            return StockProcessResult.None;
        }

        var normalized = stock with { Exchange = normalizedExchange };

        var quoteOutcome = await ExecuteWithRetriesAsync(
            () => RefreshQuoteAsync(normalized, cancellationToken),
            cancellationToken);
        await UpdateRunAsync(runId, run =>
        {
            run.PendingQuoteCompleted = true;
            run.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        }, cancellationToken);

        StepOutcome historyOutcome;
        if (quoteOutcome.Kind == StepOutcomeKind.RateLimited)
        {
            historyOutcome = StepOutcome.Skipped("Quote was rate-limited.");
        }
        else
        {
            historyOutcome = await ExecuteWithRetriesAsync(
                () => RefreshHistoryAsync(normalized, cancellationToken),
                cancellationToken);
        }

        await UpdateRunAsync(runId, run =>
        {
            run.PendingHistoryCompleted = true;
            run.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        }, cancellationToken);

        await ApplyResultAsync(runId, stock.StockId, quoteOutcome, historyOutcome, cancellationToken);

        return new StockProcessResult(quoteOutcome.GetCooldown(_options.RateLimitCooldown), historyOutcome.GetCooldown(_options.RateLimitCooldown));
    }

    private async Task TryProcessPendingStockAsync(int runId, CancellationToken cancellationToken)
    {
        var pending = await GetPendingStateAsync(runId, cancellationToken);
        if (pending.PendingStockId is null)
        {
            return;
        }

        var stock = await LoadStockByIdAsync(pending.PendingStockId.Value, cancellationToken);
        if (stock is null)
        {
            await ApplyResultAsync(
                runId,
                pending.PendingStockId.Value,
                pending.PendingQuoteCompleted ? StepOutcome.Skipped("Stock no longer exists.") : StepOutcome.Skipped("Stock no longer exists."),
                pending.PendingHistoryCompleted ? StepOutcome.Skipped("Stock no longer exists.") : StepOutcome.Skipped("Stock no longer exists."),
                cancellationToken);
            return;
        }

        StepOutcome quoteOutcome = pending.PendingQuoteCompleted
            ? StepOutcome.AlreadyCompleted()
            : await ExecuteWithRetriesAsync(() => RefreshQuoteAsync(stock.Value, cancellationToken), cancellationToken);

        if (!pending.PendingQuoteCompleted)
        {
            await UpdateRunAsync(runId, run =>
            {
                run.PendingQuoteCompleted = true;
                run.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
            }, cancellationToken);
        }

        StepOutcome historyOutcome = pending.PendingHistoryCompleted
            ? StepOutcome.AlreadyCompleted()
            : quoteOutcome.Kind == StepOutcomeKind.RateLimited
                ? StepOutcome.Skipped("Quote was rate-limited.")
                : await ExecuteWithRetriesAsync(() => RefreshHistoryAsync(stock.Value, cancellationToken), cancellationToken);

        if (!pending.PendingHistoryCompleted)
        {
            await UpdateRunAsync(runId, run =>
            {
                run.PendingHistoryCompleted = true;
                run.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
            }, cancellationToken);
        }

        await ApplyResultAsync(runId, stock.Value.StockId, quoteOutcome, historyOutcome, cancellationToken);
    }

    private async Task<RefreshStockCandidate?> LoadStockByIdAsync(int stockId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stock = await db.Stocks
            .AsNoTracking()
            .Where(s => s.Id == stockId && (s.TrackingStatus == StockTrackingStatus.Tracked || s.TrackingStatus == StockTrackingStatus.CatalogOnly))
            .Select(s => new RefreshStockCandidate(s.Id, s.Ticker, s.Exchange, s.FinanzenNetSlug))
            .FirstOrDefaultAsync(cancellationToken);
        return stock.StockId == 0 ? null : stock;
    }

    private async Task<StepOutcome> RefreshQuoteAsync(RefreshStockCandidate stock, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var quoteFetchService = scope.ServiceProvider.GetRequiredService<IStockQuoteFetchService>();
        var fetchResult = await quoteFetchService.FetchAsync(stock.Ticker, stock.Exchange, stock.FinanzenNetSlug, cancellationToken);

        if (fetchResult.IsRateLimited)
        {
            return StepOutcome.RateLimited(fetchResult.ErrorMessage, fetchResult.RetryAfterDelay);
        }

        if (!fetchResult.IsSuccess || fetchResult.Quote is null)
        {
            return StepOutcome.Failed(fetchResult.ErrorMessage ?? "Quote fetch failed.");
        }

        var quote = fetchResult.Quote;
        if (quote.CurrentPriceEur is null)
        {
            return StepOutcome.Skipped("Quote is missing EUR conversion.");
        }

        var incomingPrice = Math.Round(quote.CurrentPriceEur.Value, 2);
        var incomingChange = quote.ChangeEur.HasValue ? Math.Round(quote.ChangeEur.Value, 4) : (decimal?)null;
        var incomingPercent = Math.Round(quote.PercentChange, 4);

        var quoteSnapshotPersistenceService = scope.ServiceProvider.GetRequiredService<StockQuoteSnapshotPersistenceService>();

        var persistenceResult = await quoteSnapshotPersistenceService.ApplyAsync(
            stock.StockId,
            new PersistStockQuoteSnapshotRequest
            {
                CurrentPrice = incomingPrice,
                CurrentPriceChange = incomingChange,
                CurrentPriceChangePercent = incomingPercent,
                CurrentPriceAt = quote.PriceTimestampUtc,
                CurrentPriceIsDelayed = quote.IsStale || !string.IsNullOrWhiteSpace(quote.DelayWarning),
                CurrentPriceDelayWarning = quote.DelayWarning,
                QuoteCurrency = "EUR",
                FinancialCurrency = "EUR",
                NormalizedQuoteCurrency = "EUR",
                QuoteUnitMultiplier = 1m,
            },
            cancellationToken);

        if (!persistenceResult.StockFound)
        {
            return StepOutcome.Skipped("Stock record not found.");
        }

        return persistenceResult.Applied
            ? StepOutcome.Succeeded()
            : StepOutcome.Skipped(persistenceResult.Reason);
    }

    private async Task<StepOutcome> RefreshHistoryAsync(RefreshStockCandidate stock, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var historyService = scope.ServiceProvider.GetRequiredService<IStockHistoryService>();

        var historyResult = await historyService.RefreshHistoryAsync(new Stock
        {
            Id = stock.StockId,
            Ticker = stock.Ticker,
            Exchange = stock.Exchange
        }, StockHistoryRefreshTrigger.Automatic, cancellationToken);

        if (historyResult.RateLimited)
        {
            return StepOutcome.RateLimited("History provider rate-limited.");
        }

        if (historyResult.StockNotFound)
        {
            return StepOutcome.Skipped("Stock record not found for history refresh.");
        }

        if (historyResult.SkippedNotDue)
        {
            var tierSuffix = string.IsNullOrWhiteSpace(historyResult.AppliedTier)
                ? string.Empty
                : $" (tier={historyResult.AppliedTier})";
            return StepOutcome.Skipped($"History refresh not due{tierSuffix}.");
        }

        return StepOutcome.Succeeded();
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

            if (outcome.Kind is StepOutcomeKind.Succeeded or StepOutcomeKind.Skipped or StepOutcomeKind.AlreadyCompleted)
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
            var retryDelay = TimeSpan.FromMilliseconds(baseDelay + jitterMs);
            await _delayAsync(retryDelay, cancellationToken);
        }
    }

    private async Task ApplyResultAsync(
        int runId,
        int stockId,
        StepOutcome quoteOutcome,
        StepOutcome historyOutcome,
        CancellationToken cancellationToken)
    {
        await UpdateRunAsync(runId, run =>
        {
            ApplyStep(run, quoteOutcome, isQuote: true, stockId);
            ApplyStep(run, historyOutcome, isQuote: false, stockId);

            run.LastProcessedStockId = stockId;
            run.PendingStockId = null;
            run.PendingQuoteCompleted = false;
            run.PendingHistoryCompleted = false;
            run.Processed++;
            run.Remaining = Math.Max(0, run.TotalDiscovered - run.Processed);
            run.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        }, cancellationToken);
    }

    private static void ApplyStep(CatalogStockRefreshRun run, StepOutcome outcome, bool isQuote, int stockId)
    {
        switch (outcome.Kind)
        {
            case StepOutcomeKind.Succeeded:
            case StepOutcomeKind.AlreadyCompleted:
                if (isQuote) run.QuoteSucceeded++;
                else run.HistorySucceeded++;
                return;
            case StepOutcomeKind.Skipped:
                if (isQuote) run.QuoteSkipped++;
                else run.HistorySkipped++;
                if (!string.IsNullOrWhiteSpace(outcome.Reason))
                {
                    AppendFailure(run, $"stockId={stockId} {(isQuote ? "quote" : "history")} skipped: {outcome.Reason}");
                }
                return;
            case StepOutcomeKind.RateLimited:
                run.RateLimited++;
                if (isQuote) run.QuoteSkipped++;
                else run.HistorySkipped++;
                run.Status = CatalogStockRefreshRunStatus.PausedRateLimited;
                AppendFailure(run, $"stockId={stockId} {(isQuote ? "quote" : "history")} rate-limited");
                return;
            case StepOutcomeKind.Failed:
                if (isQuote) run.QuoteFailed++;
                else run.HistoryFailed++;
                AppendFailure(run, $"stockId={stockId} {(isQuote ? "quote" : "history")} failed: {outcome.Reason}");
                return;
            default:
                return;
        }
    }

    private static void AppendFailure(CatalogStockRefreshRun run, string message)
    {
        var safe = message.Trim();
        if (safe.Length > 400)
        {
            safe = safe[..400];
        }

        run.LastError = safe;
        var current = string.IsNullOrWhiteSpace(run.FailureSummary)
            ? string.Empty
            : run.FailureSummary + Environment.NewLine;
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
        var run = await db.CatalogStockRefreshRuns.FirstAsync(x => x.Id == runId, cancellationToken);
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
        var run = await db.CatalogStockRefreshRuns
            .AsNoTracking()
            .Where(x => x.Id == runId)
            .Select(x => new { x.LastProcessedStockId })
            .FirstAsync(cancellationToken);
        return run.LastProcessedStockId ?? 0;
    }

    private async Task<(int? PendingStockId, bool PendingQuoteCompleted, bool PendingHistoryCompleted)> GetPendingStateAsync(
        int runId,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.CatalogStockRefreshRuns
            .AsNoTracking()
            .Where(x => x.Id == runId)
            .Select(x => new { x.PendingStockId, x.PendingQuoteCompleted, x.PendingHistoryCompleted })
            .FirstAsync(cancellationToken);
        return (run.PendingStockId, run.PendingQuoteCompleted, run.PendingHistoryCompleted);
    }

    private async Task<bool> TryAcquireLeaseAsync(int runId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var leaseUntil = now.Add(_options.LeaseDuration);

        if (!db.Database.IsRelational())
        {
            var run = await db.CatalogStockRefreshRuns.FirstOrDefaultAsync(x => x.Id == runId, cancellationToken);
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
            UPDATE CatalogStockRefreshRuns
            SET LeaseOwner = {_instanceId},
                LeaseExpiresAtUtc = {leaseUntil},
                UpdatedAtUtc = {now}
            WHERE Id = {runId}
              AND (LeaseOwner IS NULL OR LeaseOwner = {_instanceId} OR LeaseExpiresAtUtc IS NULL OR LeaseExpiresAtUtc < {now})
            """, cancellationToken);
        return affected == 1;
    }

    private async Task<(bool Success, DateTime LastRenewedAtUtc)> TryRenewLeaseIfNeededAsync(
        int runId,
        DateTime lastLeaseRenewedAtUtc,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (lastLeaseRenewedAtUtc != DateTime.MinValue &&
            now - lastLeaseRenewedAtUtc < _options.LeaseRenewInterval)
        {
            return (true, lastLeaseRenewedAtUtc);
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var newLeaseExpiresAt = now.Add(_options.LeaseDuration);

        if (!db.Database.IsRelational())
        {
            var run = await db.CatalogStockRefreshRuns.FirstOrDefaultAsync(x => x.Id == runId, cancellationToken);
            if (run is null || !string.Equals(run.LeaseOwner, _instanceId, StringComparison.Ordinal))
            {
                return (false, lastLeaseRenewedAtUtc);
            }

            run.LeaseExpiresAtUtc = newLeaseExpiresAt;
            run.UpdatedAtUtc = now;
            await db.SaveChangesAsync(cancellationToken);
            return (true, now);
        }

        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE CatalogStockRefreshRuns
            SET LeaseExpiresAtUtc = {newLeaseExpiresAt},
                UpdatedAtUtc = {now}
            WHERE Id = {runId}
              AND LeaseOwner = {_instanceId}
            """, cancellationToken);
        if (affected == 1)
        {
            return (true, now);
        }

        return (false, lastLeaseRenewedAtUtc);
    }

    private async Task<(bool Success, DateTime LastRenewedAtUtc)> TryRenewSharedLeaseIfNeededAsync(
        DateTime lastLeaseRenewedAtUtc,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (lastLeaseRenewedAtUtc != DateTime.MinValue &&
            now - lastLeaseRenewedAtUtc < _options.LeaseRenewInterval)
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

    private async Task ReleaseRunLeaseAsync(int runId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        if (!db.Database.IsRelational())
        {
            var run = await db.CatalogStockRefreshRuns.FirstOrDefaultAsync(x => x.Id == runId, cancellationToken);
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
            UPDATE CatalogStockRefreshRuns
            SET LeaseOwner = NULL,
                LeaseExpiresAtUtc = NULL,
                UpdatedAtUtc = {now}
            WHERE Id = {runId}
              AND LeaseOwner = {_instanceId}
            """, cancellationToken);
    }

    private async Task FinalizeRunAsync(int runId, CancellationToken cancellationToken)
    {
        await UpdateRunAsync(runId, run =>
        {
            run.CompletedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
            run.PendingStockId = null;
            run.PendingQuoteCompleted = false;
            run.PendingHistoryCompleted = false;
            run.Remaining = Math.Max(0, run.TotalDiscovered - run.Processed);
            run.Status = run.QuoteFailed > 0 || run.HistoryFailed > 0 || run.RateLimited > 0
                ? CatalogStockRefreshRunStatus.CompletedWithErrors
                : CatalogStockRefreshRunStatus.Completed;
            run.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        }, cancellationToken);

        var final = await GetRunAsync(runId, cancellationToken);
        _logger.LogInformation(
            "Nightly catalog refresh completed: runKey={RunKey} status={Status} total={Total} processed={Processed} quoteOk={QuoteSucceeded} quoteFailed={QuoteFailed} quoteSkipped={QuoteSkipped} historyOk={HistorySucceeded} historyFailed={HistoryFailed} historySkipped={HistorySkipped} rateLimited={RateLimited}",
            final?.RunKey,
            final?.Status,
            final?.TotalDiscovered,
            final?.Processed,
            final?.QuoteSucceeded,
            final?.QuoteFailed,
            final?.QuoteSkipped,
            final?.HistorySucceeded,
            final?.HistoryFailed,
            final?.HistorySkipped,
            final?.RateLimited);
    }

    private async Task<CatalogStockRefreshRun?> GetRunAsync(int runId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.CatalogStockRefreshRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == runId, cancellationToken);
    }

    private static UpdateSystemProcessRunRequest BuildProcessRunUpdate(CatalogStockRefreshRun run, string? errorSummary)
        => new()
        {
            TotalItems = run.TotalDiscovered,
            ProcessedItems = run.Processed,
            SucceededItems = run.QuoteSucceeded + run.HistorySucceeded,
            FailedItems = run.QuoteFailed + run.HistoryFailed + run.RateLimited,
            SkippedItems = run.QuoteSkipped + run.HistorySkipped,
            ResultSummary = $"Обработано {run.Processed} из {run.TotalDiscovered}; quote ok {run.QuoteSucceeded}; history ok {run.HistorySucceeded}.",
            ErrorSummary = errorSummary,
            LastProcessedEntity = run.LastProcessedStockId.HasValue ? $"stockId={run.LastProcessedStockId.Value}" : null,
        };

    private async Task<CatalogStockRefreshRun?> GetOrCreateRunAsync(
        string runKey,
        string legacyRunKey,
        DateOnly businessDate,
        string timeZoneId,
        DateTime scheduledAtUtc,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var existing = await db.CatalogStockRefreshRuns.FirstOrDefaultAsync(x => x.RunKey == runKey, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        // Backward compatibility: look for a legacy-format row (timezone:date) that represents
        // the same occurrence. Only resume/skip legacy rows that are for the matching business date
        // and whose ScheduledAtUtc is within 2 hours of the expected occurrence (to avoid mistakenly
        // treating an early-morning catch-up for the wrong occurrence as today's scheduled run —
        // the root cause of the 2026-08-19 production incident).
        var legacy = await db.CatalogStockRefreshRuns.FirstOrDefaultAsync(x => x.RunKey == legacyRunKey, cancellationToken);
        if (legacy is not null && legacy.BusinessDate == businessDate)
        {
            var drift = Math.Abs((legacy.ScheduledAtUtc - scheduledAtUtc).TotalHours);
            if (drift <= 2.0)
            {
                _logger.LogInformation(
                    "Nightly catalog refresh: found legacy run key={LegacyRunKey} for occurrence={OccurrenceUtc} — treating as same occurrence (drift={DriftHours:F2}h)",
                    legacyRunKey,
                    scheduledAtUtc,
                    drift);
                return legacy;
            }

            _logger.LogInformation(
                "Nightly catalog refresh: legacy run key={LegacyRunKey} found but ScheduledAtUtc={LegacyScheduledAtUtc} differs from expected occurrence={OccurrenceUtc} by {DriftHours:F2}h (>2h) — creating new run with key={RunKey}",
                legacyRunKey,
                legacy.ScheduledAtUtc,
                scheduledAtUtc,
                drift,
                runKey);
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var run = new CatalogStockRefreshRun
        {
            RunKey = runKey,
            BusinessDate = businessDate,
            TimeZoneId = timeZoneId,
            ScheduledAtUtc = scheduledAtUtc,
            Status = CatalogStockRefreshRunStatus.Pending,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        db.CatalogStockRefreshRuns.Add(run);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return run;
        }
        catch (DbUpdateException)
        {
            return await db.CatalogStockRefreshRuns.FirstOrDefaultAsync(x => x.RunKey == runKey, cancellationToken);
        }
    }

    private async Task UpdateRunAsync(int runId, Action<CatalogStockRefreshRun> update, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.CatalogStockRefreshRuns.FirstAsync(x => x.Id == runId, cancellationToken);
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

    private static string BuildRunKey(string timeZoneId, DateTime scheduledAtUtc)
        => $"{timeZoneId}:{scheduledAtUtc:yyyy-MM-ddTHH:mm:ssZ}";

    private static string BuildLegacyRunKey(DateOnly businessDate, string timeZoneId)
        => $"{timeZoneId}:{businessDate:yyyy-MM-dd}";

    private static CatalogStockRefreshRunDetails MapRun(CatalogStockRefreshRun run)
        => new()
        {
            RunKey = run.RunKey,
            BusinessDate = run.BusinessDate,
            Status = run.Status,
            ScheduledAtUtc = run.ScheduledAtUtc,
            StartedAtUtc = run.StartedAtUtc,
            CompletedAtUtc = run.CompletedAtUtc,
            LastProcessedStockId = run.LastProcessedStockId ?? 0,
            TotalDiscovered = run.TotalDiscovered,
            Processed = run.Processed,
            Remaining = run.Remaining,
            QuoteSucceeded = run.QuoteSucceeded,
            QuoteFailed = run.QuoteFailed,
            QuoteSkipped = run.QuoteSkipped,
            HistorySucceeded = run.HistorySucceeded,
            HistoryFailed = run.HistoryFailed,
            HistorySkipped = run.HistorySkipped,
            RateLimited = run.RateLimited,
            LastError = run.LastError,
            FailureSummary = run.FailureSummary
        };

    private readonly record struct RefreshStockCandidate(int StockId, string Ticker, string Exchange, string? FinanzenNetSlug);
    private readonly record struct StockProcessResult(TimeSpan QuoteCooldownDelay, TimeSpan HistoryCooldownDelay)
    {
        public static StockProcessResult None => new(TimeSpan.Zero, TimeSpan.Zero);
        public TimeSpan CooldownDelay => QuoteCooldownDelay > HistoryCooldownDelay ? QuoteCooldownDelay : HistoryCooldownDelay;
    }

    private enum RunAttemptOutcome
    {
        NotStarted,
        DeferredForSharedLease,
        Completed
    }

    private enum StepOutcomeKind
    {
        Succeeded,
        Failed,
        Skipped,
        RateLimited,
        AlreadyCompleted
    }

    private readonly record struct StepOutcome(StepOutcomeKind Kind, string? Reason, TimeSpan? RetryAfter)
    {
        public static StepOutcome Succeeded() => new(StepOutcomeKind.Succeeded, null, null);
        public static StepOutcome Failed(string? reason) => new(StepOutcomeKind.Failed, reason, null);
        public static StepOutcome Skipped(string? reason) => new(StepOutcomeKind.Skipped, reason, null);
        public static StepOutcome RateLimited(string? reason, TimeSpan? retryAfter = null) => new(StepOutcomeKind.RateLimited, reason, retryAfter);
        public static StepOutcome AlreadyCompleted() => new(StepOutcomeKind.AlreadyCompleted, null, null);

        public TimeSpan GetCooldown(TimeSpan defaultDelay)
        {
            if (Kind != StepOutcomeKind.RateLimited)
            {
                return TimeSpan.Zero;
            }

            if (RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero)
            {
                return retryAfter;
            }

            return defaultDelay;
        }
    }
}
