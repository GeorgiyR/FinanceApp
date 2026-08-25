using System.Collections.Concurrent;
using System.Threading.Channels;
using FinanceApp.API.Models;
using FinanceApp.Core.Models;
using FinanceApp.Data.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinanceApp.API.Services;

public sealed class IndexConstituentHistoryRefreshJobOptions
{
    public int QueueCapacity { get; init; } = 64;
    public int RegistryCapacity { get; init; } = 512;
    public TimeSpan CompletedJobTtl { get; init; } = TimeSpan.FromMinutes(30);
    public int MaxErrorMessageLength { get; init; } = 240;
}

public enum IndexConstituentHistoryRefreshJobEnqueueStatus
{
    Enqueued,
    ReusedActiveJob,
    QueueFull
}

public sealed class IndexConstituentHistoryRefreshJobEnqueueResult
{
    public required IndexConstituentHistoryRefreshJobEnqueueStatus Status { get; init; }
    public IndexConstituentHistoryRefreshJobResponse? Job { get; init; }
}

public interface IIndexConstituentHistoryRefreshJobService
{
    IndexConstituentHistoryRefreshJobEnqueueResult Enqueue(int marketIndexId, int stockId);
    bool TryGetJob(int marketIndexId, int stockId, string jobId, out IndexConstituentHistoryRefreshJobResponse? job);
}

public sealed class IndexConstituentHistoryRefreshJobService : BackgroundService, IIndexConstituentHistoryRefreshJobService
{
    private const string RateLimitMessage = "Поставщик временно ограничил запросы.";
    private const string InterruptedMessage = "Обновление прервано из-за перезапуска приложения. Повторите попытку.";
    private const string GenericFailureMessage = "Не удалось обновить исторические данные акции. Попробуйте позже.";

    private readonly object _sync = new();
    private readonly Dictionary<string, JobEntry> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _activeJobsByStockId = new();
    private readonly Channel<JobWorkItem> _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IndexConstituentHistoryRefreshJobOptions _options;
    private readonly ILogger<IndexConstituentHistoryRefreshJobService> _logger;
    private readonly ISystemProcessJournalService? _processJournalService;

    public IndexConstituentHistoryRefreshJobService(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        IOptions<IndexConstituentHistoryRefreshJobOptions> options,
        ILogger<IndexConstituentHistoryRefreshJobService> logger,
        ISystemProcessJournalService? processJournalService = null)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
        _processJournalService = processJournalService;

        var rawOptions = options.Value;
        _options = new IndexConstituentHistoryRefreshJobOptions
        {
            QueueCapacity = rawOptions.QueueCapacity > 0 ? rawOptions.QueueCapacity : 64,
            RegistryCapacity = rawOptions.RegistryCapacity > 0 ? rawOptions.RegistryCapacity : 512,
            CompletedJobTtl = rawOptions.CompletedJobTtl > TimeSpan.Zero
                ? rawOptions.CompletedJobTtl
                : TimeSpan.FromMinutes(30),
            MaxErrorMessageLength = rawOptions.MaxErrorMessageLength > 0 ? rawOptions.MaxErrorMessageLength : 240
        };

        _queue = Channel.CreateBounded<JobWorkItem>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public IndexConstituentHistoryRefreshJobEnqueueResult Enqueue(int marketIndexId, int stockId)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        JobEntry entry;
        lock (_sync)
        {
            CleanupLocked(now);

            if (_activeJobsByStockId.TryGetValue(stockId, out var activeJobId)
                && _jobs.TryGetValue(activeJobId, out var activeEntry)
                && activeEntry.State is IndexConstituentHistoryRefreshJobState.Queued or IndexConstituentHistoryRefreshJobState.Running)
            {
                return new IndexConstituentHistoryRefreshJobEnqueueResult
                {
                    Status = IndexConstituentHistoryRefreshJobEnqueueStatus.ReusedActiveJob,
                    Job = ToResponse(activeEntry, reusedActiveJob: true)
                };
            }

            if (_jobs.Count >= _options.RegistryCapacity && !TryEvictOneTerminalJobLocked())
            {
                return new IndexConstituentHistoryRefreshJobEnqueueResult
                {
                    Status = IndexConstituentHistoryRefreshJobEnqueueStatus.QueueFull
                };
            }

            entry = new JobEntry
            {
                JobId = Guid.NewGuid().ToString("N"),
                MarketIndexId = marketIndexId,
                StockId = stockId,
                State = IndexConstituentHistoryRefreshJobState.Queued,
                CreatedAtUtc = now
            };
            _jobs[entry.JobId] = entry;
            _activeJobsByStockId[stockId] = entry.JobId;
        }

