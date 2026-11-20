using System.ComponentModel.DataAnnotations.Schema;

namespace FinanceApp.Core.Models;

public enum OrderSide { Buy, Sell }

public enum OrderKind
{
    Market = 0,
    Limit = 1,
    StopMarket = 2,
    StopLimit = 3,
    TakeProfit = 4,
    TrailingStop = 5,
}

public enum TimeInForce
{
    Gtc = 0,
    Day = 1,
    Gtd = 2,
    Ioc = 3,
    Fok = 4,
}

/// <summary>
/// Numeric values are persisted. Keep existing values stable and only append new ones.
/// </summary>
public enum OrderStatus
{
    Pending = 0,
    Executed = 1,
    Cancelled = 2,
    Triggered = 3,
    PartiallyFilled = 4,
    Rejected = 5,
    Expired = 6,
}

public enum OrderExecutionSource
{
    Manual = 0,
    Automatic = 1,
}

public class Order
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public Portfolio Portfolio { get; set; } = null!;
    public int StockId { get; set; }
    public Stock Stock { get; set; } = null!;
    public OrderSide Side { get; set; }
    public OrderKind Kind { get; set; } = OrderKind.Limit;
    public TimeInForce TimeInForce { get; set; } = TimeInForce.Gtc;
    public OrderStatus Status { get; set; } = OrderStatus.Pending;
    [Column(TypeName = "decimal(18,2)")]
    public decimal Quantity { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal FilledQuantity { get; set; }
    /// <summary>Limit price for Limit/StopLimit orders; legacy orders use this as the execution price.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Price { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal? AverageFillPrice { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal? StopPrice { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal? TakeProfitPrice { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal? TrailAmount { get; set; }
    [Column(TypeName = "decimal(18,4)")]
    public decimal? TrailPercent { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal? TrailingReferencePrice { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal? StopLoss { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal? StopMarket { get; set; }
    public OrderExecutionSource? ExecutionSource { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public DateTime? TriggeredAt { get; set; }
    public DateTime? ExecutedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public DateTime? RejectedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string? RejectReason { get; set; }
    public int? ParentOrderId { get; set; }
    public string? OcoGroupId { get; set; }
}
