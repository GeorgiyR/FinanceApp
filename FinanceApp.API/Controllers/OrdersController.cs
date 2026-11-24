using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using FinanceApp.Data.Data;
using FinanceApp.Core.Models;
using FinanceApp.API.Services;
using System.Security.Claims;
using System.Text.Json.Serialization;

namespace FinanceApp.API.Controllers;

[ApiController]
[Route("api/Portfolios/{portfolioId}/orders")]
[Authorize]
public class OrdersController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IOrderExecutionService _executionService;
    private readonly TimeProvider _timeProvider;

    public OrdersController(
        AppDbContext context,
        IOrderExecutionService executionService,
        TimeProvider timeProvider)
    {
        _context = context;
        _executionService = executionService;
        _timeProvider = timeProvider;
    }

    private int GetUserId() => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private async Task<bool> PortfolioBelongsToUser(int portfolioId) =>
        await _context.Portfolios.AnyAsync(p => p.Id == portfolioId && p.UserId == GetUserId());

    [HttpGet]
    public async Task<ActionResult<IEnumerable<OrderDto>>> GetAll(int portfolioId)
    {
        if (!await PortfolioBelongsToUser(portfolioId)) return NotFound();

        var orders = await _context.Orders
            .AsNoTracking()
            .Where(o => o.PortfolioId == portfolioId)
            .Include(o => o.Stock)
            .OrderByDescending(o => o.CreatedAt)
            .ThenByDescending(o => o.Id)
            .ToListAsync();

        return Ok(orders.Select(OrderDto.FromEntity).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<OrderDto>> Create(int portfolioId, CreateOrderDto dto)
    {
        if (!await PortfolioBelongsToUser(portfolioId)) return NotFound();

        var stock = await _context.Stocks.FirstOrDefaultAsync(s => s.Id == dto.StockId);
        if (stock is null) return BadRequest("Stock not found");

        var side = dto.Side ?? dto.Type;
        if (side is null) return BadRequest("side (or legacy type) is required.");

        var kind = dto.Kind ?? OrderKind.Limit;
        var validationError = Validate(side.Value, kind, dto.Quantity, dto.Price, dto.StopPrice ?? dto.StopMarket,
            dto.TakeProfitPrice, dto.TrailAmount, dto.TrailPercent, dto.Price);
        if (validationError is not null) return BadRequest(validationError);

        if (kind == OrderKind.Market && stock.CurrentPrice <= 0m)
        {
            return BadRequest("Для рыночного ордера нет актуальной цены инструмента.");
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var order = new Order
        {
            PortfolioId = portfolioId,
            StockId = dto.StockId,
            Side = side.Value,
            Kind = kind,
            TimeInForce = dto.TimeInForce ?? TimeInForce.Gtc,
            Status = OrderStatus.Pending,
            Quantity = dto.Quantity,
            Price = dto.Price,
            StopPrice = dto.StopPrice ?? dto.StopMarket,
            TakeProfitPrice = dto.TakeProfitPrice,
            TrailAmount = dto.TrailAmount,
            TrailPercent = dto.TrailPercent,
            TrailingReferencePrice = kind == OrderKind.TrailingStop && stock.CurrentPrice > 0m
                ? stock.CurrentPrice
                : null,
            StopLoss = dto.StopLoss,
            StopMarket = dto.StopMarket,
            ExpiresAt = dto.ExpiresAt,
            CreatedAt = now,
        };

        _context.Orders.Add(order);
        await _context.SaveChangesAsync();

        order.Stock = stock;

        if (kind == OrderKind.Market)
        {
            var result = await _executionService.ExecuteAsync(
                order,
                stock.CurrentPrice,
                OrderExecutionSource.Manual,
                "Market order executed on creation.");
            if (!result.IsSuccess)
            {
                return BadRequest(result.Error);
            }
        }

        return Ok(OrderDto.FromEntity(order));
    }

    [HttpPut("{orderId}")]
    public async Task<ActionResult<OrderDto>> Update(int portfolioId, int orderId, UpdateOrderDto dto)
    {
        if (!await PortfolioBelongsToUser(portfolioId)) return NotFound();

        var order = await _context.Orders
            .Include(o => o.Stock)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.PortfolioId == portfolioId);
        if (order is null) return NotFound();

        // A bare update (no status transition) is allowed for historical orders too:
        // the orders view lets users correct quantity and fill price after execution.
        var editOnly = dto.Status is null && order.Status is not (OrderStatus.Pending or OrderStatus.Triggered);
        if (!editOnly && order.Status is not (OrderStatus.Pending or OrderStatus.Triggered))
        {
            return Conflict($"Order is already {order.Status}.");
        }

        if (dto.Status == OrderStatus.Executed)
        {
            var fillPrice = order.Kind == OrderKind.Market
                ? order.Stock?.CurrentPrice ?? 0m
                : dto.Price > 0m ? dto.Price : order.Price;
            var result = await _executionService.ExecuteAsync(
                order,
                fillPrice,
                OrderExecutionSource.Manual,
                "Manual execution.");
            if (!result.IsSuccess)
            {
                return BadRequest(result.Error);
            }

            return Ok(OrderDto.FromEntity(order));
        }

        var side = dto.Side ?? dto.Type ?? order.Side;
        var kind = dto.Kind ?? order.Kind;
        var price = dto.Price > 0m ? dto.Price : order.Price;
        var stopPrice = dto.StopPrice ?? dto.StopMarket ?? order.StopPrice;
        var takeProfitPrice = dto.TakeProfitPrice ?? order.TakeProfitPrice;
        var trailAmount = dto.TrailAmount ?? order.TrailAmount;
        var trailPercent = dto.TrailPercent ?? order.TrailPercent;
        var quantity = dto.Quantity > 0m ? dto.Quantity : order.Quantity;

        var validationError = Validate(side, kind, quantity, price, stopPrice,
            takeProfitPrice, trailAmount, trailPercent, price);
        if (validationError is not null) return BadRequest(validationError);

        var isExecutedEdit = editOnly && order.Status == OrderStatus.Executed;
        var oldQuantity = order.Quantity;
        var oldFillPrice = order.AverageFillPrice ?? order.Price;
        if (isExecutedEdit)
        {
            // Side and kind are fixed for an executed trade; only the numbers may be corrected.
            side = order.Side;
            kind = order.Kind;
        }

        order.Side = side;
        order.Kind = kind;
        order.TimeInForce = dto.TimeInForce ?? order.TimeInForce;
        order.Quantity = quantity;
        order.Price = price;
        order.StopPrice = stopPrice;
        order.TakeProfitPrice = takeProfitPrice;
        order.TrailAmount = trailAmount;
        order.TrailPercent = trailPercent;
        order.StopLoss = dto.StopLoss ?? order.StopLoss;
        order.StopMarket = dto.StopMarket ?? order.StopMarket;
        order.ExpiresAt = dto.ExpiresAt ?? order.ExpiresAt;
        order.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;

        if (dto.Status == OrderStatus.Cancelled)
        {
            order.Status = OrderStatus.Cancelled;
            order.CancelledAt = order.UpdatedAt;
        }
        else if (dto.Status is not null)
        {
            order.Status = dto.Status.Value;
        }

        if (isExecutedEdit)
        {
            order.FilledQuantity = order.Quantity;
            order.AverageFillPrice = price;
            await SyncExecutedOrderEditAsync(order, oldQuantity, oldFillPrice);
        }

        await _context.SaveChangesAsync();
        return Ok(OrderDto.FromEntity(order));
    }

    /// <summary>
    /// Applies a correction of an executed order's quantity/price to the linked transaction
    /// and the portfolio position so that all three views stay consistent.
    /// </summary>
    private async Task SyncExecutedOrderEditAsync(Order order, decimal oldQuantity, decimal oldFillPrice)
    {
        var transaction = await _context.Transactions
            .FirstOrDefaultAsync(t => t.OrderId == order.Id);
        if (transaction is null) return;

        var newQuantity = order.Quantity;
        var newFillPrice = order.AverageFillPrice ?? order.Price;

        transaction.Quantity = newQuantity;
        transaction.UnitPrice = newFillPrice;
        transaction.Amount = newQuantity * newFillPrice;
        transaction.SignedAmount = TransactionDirection.DeriveSignedAmount(transaction.Type, transaction.Amount);

        var item = await _context.PortfolioItems
            .FirstOrDefaultAsync(i => i.PortfolioId == order.PortfolioId && i.StockId == order.StockId);
        if (item is null) return;

        var quantityDelta = newQuantity - oldQuantity;
        var costDelta = newQuantity * newFillPrice - oldQuantity * oldFillPrice;
        var newPositionQuantity = transaction.Type == TransactionType.Buy
            ? item.Quantity + quantityDelta
            : item.Quantity - quantityDelta;

        if (newPositionQuantity <= 0m)
        {
            _context.PortfolioItems.Remove(item);
            return;
        }

        if (transaction.Type == TransactionType.Buy)
        {
            var newCostBasis = item.BuyPrice * item.Quantity + costDelta;
            item.BuyPrice = newCostBasis > 0m ? newCostBasis / newPositionQuantity : 0m;
        }

        item.Quantity = newPositionQuantity;
    }

    [HttpPost("{orderId}/cancel")]
    public async Task<ActionResult<OrderDto>> Cancel(int portfolioId, int orderId)
    {
        if (!await PortfolioBelongsToUser(portfolioId)) return NotFound();

        var order = await _context.Orders
            .Include(o => o.Stock)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.PortfolioId == portfolioId);
        if (order is null) return NotFound();

        if (order.Status is not (OrderStatus.Pending or OrderStatus.Triggered))
        {
            return Conflict($"Order is already {order.Status}.");
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        order.Status = OrderStatus.Cancelled;
        order.CancelledAt = now;
        order.UpdatedAt = now;
        await _context.SaveChangesAsync();

        return Ok(OrderDto.FromEntity(order));
    }

    [HttpDelete("{orderId}")]
    public async Task<IActionResult> Delete(int portfolioId, int orderId)
    {
        if (!await PortfolioBelongsToUser(portfolioId)) return NotFound();
        var order = await _context.Orders
            .FirstOrDefaultAsync(o => o.Id == orderId && o.PortfolioId == portfolioId);
        if (order == null) return NotFound();
        _context.Orders.Remove(order);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    private static string? Validate(
        OrderSide side,
        OrderKind kind,
        decimal quantity,
        decimal price,
        decimal? stopPrice,
        decimal? takeProfitPrice,
        decimal? trailAmount,
        decimal? trailPercent,
        decimal limitPrice)
    {
        _ = side;
        if (quantity <= 0m) return "Количество должно быть больше нуля.";

        return kind switch
        {
            OrderKind.Market => null,
            OrderKind.Limit => limitPrice > 0m ? null : "Для лимитного ордера укажите цену больше нуля.",
            OrderKind.StopMarket => stopPrice is > 0m ? null : "Для стоп-маркет ордера укажите стоп-цену больше нуля.",
            OrderKind.StopLimit => stopPrice is > 0m && limitPrice > 0m
                ? null
                : "Для стоп-лимит ордера укажите стоп-цену и лимит больше нуля.",
            OrderKind.TakeProfit => takeProfitPrice is > 0m || price > 0m
                ? null
                : "Для тейк-профит ордера укажите целевую цену больше нуля.",
            OrderKind.TrailingStop => (trailAmount is > 0m) ^ (trailPercent is > 0m)
                ? null
                : "Для трейлинг-стоп укажите либо абсолютный размер, либо процент (ровно один).",
            _ => "Неизвестный тип ордера.",
        };
    }
}

public sealed class OrderDto
{
    public int Id { get; init; }
    public int PortfolioId { get; init; }
    public int StockId { get; init; }
    public string? StockTicker { get; init; }
    public string? StockName { get; init; }
    /// <summary>Legacy alias for <see cref="Side"/> (React API compatibility).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OrderSide Type { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OrderSide Side { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OrderKind Kind { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TimeInForce TimeInForce { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OrderStatus Status { get; init; }
    public decimal Quantity { get; init; }
    public decimal FilledQuantity { get; init; }
    public decimal Price { get; init; }
    public decimal? AverageFillPrice { get; init; }
    public decimal? StopPrice { get; init; }
    public decimal? TakeProfitPrice { get; init; }
    public decimal? TrailAmount { get; init; }
    public decimal? TrailPercent { get; init; }
    public decimal? TrailingReferencePrice { get; init; }
    public decimal? StopLoss { get; init; }
    public decimal? StopMarket { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OrderExecutionSource? ExecutionSource { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public DateTime? TriggeredAt { get; init; }
    public DateTime? ExecutedAt { get; init; }
    public DateTime? CancelledAt { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public string? RejectReason { get; init; }

    public static OrderDto FromEntity(Order order) => new()
    {
        Id = order.Id,
        PortfolioId = order.PortfolioId,
        StockId = order.StockId,
        StockTicker = order.Stock?.Ticker,
        StockName = order.Stock?.Name,
        Type = order.Side,
        Side = order.Side,
        Kind = order.Kind,
        TimeInForce = order.TimeInForce,
        Status = order.Status,
        Quantity = order.Quantity,
        FilledQuantity = order.FilledQuantity,
        Price = order.Price,
        AverageFillPrice = order.AverageFillPrice,
        StopPrice = order.StopPrice,
        TakeProfitPrice = order.TakeProfitPrice,
        TrailAmount = order.TrailAmount,
        TrailPercent = order.TrailPercent,
        TrailingReferencePrice = order.TrailingReferencePrice,
        StopLoss = order.StopLoss,
        StopMarket = order.StopMarket,
        ExecutionSource = order.ExecutionSource,
        CreatedAt = order.CreatedAt,
        UpdatedAt = order.UpdatedAt,
        TriggeredAt = order.TriggeredAt,
        ExecutedAt = order.ExecutedAt,
        CancelledAt = order.CancelledAt,
        ExpiresAt = order.ExpiresAt,
        RejectReason = order.RejectReason,
    };
}

public class CreateOrderDto
{
    public int StockId { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OrderSide? Side { get; set; }
    /// <summary>Legacy React API field carrying the order side.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OrderSide? Type { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OrderKind? Kind { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TimeInForce? TimeInForce { get; set; }
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal? StopPrice { get; set; }
    public decimal? TakeProfitPrice { get; set; }
    public decimal? TrailAmount { get; set; }
    public decimal? TrailPercent { get; set; }
    public decimal? StopLoss { get; set; }
    public decimal? StopMarket { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

public class UpdateOrderDto
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OrderSide? Side { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OrderSide? Type { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OrderKind? Kind { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TimeInForce? TimeInForce { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OrderStatus? Status { get; set; }
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal? StopPrice { get; set; }
    public decimal? TakeProfitPrice { get; set; }
    public decimal? TrailAmount { get; set; }
    public decimal? TrailPercent { get; set; }
    public decimal? StopLoss { get; set; }
    public decimal? StopMarket { get; set; }
    public DateTime? ExpiresAt { get; set; }
}