        if (!_queue.Writer.TryWrite(new JobWorkItem(entry.JobId, entry.MarketIndexId, entry.StockId)))
        {
            lock (_sync)
            {
                RemoveJobLocked(entry.JobId);
            }

            return new IndexConstituentHistoryRefreshJobEnqueueResult
            {
                Status = IndexConstituentHistoryRefreshJobEnqueueStatus.QueueFull
            };
        }

        return new IndexConstituentHistoryRefreshJobEnqueueResult
        {
            Status = IndexConstituentHistoryRefreshJobEnqueueStatus.Enqueued,
            Job = ToResponse(entry, reusedActiveJob: false)
        };
    }

    public bool TryGetJob(int marketIndexId, int stockId, string jobId, out IndexConstituentHistoryRefreshJobResponse? job)
    {
        job = null;
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        lock (_sync)
        {
            CleanupLocked(now);

            if (!_jobs.TryGetValue(jobId, out var entry))
            {
                return false;
            }

            if (entry.MarketIndexId != marketIndexId || entry.StockId != stockId)
            {
                return false;
            }

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
                while (_queue.Reader.TryRead(out var workItem))
                {
                    await ProcessWorkItemAsync(workItem, stoppingToken);
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

    private async Task ProcessWorkItemAsync(JobWorkItem workItem, CancellationToken stoppingToken)
    {
        if (!TryMarkRunning(workItem.JobId))
        {
            return;
        }

        var processRunId = await (_processJournalService?.CreateOrGetAsync(new CreateSystemProcessRunRequest
        {
            ProcessType = SystemProcessTypes.IndexConstituentHistoryRefresh,
            Trigger = SystemProcessTrigger.Automatic,
            ExternalRunKey = workItem.JobId,
            CorrelationId = workItem.JobId,
            InitialStatus = SystemProcessRunStatus.Running,
            Details = new { workItem.MarketIndexId, workItem.StockId, workItem.JobId },
        }, stoppingToken) ?? Task.FromResult(0L));

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var historyService = scope.ServiceProvider.GetRequiredService<IStockHistoryService>();

            var marketIndex = await context.MarketIndices
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == workItem.MarketIndexId, stoppingToken);
            if (marketIndex is null)
            {
                MarkCompleted(workItem.JobId, IndexConstituentHistoryRefreshJobState.Failed, error: "Индекс не найден.");
                await CompleteJournalForJobAsync(processRunId, IndexConstituentHistoryRefreshJobState.Failed, 0, 0, "Индекс не найден.");
                return;
            }

            if (marketIndex.IsArchived)
            {
                MarkCompleted(workItem.JobId, IndexConstituentHistoryRefreshJobState.Failed, error: "Нельзя обновлять историю акций для архивного индекса.");
                await CompleteJournalForJobAsync(processRunId, IndexConstituentHistoryRefreshJobState.Failed, 0, 0, "Нельзя обновлять историю акций для архивного индекса.");
                return;
            }

            var membership = await context.StockMarketIndices
                .Include(x => x.Stock)
                .FirstOrDefaultAsync(
                    x => x.MarketIndexId == workItem.MarketIndexId
                         && x.StockId == workItem.StockId
                         && x.EffectiveTo == null,
                    stoppingToken);

            if (membership?.Stock is null)
            {
                MarkCompleted(workItem.JobId, IndexConstituentHistoryRefreshJobState.Failed, error: "Акция не входит в текущий состав выбранного индекса.");
                await CompleteJournalForJobAsync(processRunId, IndexConstituentHistoryRefreshJobState.Failed, 0, 0, "Акция не входит в текущий состав выбранного индекса.");
                return;
            }

            var stock = membership.Stock;
            if (!TryValidateTickerAndExchange(stock, out var validationError))
            {
                MarkCompleted(workItem.JobId, IndexConstituentHistoryRefreshJobState.Failed, error: validationError);
                await CompleteJournalForJobAsync(processRunId, IndexConstituentHistoryRefreshJobState.Failed, 0, 0, validationError);
                return;
            }

            var result = await historyService.RefreshHistoryAsync(stock, stoppingToken);
            if (result.RateLimited)
            {
                MarkCompleted(
                    workItem.JobId,
                    IndexConstituentHistoryRefreshJobState.RateLimited,
                    deletedPoints: result.DeletedPoints,
                    importedPoints: result.ImportedPoints,
                    error: RateLimitMessage);
                await CompleteJournalForJobAsync(processRunId, IndexConstituentHistoryRefreshJobState.RateLimited, result.ImportedPoints, result.DeletedPoints, RateLimitMessage);
                return;
            }

            MarkCompleted(
                workItem.JobId,
                IndexConstituentHistoryRefreshJobState.Succeeded,
                deletedPoints: result.DeletedPoints,
                importedPoints: result.ImportedPoints);
            await CompleteJournalForJobAsync(processRunId, IndexConstituentHistoryRefreshJobState.Succeeded, result.ImportedPoints, result.DeletedPoints, null);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            MarkCompleted(workItem.JobId, IndexConstituentHistoryRefreshJobState.Interrupted, error: InterruptedMessage);
            await CompleteJournalForJobAsync(processRunId, IndexConstituentHistoryRefreshJobState.Interrupted, 0, 0, InterruptedMessage);
        }
        catch (InvalidOperationException ex)
        {
            MarkCompleted(workItem.JobId, IndexConstituentHistoryRefreshJobState.Failed, error: ex.Message);
            await CompleteJournalForJobAsync(processRunId, IndexConstituentHistoryRefreshJobState.Failed, 0, 0, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unexpected failure in constituent history refresh job {JobId} (indexId={IndexId}, stockId={StockId})",
                workItem.JobId,
                workItem.MarketIndexId,
                workItem.StockId);
            MarkCompleted(workItem.JobId, IndexConstituentHistoryRefreshJobState.Failed, error: GenericFailureMessage);
            await CompleteJournalForJobAsync(processRunId, IndexConstituentHistoryRefreshJobState.Failed, 0, 0, GenericFailureMessage);
        }
    }

    private async Task CompleteJournalForJobAsync(
        long processRunId,
        IndexConstituentHistoryRefreshJobState state,
        int importedPoints,
        int deletedPoints,
        string? error)
    {
        if (_processJournalService is null || processRunId <= 0)
        {
            return;
        }

        var status = state switch
        {
            IndexConstituentHistoryRefreshJobState.Succeeded => SystemProcessRunStatus.Succeeded,
            IndexConstituentHistoryRefreshJobState.RateLimited => SystemProcessRunStatus.Deferred,
            IndexConstituentHistoryRefreshJobState.Interrupted => SystemProcessRunStatus.Interrupted,
            _ => SystemProcessRunStatus.Failed,
        };

        await _processJournalService.CompleteAsync(
            processRunId,
            status,
            new UpdateSystemProcessRunRequest
            {
                ProcessedItems = 1,
                SucceededItems = state == IndexConstituentHistoryRefreshJobState.Succeeded ? 1 : 0,
                FailedItems = state is IndexConstituentHistoryRefreshJobState.Failed or IndexConstituentHistoryRefreshJobState.Interrupted ? 1 : 0,
                ResultSummary = $"Импортировано {importedPoints}; удалено {deletedPoints}.",
                ErrorSummary = error,
            },
            CancellationToken.None);
    }

    private bool TryMarkRunning(string jobId)
    {
        lock (_sync)
        {
            if (!_jobs.TryGetValue(jobId, out var entry))
            {
                return false;
            }

            if (entry.State != IndexConstituentHistoryRefreshJobState.Queued)
            {
                return false;
            }

            entry.State = IndexConstituentHistoryRefreshJobState.Running;
            entry.StartedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
            return true;
        }
    }

    private void MarkCompleted(
        string jobId,
        IndexConstituentHistoryRefreshJobState state,
        int deletedPoints = 0,
        int importedPoints = 0,
        string? error = null)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        lock (_sync)
        {
            if (!_jobs.TryGetValue(jobId, out var entry))
            {
                return;
            }

            entry.State = state;
            entry.DeletedPoints = deletedPoints;
            entry.ImportedPoints = importedPoints;
            entry.Error = ToSafeError(error);
            entry.CompletedAtUtc = now;
            entry.ExpiresAtUtc = now.Add(_options.CompletedJobTtl);

            if (_activeJobsByStockId.TryGetValue(entry.StockId, out var currentJobId)
                && string.Equals(currentJobId, entry.JobId, StringComparison.Ordinal))
            {
                _activeJobsByStockId.Remove(entry.StockId);
            }

            CleanupLocked(now);
        }
    }

    private void InterruptActiveJobs(string error)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        lock (_sync)
        {
            foreach (var entry in _jobs.Values.Where(x =>
                         x.State is IndexConstituentHistoryRefreshJobState.Queued or IndexConstituentHistoryRefreshJobState.Running))
            {
                entry.State = IndexConstituentHistoryRefreshJobState.Interrupted;
                entry.Error = ToSafeError(error);
                entry.CompletedAtUtc = now;
                entry.ExpiresAtUtc = now.Add(_options.CompletedJobTtl);
            }

            _activeJobsByStockId.Clear();
            CleanupLocked(now);
        }
    }

    private void CleanupLocked(DateTime now)
    {
        foreach (var entry in _jobs.Values.Where(x => x.ExpiresAtUtc.HasValue && x.ExpiresAtUtc.Value <= now).ToList())
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
        if (candidate is null)
        {
            return false;
        }

        RemoveJobLocked(candidate.JobId);
        return true;
    }

    private void RemoveJobLocked(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var entry))
        {
            return;
        }

        _jobs.Remove(jobId);
        if (_activeJobsByStockId.TryGetValue(entry.StockId, out var currentJobId)
            && string.Equals(currentJobId, jobId, StringComparison.Ordinal))
        {
            _activeJobsByStockId.Remove(entry.StockId);
        }
    }

    private string? ToSafeError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return null;
        }

        var trimmed = error.Trim();
        if (trimmed.Length <= _options.MaxErrorMessageLength)
        {
            return trimmed;
        }

        return trimmed[.._options.MaxErrorMessageLength];
    }

    private static bool TryValidateTickerAndExchange(Stock stock, out string? validationError)
    {
        if (string.IsNullOrWhiteSpace(stock.Ticker))
        {
            validationError = "У акции должен быть указан тикер для обновления исторических данных.";
            return false;
        }

        if (!StockExchanges.TryNormalize(stock.Exchange, out var normalizedExchange))
        {
            validationError = "У акции указана некорректная биржа для обновления исторических данных.";
            return false;
        }

        stock.Exchange = normalizedExchange;
        validationError = null;
        return true;
    }

    private static IndexConstituentHistoryRefreshJobResponse ToResponse(JobEntry entry, bool reusedActiveJob)
        => new()
        {
            JobId = entry.JobId,
            MarketIndexId = entry.MarketIndexId,
            StockId = entry.StockId,
            State = entry.State,
            ReusedActiveJob = reusedActiveJob,
            CreatedAtUtc = entry.CreatedAtUtc,
            StartedAtUtc = entry.StartedAtUtc,
            CompletedAtUtc = entry.CompletedAtUtc,
            ExpiresAtUtc = entry.ExpiresAtUtc,
            DeletedPoints = entry.DeletedPoints,
            ImportedPoints = entry.ImportedPoints,
            Error = entry.Error
        };

    private sealed class JobEntry
    {
        public required string JobId { get; init; }
        public required int MarketIndexId { get; init; }
        public required int StockId { get; init; }
        public required DateTime CreatedAtUtc { get; init; }
        public IndexConstituentHistoryRefreshJobState State { get; set; }
        public DateTime? StartedAtUtc { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
        public DateTime? ExpiresAtUtc { get; set; }
        public int DeletedPoints { get; set; }
        public int ImportedPoints { get; set; }
        public string? Error { get; set; }
    }

    private readonly record struct JobWorkItem(string JobId, int MarketIndexId, int StockId);
}
