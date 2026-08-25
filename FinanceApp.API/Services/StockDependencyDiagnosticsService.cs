using FinanceApp.API.Models;
using FinanceApp.Data.Data;
using Microsoft.EntityFrameworkCore;

namespace FinanceApp.API.Services;

public interface IStockDependencyDiagnosticsService
{
    Task<StockDependencyDiagnosticsResponse> GetDiagnosticsAsync(int stockId, CancellationToken cancellationToken = default);
}

public sealed class StockDependencyDiagnosticsService(AppDbContext dbContext) : IStockDependencyDiagnosticsService
{
    public async Task<StockDependencyDiagnosticsResponse> GetDiagnosticsAsync(int stockId, CancellationToken cancellationToken = default)
    {
        var blockers = new List<StockDependencyBlockerResponse>();

        var portfolioNames = await dbContext.PortfolioItems
            .AsNoTracking()
            .Where(x => x.StockId == stockId)
            .Select(x => x.Portfolio.Name)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(cancellationToken);
        if (portfolioNames.Count > 0)
        {
            blockers.Add(new StockDependencyBlockerResponse
            {
                Category = "portfolioItems",
                DisplayName = "Позиции в портфелях",
                Count = portfolioNames.Count,
                RelatedNames = portfolioNames,
            });
        }

        var transactionPortfolioNames = await dbContext.Transactions
            .AsNoTracking()
            .Where(x => x.StockId == stockId)
            .Select(x => x.Portfolio.Name)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(cancellationToken);
        if (transactionPortfolioNames.Count > 0)
        {
            blockers.Add(new StockDependencyBlockerResponse
            {
                Category = "transactions",
                DisplayName = "Транзакции",
                Count = transactionPortfolioNames.Count,
                RelatedNames = transactionPortfolioNames,
            });
        }

        var orderPortfolioNames = await dbContext.Orders
            .AsNoTracking()
            .Where(x => x.StockId == stockId)
            .Select(x => x.Portfolio.Name)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(cancellationToken);
        if (orderPortfolioNames.Count > 0)
        {
            blockers.Add(new StockDependencyBlockerResponse
            {
                Category = "orders",
                DisplayName = "Ордера",
                Count = orderPortfolioNames.Count,
                RelatedNames = orderPortfolioNames,
            });
        }

        var dividendPortfolioNames = await dbContext.Dividends
            .AsNoTracking()
            .Where(x => x.StockId == stockId)
            .Select(x => x.Portfolio.Name)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(cancellationToken);
        if (dividendPortfolioNames.Count > 0)
        {
            blockers.Add(new StockDependencyBlockerResponse
            {
                Category = "dividends",
                DisplayName = "Дивиденды",
                Count = dividendPortfolioNames.Count,
                RelatedNames = dividendPortfolioNames,
            });
        }

        var activeIndexNames = await dbContext.StockMarketIndices
            .AsNoTracking()
            .Where(x => x.StockId == stockId && x.EffectiveTo == null)
            .Select(x => x.MarketIndex.Name)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(cancellationToken);
        if (activeIndexNames.Count > 0)
        {
            blockers.Add(new StockDependencyBlockerResponse
            {
                Category = "activeIndexMemberships",
                DisplayName = "Текущие включения в индексы",
                Count = activeIndexNames.Count,
                RelatedNames = activeIndexNames,
            });
        }

        var historicalIndexNames = await dbContext.StockMarketIndices
            .AsNoTracking()
            .Where(x => x.StockId == stockId && x.EffectiveTo != null)
            .Select(x => x.MarketIndex.Name)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(cancellationToken);
        if (historicalIndexNames.Count > 0)
        {
            blockers.Add(new StockDependencyBlockerResponse
            {
                Category = "historicalIndexMemberships",
                DisplayName = "Исторические включения в индексы",
                Count = historicalIndexNames.Count,
                RelatedNames = historicalIndexNames,
            });
        }

        return new StockDependencyDiagnosticsResponse
        {
            StockId = stockId,
            HasBlockers = blockers.Count > 0,
            Blockers = blockers,
        };
    }
}
