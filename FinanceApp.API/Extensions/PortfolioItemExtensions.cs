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
    public static void UpdatePortfolioItemsForTransaction(this AppDbContext context, int portfolioId, Transaction transaction)
    {
        if (transaction == null) return;

        var portfolioItems = context.PortfolioItems;

        // Try to find existing tracked entity first
        var existing = context.ChangeTracker.Entries<PortfolioItem>()
            .Select(e => e.Entity)
            .FirstOrDefault(pi => pi.PortfolioId == portfolioId && pi.StockId == transaction.StockId);

        // If not tracked, attempt to find in local cache
        if (existing == null && transaction.StockId.HasValue)
        {
            existing = portfolioItems.Local.FirstOrDefault(pi => pi.PortfolioId == portfolioId && pi.StockId == transaction.StockId);
        }

        // Determine behavior based on transaction type
        if (transaction.Type == TransactionType.Buy)
        {
            if (transaction.Quantity.GetValueOrDefault() <= 0)
            {
                // For pure cash buys without quantity, nothing to update for positions
                return;
            }

            if (existing != null)
            {
                existing.Quantity += transaction.Quantity!.Value;
                existing.BuyPrice = (existing.BuyPrice + transaction.UnitPrice * transaction.Quantity!.Value) / existing.Quantity ?? 0;
            }
            else
            {
                // Create new portfolio item
                var newItem = new PortfolioItem
                {
                    PortfolioId = portfolioId,
                    StockId = transaction.StockId!.Value,
                    Quantity = transaction.Quantity!.Value,
                    BuyPrice = transaction.UnitPrice ?? 0m,
                    BoughtAt = transaction.CreatedAt
                };
                portfolioItems.Add(newItem);
            }
        }
        else if (transaction.Type == TransactionType.Sell)
        {
            if (!transaction.StockId.HasValue || transaction.Quantity.GetValueOrDefault() <= 0)
                return;

            if (existing != null)
            {
                existing.Quantity -= transaction.Quantity!.Value;

                if (existing.Quantity <= 0)
                {
                    context.Remove(existing);
                } 
               
            }
            // If no existing item and sell transaction, nothing to do (position already absent)
        }
        else if (transaction.Type == TransactionType.Dividend)
        {
            // Dividends don't change quantities; no portfolio item update required.
            return;
        }
        // For other transaction types (Deposit, Withdrawal) no position updates are needed.
    }
}
