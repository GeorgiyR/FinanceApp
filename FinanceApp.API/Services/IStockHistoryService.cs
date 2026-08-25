using FinanceApp.API.Models;
using FinanceApp.Core.Models;

namespace FinanceApp.API.Services;

public interface IStockHistoryService
{
    Task SyncHistoricalDataForStockAsync(Stock stock, CancellationToken cancellationToken = default);
    Task SyncHistoricalDataForAllStocksAsync(CancellationToken cancellationToken = default);
    Task<StockHistoryResponse> GetHistoryAsync(Stock stock, string range, CancellationToken cancellationToken = default);
    Task<StockHistoryRefreshResponse> RefreshHistoryAsync(Stock stock, CancellationToken cancellationToken = default);
    Task<StockHistoryRefreshResponse> RefreshHistoryAsync(Stock stock, StockHistoryRefreshTrigger trigger, CancellationToken cancellationToken = default);
    Task<FrankfurtAggregateRebuildResponse> RebuildFrankfurtAggregatesAsync(int batchSize = 25, int? afterStockId = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new FrankfurtAggregateRebuildResponse
        {
            BatchSize = batchSize > 0 ? batchSize : 25,
            StartedAfterStockId = afterStockId,
            NextAfterStockId = afterStockId,
            ProcessedStocks = 0,
            RebuiltStocks = 0,
            FailedStocks = 0,
            StoppedDueToRateLimit = false,
            HasMore = false,
        });
    Task<StockHistoryRepairDiagnosticsResponse> GetRepairDiagnosticsAsync(Stock stock, CancellationToken cancellationToken = default)
        => Task.FromResult(new StockHistoryRepairDiagnosticsResponse
        {
            StockId = stock.Id,
            Ticker = stock.Ticker,
            Exchange = stock.Exchange,
            Name = stock.Name,
            ConfiguredProviderSymbol = stock.ProviderSymbol,
            EffectiveProviderSymbol = stock.ProviderSymbol ?? stock.Ticker,
            ResultBucket = "failed",
            Errors = new[] { "Repair diagnostics are not supported by this implementation." },
        });

    Task<StockHistoryRepairDiagnosticsResponse> ValidateProviderSymbolAsync(Stock stock, string? candidateProviderSymbol, CancellationToken cancellationToken = default)
        => Task.FromResult(new StockHistoryRepairDiagnosticsResponse
        {
            StockId = stock.Id,
            Ticker = stock.Ticker,
            Exchange = stock.Exchange,
            Name = stock.Name,
            ConfiguredProviderSymbol = stock.ProviderSymbol,
            EffectiveProviderSymbol = stock.ProviderSymbol ?? stock.Ticker,
            CandidateOverrideSymbol = candidateProviderSymbol,
            ResultBucket = "failed",
            Errors = new[] { "Provider-symbol validation is not supported by this implementation." },
        });

    Task<StockHistoryRepairDiagnosticsResponse> HardResetHistoryAsync(Stock stock, string? candidateProviderSymbol, CancellationToken cancellationToken = default)
        => Task.FromResult(new StockHistoryRepairDiagnosticsResponse
        {
            StockId = stock.Id,
            Ticker = stock.Ticker,
            Exchange = stock.Exchange,
            Name = stock.Name,
            ConfiguredProviderSymbol = stock.ProviderSymbol,
            EffectiveProviderSymbol = stock.ProviderSymbol ?? stock.Ticker,
            CandidateOverrideSymbol = candidateProviderSymbol,
            ResultBucket = "failed",
            Errors = new[] { "Hard reset is not supported by this implementation." },
        });
}
