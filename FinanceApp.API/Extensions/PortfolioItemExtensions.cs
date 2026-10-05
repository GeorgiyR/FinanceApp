using Microsoft.EntityFrameworkCore;
using FinanceApp.Core.Models;
using FinanceApp.Data.Data;

namespace FinanceApp.API.Extensions;

public static class PortfolioItemExtensions
{
    /// <summary>
    /// Update portfolio items based on a transaction. Handles Buy, Sell and Dividend types.
    /// This is a best-effort implementation similar to OrdersController logic used when orders execute.
    /// </summary>
    public static void UpdatePortfolioItemsForTransaction(
    this AppDbContext context,
    int portfolioId,
    Transaction transaction)
    {
        if (transaction == null)
            return;

        if (!transaction.StockId.HasValue)
            return;

        if (transaction.Quantity.GetValueOrDefault() <= 0)
            return;

        var portfolioItem = context.PortfolioItems
            .FirstOrDefault(pi =>
                pi.PortfolioId == portfolioId &&
                pi.StockId == transaction.StockId.Value);

        switch (transaction.Type)
        {
            case TransactionType.Buy:
                {
                    if (portfolioItem != null)
                    {
                        var oldQuantity = portfolioItem.Quantity;
                        var newQuantity = oldQuantity + transaction.Quantity!.Value;

                        portfolioItem.BuyPrice =
                            ((portfolioItem.BuyPrice * oldQuantity) +
                             ((transaction.UnitPrice ?? 0m) * transaction.Quantity.Value))
                            / newQuantity;

                        portfolioItem.Quantity = newQuantity;
                    }
                    else
                    {
                        context.PortfolioItems.Add(new PortfolioItem
                        {
                            PortfolioId = portfolioId,
                            StockId = transaction.StockId.Value,
                            Quantity = transaction.Quantity.Value,
                            BuyPrice = transaction.UnitPrice ?? 0m,
                            BoughtAt = transaction.CreatedAt
                        });
                    }

                    break;
                }

            case TransactionType.Sell:
                {
                    if (portfolioItem == null)
                        return;

                    portfolioItem.Quantity -= transaction.Quantity!.Value;

                    if (portfolioItem.Quantity <= 0)
                    {
                        context.PortfolioItems.Remove(portfolioItem);
                    }

                    break;
                }

            case TransactionType.Dividend:
            case TransactionType.Deposit:
            case TransactionType.Withdrawal:
                break;
        }
    }
}
