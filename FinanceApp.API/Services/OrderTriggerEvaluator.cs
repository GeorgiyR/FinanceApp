using FinanceApp.Core.Models;

namespace FinanceApp.API.Services;

public enum OrderEvaluationAction
{
    None = 0,
    Activate = 1,
    Execute = 2,
}

public sealed record OrderEvaluationResult(
    OrderEvaluationAction Action,
    string? Reason,
    decimal? TrailingReferencePrice)
{
    public static readonly OrderEvaluationResult Nothing = new(OrderEvaluationAction.None, null, null);
}

/// <summary>
/// Pure domain logic that decides whether an order should be activated or executed
/// for the current quote. No database or clock access, which keeps it easy to test.
/// </summary>
public static class OrderTriggerEvaluator
{
    public static OrderEvaluationResult Evaluate(Order order, decimal currentPrice)
    {
        if (currentPrice <= 0m)
        {
            return OrderEvaluationResult.Nothing;
        }

        return order.Kind switch
        {
            OrderKind.Market => Execute(order, currentPrice, "Market order."),
            OrderKind.Limit => EvaluateLimit(order, currentPrice),
            OrderKind.StopMarket => EvaluateStopMarket(order, currentPrice),
            OrderKind.StopLimit => EvaluateStopLimit(order, currentPrice),
            OrderKind.TakeProfit => EvaluateTakeProfit(order, currentPrice),
            OrderKind.TrailingStop => EvaluateTrailingStop(order, currentPrice),
            _ => OrderEvaluationResult.Nothing,
        };
    }

    private static OrderEvaluationResult EvaluateLimit(Order order, decimal currentPrice)
    {
        var limit = order.Price;
        if (limit <= 0m)
        {
            return OrderEvaluationResult.Nothing;
        }

        var hit = order.Side == OrderSide.Buy
            ? currentPrice <= limit
            : currentPrice >= limit;

        return hit
            ? Execute(order, currentPrice, $"Limit {limit:0.##} reached at {currentPrice:0.##}.")
            : OrderEvaluationResult.Nothing;
    }

    private static OrderEvaluationResult EvaluateStopMarket(Order order, decimal currentPrice)
    {
        var stop = order.StopPrice ?? order.StopMarket;
        if (stop is not > 0m)
        {
            return OrderEvaluationResult.Nothing;
        }

        var hit = order.Side == OrderSide.Buy
            ? currentPrice >= stop.Value
            : currentPrice <= stop.Value;

        return hit
            ? Execute(order, currentPrice, $"Stop {stop.Value:0.##} reached at {currentPrice:0.##}.")
            : OrderEvaluationResult.Nothing;
    }

    private static OrderEvaluationResult EvaluateStopLimit(Order order, decimal currentPrice)
    {
        var stop = order.StopPrice;
        if (stop is not > 0m)
        {
            return OrderEvaluationResult.Nothing;
        }

        if (order.Status == OrderStatus.Pending)
        {
            var stopHit = order.Side == OrderSide.Buy
                ? currentPrice >= stop.Value
                : currentPrice <= stop.Value;

            return stopHit
                ? new OrderEvaluationResult(
                    OrderEvaluationAction.Activate,
                    $"Stop {stop.Value:0.##} reached at {currentPrice:0.##}; limit leg activated.",
                    null)
                : OrderEvaluationResult.Nothing;
        }

        // Already triggered: wait for the limit leg.
        var limit = order.Price;
        if (limit <= 0m)
        {
            return OrderEvaluationResult.Nothing;
        }

        var limitHit = order.Side == OrderSide.Buy
            ? currentPrice <= limit
            : currentPrice >= limit;

        return limitHit
            ? Execute(order, currentPrice, $"Stop-limit triggered; limit {limit:0.##} reached at {currentPrice:0.##}.")
            : OrderEvaluationResult.Nothing;
    }

    private static OrderEvaluationResult EvaluateTakeProfit(Order order, decimal currentPrice)
    {
        var target = order.TakeProfitPrice ?? order.Price;
        if (target <= 0m)
        {
            return OrderEvaluationResult.Nothing;
        }

        var hit = order.Side == OrderSide.Sell
            ? currentPrice >= target
            : currentPrice <= target;

        return hit
            ? Execute(order, currentPrice, $"Take profit {target:0.##} reached at {currentPrice:0.##}.")
            : OrderEvaluationResult.Nothing;
    }

    private static OrderEvaluationResult EvaluateTrailingStop(Order order, decimal currentPrice)
    {
        if (order.TrailAmount is not > 0m && order.TrailPercent is not > 0m)
        {
            return OrderEvaluationResult.Nothing;
        }

        var reference = order.TrailingReferencePrice;
        var isSell = order.Side == OrderSide.Sell;

        // Track the high-water mark (sell) or low-water mark (buy).
        if (reference is null || (isSell ? currentPrice > reference.Value : currentPrice < reference.Value))
        {
            reference = currentPrice;
        }

        decimal threshold;
        if (order.TrailAmount is > 0m)
        {
            threshold = isSell ? reference.Value - order.TrailAmount.Value : reference.Value + order.TrailAmount.Value;
        }
        else
        {
            var factor = order.TrailPercent!.Value / 100m;
            threshold = isSell ? reference.Value * (1m - factor) : reference.Value * (1m + factor);
        }

        var triggered = isSell ? currentPrice <= threshold : currentPrice >= threshold;
        var reason = triggered
            ? $"Trailing stop {threshold:0.##} reached at {currentPrice:0.##} (reference {reference.Value:0.##})."
            : null;

        return new OrderEvaluationResult(
            triggered ? OrderEvaluationAction.Execute : OrderEvaluationAction.None,
            reason,
            reference);
    }

    private static OrderEvaluationResult Execute(Order order, decimal price, string reason)
        => new(OrderEvaluationAction.Execute, reason, null);
}
