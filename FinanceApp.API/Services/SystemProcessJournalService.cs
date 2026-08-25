using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using FinanceApp.Core.Models;
using FinanceApp.Data.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinanceApp.API.Services;

public sealed class SystemProcessJournalOptions
{
    public int RetentionDays { get; init; } = 90;
    public int MaxRows { get; init; } = 20000;
    public int CleanupBatchSize { get; init; } = 250;
    public TimeSpan CleanupInterval { get; init; } = TimeSpan.FromHours(6);
    public TimeSpan StaleRunningThreshold { get; init; } = TimeSpan.FromMinutes(30);
}

public static class SystemProcessTypes
{
    public const string CatalogStockRefresh = "catalog-stock-refresh";
    public const string CatalogFundamentalsRefresh = "catalog-fundamentals-refresh";
    public const string StockHistoryRefreshCycle = "stock-history-refresh-cycle";
    public const string StockQuoteRefreshCycle = "stock-quote-refresh-cycle";
    public const string IndexConstituentHistoryRefresh = "index-constituent-history-refresh";
    public const string IndexConstituentsBatchQuoteRefresh = "index-constituents-batch-quote-refresh";
    public const string FrankfurtAggregateRebuild = "frankfurt-aggregate-rebuild";

    public static string GetDisplayName(string processType)
        => processType switch
        {
            CatalogStockRefresh => "Ночное обновление каталога акций",
            CatalogFundamentalsRefresh => "Недельное обновление фундаментальных данных",
            StockHistoryRefreshCycle => "Автообновление исторических данных акций",
            StockQuoteRefreshCycle => "Автообновление котировок акций",
            IndexConstituentHistoryRefresh => "Обновление истории акции индекса",
            IndexConstituentsBatchQuoteRefresh => "Пакетное обновление котировок индекса",
            FrankfurtAggregateRebuild => "Пересборка агрегатов Frankfurt",
            _ => processType,
        };
}

public sealed class CreateSystemProcessRunRequest
{
    public required string ProcessType { get; init; }
    public string? DisplayName { get; init; }
    public SystemProcessTrigger Trigger { get; init; } = SystemProcessTrigger.Automatic;
    public string? InitiatedByUserId { get; init; }
    public string? CorrelationId { get; init; }
    public string? ExternalRunKey { get; init; }
    public int? TotalItems { get; init; }
    public string? ResultSummary { get; init; }
    public string? ErrorSummary { get; init; }
    public string? LastProcessedEntity { get; init; }
    public object? Details { get; init; }
    public SystemProcessRunStatus InitialStatus { get; init; } = SystemProcessRunStatus.Pending;
}

public sealed class UpdateSystemProcessRunRequest
{
    public SystemProcessRunStatus? Status { get; init; }
    public int? TotalItems { get; init; }
    public int? ProcessedItems { get; init; }
    public int? SucceededItems { get; init; }
    public int? FailedItems { get; init; }
    public int? SkippedItems { get; init; }
    public string? ResultSummary { get; init; }
    public string? ErrorSummary { get; init; }
    public string? LastProcessedEntity { get; init; }
    public object? Details { get; init; }
}

public interface ISystemProcessJournalService
{
    Task<long> CreateOrGetAsync(CreateSystemProcessRunRequest request, CancellationToken cancellationToken = default);
    Task MarkRunningAsync(long processRunId, CancellationToken cancellationToken = default);
    Task UpdateAsync(long processRunId, UpdateSystemProcessRunRequest request, CancellationToken cancellationToken = default);
    Task CompleteAsync(long processRunId, SystemProcessRunStatus terminalStatus, UpdateSystemProcessRunRequest? request = null, CancellationToken cancellationToken = default);
    Task<int> ReconcileInterruptedAsync(CancellationToken cancellationToken = default);
    Task<int> CleanupAsync(CancellationToken cancellationToken = default);
}

public sealed class SystemProcessJournalService : ISystemProcessJournalService
{
    private static readonly HashSet<SystemProcessRunStatus> TerminalStatuses =
    [
        SystemProcessRunStatus.Succeeded,
        SystemProcessRunStatus.CompletedWithErrors,
        SystemProcessRunStatus.Failed,
        SystemProcessRunStatus.Cancelled,
        SystemProcessRunStatus.Interrupted,
    ];

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private static readonly Regex SensitivePattern = new(
        "(?i)(token|password|secret|apikey|api-key|connectionstring)\\s*[=:]\\s*[^,;\\s]+",
        RegexOptions.Compiled);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SystemProcessJournalService> _logger;
    private readonly SystemProcessJournalOptions _options;
    private readonly string _instanceId = $"{Environment.MachineName}-{Guid.NewGuid():N}";
    private readonly ConcurrentDictionary<long, DateTime> _lastProgressFlushUtc = new();

