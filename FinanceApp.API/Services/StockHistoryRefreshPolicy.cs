namespace FinanceApp.API.Services;

public enum StockHistoryRefreshTrigger
{
    Automatic = 0,
    Manual = 1,
}

public enum StockHistoryRefreshTier
{
    Incremental = 0,
    Reconciliation = 1,
    FullBackfill = 2,
}

public sealed class StockHistoryRefreshOptions
{
    public int IncrementalLookbackDays { get; init; } = 10;
    public int ReconciliationLookbackDays { get; init; } = 183;
    public int FullBackfillLookbackDays { get; init; } = 1830;

    public TimeSpan IncrementalDailyCadence { get; init; } = TimeSpan.FromDays(1);
    public TimeSpan IncrementalWeeklyCadence { get; init; } = TimeSpan.FromDays(7);
    public TimeSpan ReconciliationTrackedCadence { get; init; } = TimeSpan.FromDays(7);
    public TimeSpan ReconciliationCatalogCadence { get; init; } = TimeSpan.FromDays(30);
    public TimeSpan FullBackfillTrackedCadence { get; init; } = TimeSpan.FromDays(30);
    public TimeSpan FullBackfillCatalogCadence { get; init; } = TimeSpan.FromDays(30);
    public TimeSpan TransientFailureRetryDelay { get; init; } = TimeSpan.FromHours(2);
    public TimeSpan OnDemandIntradayRefreshMinInterval { get; init; } = TimeSpan.FromMinutes(10);
    public int MaxAutomaticStocksPerRun { get; init; } = 100;
}
