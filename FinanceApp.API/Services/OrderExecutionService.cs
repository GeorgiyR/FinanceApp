using System.Text.Json;
using System.Text.Json.Serialization;
using FinanceApp.Core.Models;
using FinanceApp.Core.Models.RabbitMQ.Events;
using FinanceApp.Data.Data;
using Microsoft.EntityFrameworkCore;

namespace FinanceApp.API.Services;

public sealed record OrderExecutionResult(bool IsSuccess, string? Error, Order Order)
{
    public static OrderExecutionResult Success(Order order) => new(true, null, order);
    public static OrderExecutionResult Failure(string error, Order order) => new(false, error, order);
}

public interface IOrderExecutionService
{
    Task<OrderExecutionResult> ExecuteAsync(
        Order order,
        decimal fillPrice,
        OrderExecutionSource source,
        string? triggerReason,
        CancellationToken cancellationToken = default);

    Task<decimal> GetAvailableCashAsync(int portfolioId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronizes an order with a manually entered Buy/Sell transaction. Finds a pending
    /// order matching the instrument, quantity and price condition, executes it at the
    /// transaction price, or creates an executed order from the transaction when no match exists.
    /// </summary>
    Task<Order?> SyncFromTransactionAsync(Transaction transaction, CancellationToken cancellationToken = default);
}

public sealed class OrderExecutionService : IOrderExecutionService
{
    public const string OrderExecutedEventType = "order.executed.v1";
    public const string OrderRejectedEventType = "order.rejected.v1";

    private static readonly JsonSerializerOptions EventJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AppDbContext _context;
    private readonly TimeProvider _timeProvider;

    public OrderExecutionService(AppDbContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    public async Task<decimal> GetAvailableCashAsync(int portfolioId, CancellationToken cancellationToken = default)
    {
        var portfolio = await _context.Portfolios
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == portfolioId, cancellationToken);
        if (portfolio is null)
        {
            return 0m;
        }

        var transactions = await _context.Transactions
            .AsNoTracking()
            .Where(t => t.PortfolioId == portfolioId)
            .ToListAsync(cancellationToken);

        return transactions.Sum(t => t.GetEffectiveSignedAmount()) + portfolio.CashBalanceAdjustment;
    }

