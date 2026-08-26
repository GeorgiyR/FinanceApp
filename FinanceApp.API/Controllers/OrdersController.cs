using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using FinanceApp.Data.Data;
using FinanceApp.Core.Models;
using System.Security.Claims;

namespace FinanceApp.API.Controllers;

[ApiController]
[Route("api/Portfolios/{portfolioId}/orders")]
[Authorize]
public class OrdersController : ControllerBase
{
    private readonly AppDbContext _context;
    public OrdersController(AppDbContext context) { _context = context; }

    private int GetUserId() => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private async Task<bool> PortfolioBelongsToUser(int portfolioId) =>
        await _context.Portfolios.AnyAsync(p => p.Id == portfolioId && p.UserId == GetUserId());

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Order>>> GetAll(int portfolioId)
    {
        if (!await PortfolioBelongsToUser(portfolioId)) return NotFound();
        return await _context.Orders
            .Where(o => o.PortfolioId == portfolioId)
            .Include(o => o.Stock)
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync();
    }

    [HttpPost]
    public async Task<ActionResult<Order>> Create(int portfolioId, CreateOrderDto dto)
    {
        if (!await PortfolioBelongsToUser(portfolioId)) return NotFound();
        if (!await _context.Stocks.AnyAsync(s => s.Id == dto.StockId))
            return BadRequest("Stock not found");

        var order = new Order
        {
            PortfolioId = portfolioId,
            StockId = dto.StockId,
            Type = dto.Type,
            Status = OrderStatus.Pending,
            Quantity = dto.Quantity,
            Price = dto.Price,
            StopLoss = dto.StopLoss,
            StopMarket = dto.StopMarket,
            CreatedAt = DateTime.UtcNow
        };
        _context.Orders.Add(order);
        await _context.SaveChangesAsync();
        await _context.Entry(order).Reference(o => o.Stock).LoadAsync();
        return Ok(order);
    }

    [HttpPut("{orderId}")]
    public async Task<ActionResult<Order>> Update(int portfolioId, int orderId, UpdateOrderDto dto)
    {
        if (!await PortfolioBelongsToUser(portfolioId)) return NotFound();
        var order = await _context.Orders
            .Include(o => o.Stock)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.PortfolioId == portfolioId);
        if (order == null) return NotFound();

        var previousStatus = order.Status;

        order.Type = dto.Type;
        order.Quantity = dto.Quantity;
        order.Price = dto.Price;
        order.StopLoss = dto.StopLoss;
        order.StopMarket = dto.StopMarket;
        order.Status = dto.Status;

        // When status changes to Executed — update portfolio item quantity and create transaction
        if (previousStatus != OrderStatus.Executed && dto.Status == OrderStatus.Executed)
        {
            order.ExecutedAt = DateTime.UtcNow;
            var item = await _context.PortfolioItems
                .FirstOrDefaultAsync(i => i.PortfolioId == portfolioId && i.StockId == order.StockId);

            if (item != null)
            {
                if (order.Type == OrderType.Buy)
                    item.Quantity += order.Quantity;
                else
                    item.Quantity -= order.Quantity;

                if (item.Quantity <= 0)
                    _context.PortfolioItems.Remove(item);
            }
            else if (order.Type == OrderType.Buy)
            {
                // Create new position if it doesn't exist yet
                _context.PortfolioItems.Add(new PortfolioItem
                {
                    PortfolioId = portfolioId,
                    StockId = order.StockId,
                    Quantity = order.Quantity,
                    BuyPrice = order.Price,
                    BoughtAt = DateTime.UtcNow
                });

                if (order.Stock is not null && order.Stock.PurchaseCandidatePriority != StockPurchaseCandidatePriority.None)
                {
                    order.Stock.PurchaseCandidatePriority = StockPurchaseCandidatePriority.None;
                    order.Stock.UpdatedAt = DateTime.UtcNow;
                }
            }

            // Create linked transaction (idempotent: OrderId has unique constraint)
            var alreadyLinked = await _context.Transactions
                .AnyAsync(t => t.OrderId == orderId);
            if (!alreadyLinked)
            {
                var txType = order.Type == OrderType.Buy ? TransactionType.Buy : TransactionType.Sell;
                var totalAmount = order.Price * order.Quantity;
                var signedAmount = TransactionDirection.DeriveSignedAmount(txType, totalAmount);
                var stockName = order.Stock?.Name ?? string.Empty;
                var stockTicker = order.Stock?.Ticker ?? string.Empty;
                var typeLabel = order.Type == OrderType.Buy ? "Покупка" : "Продажа";
                var description = $"{typeLabel} — {stockTicker} · {stockName}";
                var (instrumentCode, instrumentCodeType) = TransactionInstrumentSnapshot.ResolveFromStock(order.Stock);

                _context.Transactions.Add(new Transaction
                {
                    PortfolioId = portfolioId,
                    Type = txType,
                    Amount = totalAmount,
                    SignedAmount = signedAmount,
                    StockId = order.StockId,
                    OrderId = orderId,
                    Description = description,
                    CreatedAt = order.ExecutedAt!.Value,
                    InstrumentCode = instrumentCode,
                    InstrumentCodeType = instrumentCodeType,
                    Quantity = order.Quantity,
                    UnitPrice = order.Price,
                });
            }
        }

        await _context.SaveChangesAsync();
        return Ok(order);
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
}

public class CreateOrderDto
{
    public int StockId { get; set; }
    public OrderType Type { get; set; }
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal? StopLoss { get; set; }
    public decimal? StopMarket { get; set; }
}

public class UpdateOrderDto
{
    public OrderType Type { get; set; }
    public OrderStatus Status { get; set; }
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal? StopLoss { get; set; }
    public decimal? StopMarket { get; set; }
}