    public SystemProcessJournalService(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        IOptions<SystemProcessJournalOptions> options,
        ILogger<SystemProcessJournalService> logger)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;

        var raw = options.Value;
        _options = new SystemProcessJournalOptions
        {
            RetentionDays = raw.RetentionDays > 0 ? raw.RetentionDays : 90,
            MaxRows = raw.MaxRows > 0 ? raw.MaxRows : 20000,
            CleanupBatchSize = raw.CleanupBatchSize > 0 ? raw.CleanupBatchSize : 250,
            CleanupInterval = raw.CleanupInterval > TimeSpan.Zero ? raw.CleanupInterval : TimeSpan.FromHours(6),
            StaleRunningThreshold = raw.StaleRunningThreshold > TimeSpan.Zero
                ? raw.StaleRunningThreshold
                : TimeSpan.FromMinutes(30),
        };
    }

    public async Task<long> CreateOrGetAsync(CreateSystemProcessRunRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            if (!string.IsNullOrWhiteSpace(request.ExternalRunKey))
            {
                var existing = await db.SystemProcessRuns
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.ExternalRunKey == request.ExternalRunKey, cancellationToken);
                if (existing is not null)
                {
                    return existing.Id;
                }
            }

            var entity = new SystemProcessRun
            {
                ProcessType = Truncate(request.ProcessType.Trim(), 100),
                DisplayName = Truncate(request.DisplayName?.Trim() ?? SystemProcessTypes.GetDisplayName(request.ProcessType), 200),
                Status = request.InitialStatus,
                Trigger = request.Trigger,
                InitiatedByUserId = TruncateOrNull(request.InitiatedByUserId, 128),
                CorrelationId = TruncateOrNull(request.CorrelationId, 128),
                ExternalRunKey = TruncateOrNull(request.ExternalRunKey, 128),
                InstanceId = Truncate(_instanceId, 128),
                QueuedAtUtc = nowUtc,
                StartedAtUtc = request.InitialStatus == SystemProcessRunStatus.Running ? nowUtc : null,
                CompletedAtUtc = TerminalStatuses.Contains(request.InitialStatus) ? nowUtc : null,
                UpdatedAtUtc = nowUtc,
                HeartbeatAtUtc = nowUtc,
                TotalItems = request.TotalItems,
                ResultSummary = NormalizeSafeText(request.ResultSummary, 1000),
                ErrorSummary = NormalizeSafeText(request.ErrorSummary, 1000),
                LastProcessedEntity = TruncateOrNull(request.LastProcessedEntity, 256),
                DetailsJson = SerializeSafeDetails(request.Details),
            };

            db.SystemProcessRuns.Add(entity);
            await db.SaveChangesAsync(cancellationToken);
            return entity.Id;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "System process journal CreateOrGet failed for processType={ProcessType}", request.ProcessType);
            return 0;
        }
    }

    public async Task MarkRunningAsync(long processRunId, CancellationToken cancellationToken = default)
    {
        if (processRunId <= 0)
        {
            return;
        }

        await UpdateInternalAsync(
            processRunId,
            request: new UpdateSystemProcessRunRequest { Status = SystemProcessRunStatus.Running },
            completeAtUtc: null,
            ignoreThrottle: true,
            cancellationToken: cancellationToken);
    }

    public async Task UpdateAsync(long processRunId, UpdateSystemProcessRunRequest request, CancellationToken cancellationToken = default)
    {
        if (processRunId <= 0)
        {
            return;
        }

        await UpdateInternalAsync(processRunId, request, completeAtUtc: null, ignoreThrottle: false, cancellationToken);
    }

    public async Task CompleteAsync(
        long processRunId,
        SystemProcessRunStatus terminalStatus,
        UpdateSystemProcessRunRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        if (processRunId <= 0 || !TerminalStatuses.Contains(terminalStatus))
        {
            return;
        }

        var updateRequest = request ?? new UpdateSystemProcessRunRequest();
        await UpdateInternalAsync(
            processRunId,
            new UpdateSystemProcessRunRequest
            {
                Status = terminalStatus,
                TotalItems = updateRequest.TotalItems,
                ProcessedItems = updateRequest.ProcessedItems,
                SucceededItems = updateRequest.SucceededItems,
                FailedItems = updateRequest.FailedItems,
                SkippedItems = updateRequest.SkippedItems,
                ResultSummary = updateRequest.ResultSummary,
                ErrorSummary = updateRequest.ErrorSummary,
                LastProcessedEntity = updateRequest.LastProcessedEntity,
                Details = updateRequest.Details,
            },
            completeAtUtc: _timeProvider.GetUtcNow().UtcDateTime,
            ignoreThrottle: true,
            cancellationToken: cancellationToken);
    }

    public async Task<int> ReconcileInterruptedAsync(CancellationToken cancellationToken = default)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var thresholdUtc = nowUtc - _options.StaleRunningThreshold;
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var stale = await db.SystemProcessRuns
            .Where(x =>
                x.CompletedAtUtc == null
                && (x.Status == SystemProcessRunStatus.Pending || x.Status == SystemProcessRunStatus.Running)
                && x.UpdatedAtUtc < thresholdUtc)
            .ToListAsync(cancellationToken);

        foreach (var run in stale)
        {
            run.Status = SystemProcessRunStatus.Interrupted;
            run.CompletedAtUtc = nowUtc;
            run.UpdatedAtUtc = nowUtc;
            run.HeartbeatAtUtc = nowUtc;
            if (string.IsNullOrWhiteSpace(run.ErrorSummary))
            {
                run.ErrorSummary = "Процесс прерван после перезапуска или потери активности.";
            }
        }

        if (stale.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return stale.Count;
    }

    public async Task<int> CleanupAsync(CancellationToken cancellationToken = default)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var retentionCutoffUtc = nowUtc.AddDays(-_options.RetentionDays);
        var deleted = 0;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        while (!cancellationToken.IsCancellationRequested)
        {
            var batchIds = await db.SystemProcessRuns
                .AsNoTracking()
                .Where(x =>
                    x.CompletedAtUtc != null
                    && x.CompletedAtUtc < retentionCutoffUtc)
                .OrderBy(x => x.CompletedAtUtc)
                .ThenBy(x => x.Id)
                .Select(x => x.Id)
                .Take(_options.CleanupBatchSize)
                .ToListAsync(cancellationToken);

            if (batchIds.Count == 0)
            {
                break;
            }

            var batch = await db.SystemProcessRuns
                .Where(x => batchIds.Contains(x.Id) && x.CompletedAtUtc != null)
                .ToListAsync(cancellationToken);

            if (batch.Count == 0)
            {
                break;
            }

            db.SystemProcessRuns.RemoveRange(batch);
            deleted += batch.Count;
            await db.SaveChangesAsync(cancellationToken);

            if (batch.Count < _options.CleanupBatchSize)
            {
                break;
            }
        }

        var totalRows = await db.SystemProcessRuns.AsNoTracking().CountAsync(cancellationToken);
        if (totalRows > _options.MaxRows)
        {
            var toTrim = totalRows - _options.MaxRows;
            while (toTrim > 0)
            {
                var trimIds = await db.SystemProcessRuns
                    .AsNoTracking()
                    .Where(x => x.CompletedAtUtc != null)
                    .OrderBy(x => x.CompletedAtUtc)
                    .ThenBy(x => x.Id)
                    .Select(x => x.Id)
                    .Take(Math.Min(_options.CleanupBatchSize, toTrim))
                    .ToListAsync(cancellationToken);

                if (trimIds.Count == 0)
                {
                    break;
                }

                var trimBatch = await db.SystemProcessRuns
                    .Where(x => trimIds.Contains(x.Id) && x.CompletedAtUtc != null)
                    .ToListAsync(cancellationToken);

                if (trimBatch.Count == 0)
                {
                    break;
                }

                db.SystemProcessRuns.RemoveRange(trimBatch);
                deleted += trimBatch.Count;
                await db.SaveChangesAsync(cancellationToken);
                toTrim -= trimBatch.Count;
            }
        }

        return deleted;
    }

    private async Task UpdateInternalAsync(
        long processRunId,
        UpdateSystemProcessRunRequest request,
        DateTime? completeAtUtc,
        bool ignoreThrottle,
        CancellationToken cancellationToken)
    {
        try
        {
            var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
            if (!ignoreThrottle && request.Status is null)
            {
                var lastFlush = _lastProgressFlushUtc.GetValueOrDefault(processRunId);
                if (lastFlush != default && (nowUtc - lastFlush) < TimeSpan.FromSeconds(2))
                {
                    return;
                }
            }

            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var run = await db.SystemProcessRuns.FirstOrDefaultAsync(x => x.Id == processRunId, cancellationToken);
            if (run is null)
            {
                return;
            }

            if (TerminalStatuses.Contains(run.Status))
            {
                return;
            }

            if (request.Status == SystemProcessRunStatus.Running)
            {
                if (run.Status is not (SystemProcessRunStatus.Pending or SystemProcessRunStatus.Deferred or SystemProcessRunStatus.Running))
                {
                    return;
                }

                run.StartedAtUtc ??= nowUtc;
                run.Status = SystemProcessRunStatus.Running;
            }
            else if (request.Status.HasValue)
            {
                run.Status = request.Status.Value;
                if (request.Status.Value == SystemProcessRunStatus.Running)
                {
                    run.StartedAtUtc ??= nowUtc;
                }
            }

            if (request.TotalItems.HasValue)
            {
                run.TotalItems = Math.Max(0, request.TotalItems.Value);
            }

            if (request.ProcessedItems.HasValue)
            {
                run.ProcessedItems = Math.Max(0, request.ProcessedItems.Value);
            }

            if (request.SucceededItems.HasValue)
            {
                run.SucceededItems = Math.Max(0, request.SucceededItems.Value);
            }

            if (request.FailedItems.HasValue)
            {
                run.FailedItems = Math.Max(0, request.FailedItems.Value);
            }

            if (request.SkippedItems.HasValue)
            {
                run.SkippedItems = Math.Max(0, request.SkippedItems.Value);
            }

            if (request.ResultSummary is not null)
            {
                run.ResultSummary = NormalizeSafeText(request.ResultSummary, 1000);
            }

            if (request.ErrorSummary is not null)
            {
                run.ErrorSummary = NormalizeSafeText(request.ErrorSummary, 1000);
            }

            if (request.LastProcessedEntity is not null)
            {
                run.LastProcessedEntity = TruncateOrNull(request.LastProcessedEntity, 256);
            }

            if (request.Details is not null)
            {
                run.DetailsJson = SerializeSafeDetails(request.Details);
            }

            if (completeAtUtc.HasValue)
            {
                run.CompletedAtUtc ??= completeAtUtc.Value;
            }

            run.HeartbeatAtUtc = nowUtc;
            run.UpdatedAtUtc = nowUtc;

            await db.SaveChangesAsync(cancellationToken);
            _lastProgressFlushUtc[processRunId] = nowUtc;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "System process journal update failed for runId={RunId}", processRunId);
        }
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static string? TruncateOrNull(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string? NormalizeSafeText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var singleLine = string.Join(' ', value
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        singleLine = SensitivePattern.Replace(singleLine, "$1=[REDACTED]");

        if (singleLine.Contains(" at ", StringComparison.OrdinalIgnoreCase)
            && singleLine.Contains(" in ", StringComparison.OrdinalIgnoreCase)
            && singleLine.Contains(":line ", StringComparison.OrdinalIgnoreCase))
        {
            singleLine = "Техническая ошибка. Подробности скрыты.";
        }

        return singleLine.Length <= maxLength ? singleLine : singleLine[..maxLength];
    }

    private static string? SerializeSafeDetails(object? details)
    {
        if (details is null)
        {
            return null;
        }

        try
        {
            var serialized = JsonSerializer.Serialize(details, SerializerOptions);
            serialized = SensitivePattern.Replace(serialized, "$1=[REDACTED]");
            if (serialized.Length > 4000)
            {
                return serialized[..4000];
            }

            return serialized;
        }
        catch
        {
            return null;
        }
    }
}

public sealed class SystemProcessJournalMaintenanceService : BackgroundService
{
    private readonly ISystemProcessJournalService _journalService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SystemProcessJournalMaintenanceService> _logger;
    private readonly SystemProcessJournalOptions _options;

    public SystemProcessJournalMaintenanceService(
        ISystemProcessJournalService journalService,
        TimeProvider timeProvider,
        IOptions<SystemProcessJournalOptions> options,
        ILogger<SystemProcessJournalMaintenanceService> logger)
    {
        _journalService = journalService;
        _timeProvider = timeProvider;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var reconciled = await _journalService.ReconcileInterruptedAsync(stoppingToken);
            if (reconciled > 0)
            {
                _logger.LogInformation("System process journal reconciled stale runs: {Count}", reconciled);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "System process journal startup reconciliation failed.");
        }

        using var timer = new PeriodicTimer(_options.CleanupInterval > TimeSpan.Zero
            ? _options.CleanupInterval
            : TimeSpan.FromHours(6));

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var deleted = await _journalService.CleanupAsync(stoppingToken);
                if (deleted > 0)
                {
                    _logger.LogInformation("System process journal cleanup removed {Count} rows.", deleted);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "System process journal cleanup failed.");
            }
        }
    }
}
