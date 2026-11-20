using FinanceApp.API.Services;
using FinanceApp.Core.Models;
using Xunit;

namespace FinanceApp.Core.Tests;

public class OrderTriggerEvaluatorTests
{
    [Fact]
    public void Market_IsAlwaysExecuted()
    {
        var order = NewOrder(OrderSide.Buy, OrderKind.Market);

        var result = OrderTriggerEvaluator.Evaluate(order, 100m);

        Assert.Equal(OrderEvaluationAction.Execute, result.Action);
    }

    [Theory]
    [InlineData(OrderSide.Buy, 95, 94, OrderEvaluationAction.Execute)]
    [InlineData(OrderSide.Buy, 95, 96, OrderEvaluationAction.None)]
    [InlineData(OrderSide.Sell, 105, 106, OrderEvaluationAction.Execute)]
    [InlineData(OrderSide.Sell, 105, 104, OrderEvaluationAction.None)]
    public void Limit_TriggersWhenPriceCrossesLimit(
        OrderSide side,
        decimal limit,
        decimal current,
        OrderEvaluationAction expected)
    {
        var order = NewOrder(side, OrderKind.Limit);
        order.Price = limit;

        var result = OrderTriggerEvaluator.Evaluate(order, current);

        Assert.Equal(expected, result.Action);
    }

    [Theory]
    [InlineData(OrderSide.Buy, 110, 111, OrderEvaluationAction.Execute)]
    [InlineData(OrderSide.Buy, 110, 109, OrderEvaluationAction.None)]
    [InlineData(OrderSide.Sell, 90, 89, OrderEvaluationAction.Execute)]
    [InlineData(OrderSide.Sell, 90, 91, OrderEvaluationAction.None)]
    public void StopMarket_TriggersWhenStopCrossed(
        OrderSide side,
        decimal stop,
        decimal current,
        OrderEvaluationAction expected)
    {
        var order = NewOrder(side, OrderKind.StopMarket);
        order.StopPrice = stop;

        var result = OrderTriggerEvaluator.Evaluate(order, current);

        Assert.Equal(expected, result.Action);
    }

    [Fact]
    public void StopLimit_ActivatesOnStop_ThenExecutesOnLimit()
    {
        var order = NewOrder(OrderSide.Buy, OrderKind.StopLimit);
        order.StopPrice = 110m;
        order.Price = 105m;

        var activation = OrderTriggerEvaluator.Evaluate(order, 111m);
        Assert.Equal(OrderEvaluationAction.Activate, activation.Action);

        order.Status = OrderStatus.Triggered;
        var noExecution = OrderTriggerEvaluator.Evaluate(order, 106m);
        Assert.Equal(OrderEvaluationAction.None, noExecution.Action);

        var execution = OrderTriggerEvaluator.Evaluate(order, 104m);
        Assert.Equal(OrderEvaluationAction.Execute, execution.Action);
    }

    [Fact]
    public void TakeProfit_Sell_ExecutesAboveTarget()
    {
        var order = NewOrder(OrderSide.Sell, OrderKind.TakeProfit);
        order.TakeProfitPrice = 120m;

        Assert.Equal(OrderEvaluationAction.Execute, OrderTriggerEvaluator.Evaluate(order, 121m).Action);
        Assert.Equal(OrderEvaluationAction.None, OrderTriggerEvaluator.Evaluate(order, 119m).Action);
    }

    [Fact]
    public void TrailingStop_TracksHighWaterMark_AndTriggersOnPullback()
    {
        var order = NewOrder(OrderSide.Sell, OrderKind.TrailingStop);
        order.TrailAmount = 5m;
        order.TrailingReferencePrice = 100m;

        var higher = OrderTriggerEvaluator.Evaluate(order, 104m);
        Assert.Equal(OrderEvaluationAction.None, higher.Action);
        Assert.Equal(104m, higher.TrailingReferencePrice);

        order.TrailingReferencePrice = higher.TrailingReferencePrice;
        var triggered = OrderTriggerEvaluator.Evaluate(order, 98m);
        Assert.Equal(OrderEvaluationAction.Execute, triggered.Action);
    }

    [Fact]
    public void TrailingStop_SupportsPercent()
    {
        var order = NewOrder(OrderSide.Sell, OrderKind.TrailingStop);
        order.TrailPercent = 10m;
        order.TrailingReferencePrice = 100m;

        Assert.Equal(OrderEvaluationAction.None, OrderTriggerEvaluator.Evaluate(order, 91m).Action);
        Assert.Equal(OrderEvaluationAction.Execute, OrderTriggerEvaluator.Evaluate(order, 89m).Action);
    }

    [Fact]
    public void BuyTrailingStop_TracksLowWaterMark()
    {
        var order = NewOrder(OrderSide.Buy, OrderKind.TrailingStop);
        order.TrailAmount = 5m;
        order.TrailingReferencePrice = 100m;

        var lower = OrderTriggerEvaluator.Evaluate(order, 96m);
        Assert.Equal(96m, lower.TrailingReferencePrice);
        Assert.Equal(OrderEvaluationAction.None, lower.Action);

        var triggered = OrderTriggerEvaluator.Evaluate(order, 106m);
        Assert.Equal(OrderEvaluationAction.Execute, triggered.Action);
    }

    private static Order NewOrder(OrderSide side, OrderKind kind) => new()
    {
        Id = 1,
        PortfolioId = 1,
        StockId = 1,
        Side = side,
        Kind = kind,
        Status = OrderStatus.Pending,
        Quantity = 1m,
    };
}
