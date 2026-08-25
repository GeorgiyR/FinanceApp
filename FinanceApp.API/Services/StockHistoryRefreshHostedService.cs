using FinanceApp.Core.Models;

namespace FinanceApp.API.Services;

public class StockHistoryRefreshHostedService : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(30);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StockHistoryRefreshHostedService> _logger;
    private readonly ISystemProcessJournalService? _processJournalService;

    public StockHistoryRefreshHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<StockHistoryRefreshHostedService> logger,
        ISystemProcessJournalService? processJournalService = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _processJournalService = processJournalService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RefreshAllStocksAsync(stoppingToken);

            using var timer = new PeriodicTimer(RefreshInterval);
            while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RefreshAllStocksAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshAllStocksAsync(CancellationToken cancellationToken)
    {
        var processRunId = await (_processJournalService?.CreateOrGetAsync(new CreateSystemProcessRunRequest
        {
            ProcessType = SystemProcessTypes.StockHistoryRefreshCycle,
            Trigger = SystemProcessTrigger.Automatic,
            InitialStatus = SystemProcessRunStatus.Pending,
        }, cancellationToken) ?? Task.FromResult(0L));

        if (processRunId > 0)
        {
            await _processJournalService!.MarkRunningAsync(processRunId, cancellationToken);
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var historyService = scope.ServiceProvider.GetRequiredService<IStockHistoryService>();
            await historyService.SyncHistoricalDataForAllStocksAsync(cancellationToken);

            if (processRunId > 0)
            {
                await _processJournalService!.CompleteAsync(
                    processRunId,
                    SystemProcessRunStatus.Succeeded,
                    new UpdateSystemProcessRunRequest
                    {
                        ResultSummary = "Цикл обновления исторических данных завершён.",
                    },
                    cancellationToken);
            }
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
                        ErrorSummary = "Цикл обновления истории прерван остановкой приложения.",
                    },
                    CancellationToken.None);
            }
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed automatic stock history refresh cycle");
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
        }
    }
}