    public async Task<OrderExecutionResult> ExecuteAsync(
        Order order,
        decimal fillPrice,
        OrderExecutionSource source,
        string? triggerReason,
        CancellationToken cancellationToken = default)
    {
        if (order.Status == OrderStatus.Executed)
        {
            // Idempotent: the linked transaction (unique OrderId) already carries the execution.
            return OrderExecutionResult.Success(order);
        }

        if (order.Status is OrderStatus.Cancelled or OrderStatus.Rejected or OrderStatus.Expired)
        {
            return OrderExecutionResult.Failure($"Order is already {order.Status}.", order);
        }

        if (order.Quantity <= 0m)
        {
            return await RejectAsync(order, "Количество должно быть больше нуля.", cancellationToken);
        }

        if (fillPrice <= 0m)
        {
            return await RejectAsync(order, "Нет корректной цены исполнения.", cancellationToken);
        }

        var item = await _context.PortfolioItems
            .FirstOrDefaultAsync(i => i.PortfolioId == order.PortfolioId && i.StockId == order.StockId, cancellationToken);

        if (source == OrderExecutionSource.Automatic)
        {
            if (order.Side == OrderSide.Sell)
            {
                if (item is null || item.Quantity < order.Quantity)
                {
                    return await RejectAsync(order, "Недостаточно бумаг в позиции для автоматического исполнения.", cancellationToken);
                }
            }
            else
            {
                var total = fillPrice * order.Quantity;
                var cash = await GetAvailableCashAsync(order.PortfolioId, cancellationToken);
                if (cash < total)
                {
                    return await RejectAsync(order, "Недостаточно денежных средств для автоматического исполнения.", cancellationToken);
                }
            }
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        if (item is not null)
        {
            if (order.Side == OrderSide.Buy)
            {
                item.Quantity += order.Quantity;
            }
            else
            {
                item.Quantity -= order.Quantity;
            }

            if (item.Quantity <= 0m)
            {
                _context.PortfolioItems.Remove(item);
            }
        }
        else if (order.Side == OrderSide.Buy)
        {
            _context.PortfolioItems.Add(new PortfolioItem
            {
                PortfolioId = order.PortfolioId,
                StockId = order.StockId,
                Quantity = order.Quantity,
                BuyPrice = fillPrice,
                BoughtAt = now,
            });

            if (order.Stock is not null && order.Stock.PurchaseCandidatePriority != StockPurchaseCandidatePriority.None)
            {
                order.Stock.PurchaseCandidatePriority = StockPurchaseCandidatePriority.None;
                order.Stock.UpdatedAt = now;
            }
        }

        var alreadyLinked = await _context.Transactions.AnyAsync(t => t.OrderId == order.Id, cancellationToken);
        if (!alreadyLinked)
        {
            var txType = order.Side == OrderSide.Buy ? TransactionType.Buy : TransactionType.Sell;
            var totalAmount = fillPrice * order.Quantity;
            var signedAmount = TransactionDirection.DeriveSignedAmount(txType, totalAmount);
            var stockName = order.Stock?.Name ?? string.Empty;
            var stockTicker = order.Stock?.Ticker ?? string.Empty;
            var typeLabel = order.Side == OrderSide.Buy ? "Покупка" : "Продажа";
            var description = $"{typeLabel} — {stockTicker} · {stockName}";
            var (instrumentCode, instrumentCodeType) = TransactionInstrumentSnapshot.ResolveFromStock(order.Stock);

            _context.Transactions.Add(new Transaction
            {
                PortfolioId = order.PortfolioId,
                Type = txType,
                Amount = totalAmount,
                SignedAmount = signedAmount,
                StockId = order.StockId,
                OrderId = order.Id,
                Description = description,
                CreatedAt = now,
                InstrumentCode = instrumentCode,
                InstrumentCodeType = instrumentCodeType,
                Quantity = order.Quantity,
                UnitPrice = fillPrice,
            });
        }

        order.Status = OrderStatus.Executed;
        order.FilledQuantity = order.Quantity;
        order.AverageFillPrice = fillPrice;
        order.ExecutedAt = now;
        order.UpdatedAt = now;
        order.ExecutionSource = source;
        order.RejectReason = null;

        await AddOutboxEventAsync(order, OrderExecutedEventType, triggerReason, source, fillPrice, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        return OrderExecutionResult.Success(order);
    }

    public async Task<Order?> SyncFromTransactionAsync(
        Transaction transaction,
        CancellationToken cancellationToken = default)
    {
        if (transaction is null) return null;
        if (transaction.OrderId.HasValue) return null; // already linked
        if (transaction.StockId is not > 0) return null;
        if (transaction.Type is not (TransactionType.Buy or TransactionType.Sell)) return null;
        var quantity = transaction.Quantity ?? 0m;
        if (quantity <= 0m) return null;

        var side = transaction.Type == TransactionType.Buy ? OrderSide.Buy : OrderSide.Sell;
        var unitPrice = transaction.UnitPrice ?? 0m;
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var candidates = await _context.Orders
            .Include(o => o.Stock)
            .Where(o => o.PortfolioId == transaction.PortfolioId
                && o.Side == side
                && (o.Status == OrderStatus.Pending || o.Status == OrderStatus.Triggered)
                && o.Quantity == quantity)
            .OrderBy(o => o.Id)
            .ToListAsync(cancellationToken);

        var match = candidates.FirstOrDefault(o =>
            SameInstrument(o.Stock, transaction) && ConditionMatches(o, unitPrice));

        if (match is not null)
        {
            var fillPrice = transaction.UnitPrice ?? match.Price;
            if (fillPrice <= 0m)
            {
                return null;
            }

            match.Status = OrderStatus.Executed;
            match.FilledQuantity = match.Quantity;
            match.AverageFillPrice = fillPrice;
            if (match.Kind == OrderKind.Market)
            {
                match.Price = fillPrice;
            }
            match.ExecutedAt = transaction.CreatedAt;
            match.UpdatedAt = now;
            match.ExecutionSource = OrderExecutionSource.Manual;
            match.RejectReason = null;

            transaction.OrderId = match.Id;
            await AddOutboxEventAsync(
                match,
                OrderExecutedEventType,
                "Синхронизировано с транзакцией.",
                OrderExecutionSource.Manual,
                fillPrice,
                cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);

            return match;
        }

        var order = new Order
        {
            PortfolioId = transaction.PortfolioId,
            StockId = transaction.StockId.Value,
            Side = side,
            Kind = unitPrice > 0m ? OrderKind.Limit : OrderKind.Market,
            TimeInForce = TimeInForce.Gtc,
            Status = OrderStatus.Executed,
            Quantity = quantity,
            FilledQuantity = quantity,
            Price = unitPrice,
            AverageFillPrice = unitPrice > 0m ? unitPrice : null,
            CreatedAt = transaction.CreatedAt,
            ExecutedAt = transaction.CreatedAt,
            ExecutionSource = OrderExecutionSource.Manual,
        };

        _context.Orders.Add(order);
        await _context.SaveChangesAsync(cancellationToken); // assigns the identity id

        transaction.OrderId = order.Id;
        await AddOutboxEventAsync(
            order,
            OrderExecutedEventType,
            "Ордер создан на основе транзакции.",
            OrderExecutionSource.Manual,
            unitPrice > 0m ? unitPrice : null,
            cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        return order;
    }

    private static bool SameInstrument(Stock? stock, Transaction transaction)
    {
        if (stock is null) return false;

        if (transaction.StockId is > 0 && stock.Id == transaction.StockId.Value)
        {
            return true;
        }

        if (transaction.InstrumentCodeType == InstrumentCodeType.ISIN &&
            !string.IsNullOrWhiteSpace(stock.Isin))
        {
            var left = NormalizeIdentifier(transaction.InstrumentCode);
            var right = NormalizeIdentifier(stock.Isin);
            return left.Length > 0 && left == right;
        }

        return false;
    }

    private static string NormalizeIdentifier(string? value)
        => value?.Trim().ToUpperInvariant() ?? string.Empty;

    private static bool ConditionMatches(Order order, decimal unitPrice)
    {
        if (unitPrice <= 0m)
        {
            return order.Kind == OrderKind.Market;
        }

        return OrderTriggerEvaluator.Evaluate(order, unitPrice).Action != OrderEvaluationAction.None;
    }

    private async Task<OrderExecutionResult> RejectAsync(Order order, string reason, CancellationToken cancellationToken)
    {
        order.Status = OrderStatus.Rejected;
        order.RejectedAt ??= _timeProvider.GetUtcNow().UtcDateTime;
        order.RejectReason = reason;
        order.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;

        await AddOutboxEventAsync(order, OrderRejectedEventType, reason, OrderExecutionSource.Automatic, order.AverageFillPrice, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        return OrderExecutionResult.Failure(reason, order);
    }

    private async Task AddOutboxEventAsync(
        Order order,
        string eventType,
        string? triggerReason,
        OrderExecutionSource source,
        decimal? fillPrice,
        CancellationToken cancellationToken)
    {
        var userId = order.Portfolio?.UserId
            ?? await _context.Portfolios
                .Where(p => p.Id == order.PortfolioId)
                .Select(p => (int?)p.UserId)
                .FirstOrDefaultAsync(cancellationToken)
            ?? 0;

        var payload = new OrderExecutedEvent(
            Guid.NewGuid(),
            order.Id,
            order.PortfolioId,
            userId,
            order.StockId,
            order.Stock?.Ticker ?? string.Empty,
            order.Stock?.Name ?? string.Empty,
            order.Side,
            order.Kind,
            order.Quantity,
            fillPrice ?? 0m,
            (fillPrice ?? 0m) * order.Quantity,
            order.ExecutedAt ?? _timeProvider.GetUtcNow().UtcDateTime,
            source,
            triggerReason);

        _context.OrderEventsOutbox.Add(new OrderEventOutbox
        {
            EventId = payload.EventId,
            EventType = eventType,
            OrderId = order.Id,
            PortfolioId = order.PortfolioId,
            Payload = JsonSerializer.Serialize(payload, EventJsonOptions),
            OccurredAt = _timeProvider.GetUtcNow().UtcDateTime,
        });
    }
}
