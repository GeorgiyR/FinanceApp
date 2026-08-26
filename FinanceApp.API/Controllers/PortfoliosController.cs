using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using FinanceApp.Data.Data;
using FinanceApp.Core.Models;
using System.Security.Claims;

namespace FinanceApp.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PortfoliosController : ControllerBase
{
    private readonly AppDbContext _context;
    public PortfoliosController(AppDbContext context) { _context = context; }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Portfolio>>> GetAll()
    {
        var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        return await _context.Portfolios
            .Where(p => p.UserId == userId)
            .Include(p => p.Items)
            .ThenInclude(i => i.Stock)
            .ToListAsync();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Portfolio>> GetById(int id)
    {
        var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var portfolio = await _context.Portfolios
            .Include(p => p.Items)
            .ThenInclude(i => i.Stock)
            .FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);
        if (portfolio == null) return NotFound();
        return portfolio;
    }

    [HttpPost]
    public async Task<ActionResult<Portfolio>> Create(CreatePortfolioDto dto)
    {
        var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var portfolio = new Portfolio
        {
            Name = dto.Name,
            UserId = userId,
            CreatedAt = DateTime.UtcNow
        };
        _context.Portfolios.Add(portfolio);
        await _context.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = portfolio.Id }, portfolio);
    }

    [HttpPost("{id}/items")]
    public async Task<ActionResult<PortfolioItem>> AddItem(int id, AddItemDto dto)
    {
        var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var mutationResult = await ExecuteMutationWithExecutionStrategyAsync(async ct =>
        {
            if (!await _context.Portfolios.AnyAsync(p => p.Id == id && p.UserId == userId, ct))
            {
                return new AddItemMutationResult(ActionResult: NotFound("Portfolio not found"));
            }

            var stock = await _context.Stocks.FirstOrDefaultAsync(s => s.Id == dto.StockId, ct);
            if (stock is null)
            {
                return new AddItemMutationResult(ActionResult: BadRequest("Stock not found"));
            }

            var item = new PortfolioItem
            {
                PortfolioId = id,
                StockId = dto.StockId,
                Quantity = dto.Quantity,
                BuyPrice = dto.BuyPrice,
                BoughtAt = DateTime.UtcNow
            };
            _context.PortfolioItems.Add(item);

            if (stock.PurchaseCandidatePriority != StockPurchaseCandidatePriority.None)
            {
                stock.PurchaseCandidatePriority = StockPurchaseCandidatePriority.None;
                stock.UpdatedAt = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync(ct);
            return new AddItemMutationResult(Item: item);
        }, HttpContext.RequestAborted);

        if (mutationResult.ActionResult is not null)
        {
            return mutationResult.ActionResult;
        }

        return Ok(mutationResult.Item);
    }

    [HttpPut("{id}/items/{itemId}")]
    public async Task<IActionResult> UpdateItem(int id, int itemId, AddItemDto dto)
    {
        var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        if (!await _context.Portfolios.AnyAsync(p => p.Id == id && p.UserId == userId))
            return NotFound("Portfolio not found");
        var item = await _context.PortfolioItems
            .FirstOrDefaultAsync(i => i.Id == itemId && i.PortfolioId == id);
        if (item == null) return NotFound("Item not found");
        item.StockId = dto.StockId;
        item.Quantity = dto.Quantity;
        item.BuyPrice = dto.BuyPrice;
        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id}/items/{itemId}")]
    public async Task<IActionResult> DeleteItem(int id, int itemId)
    {
        var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        if (!await _context.Portfolios.AnyAsync(p => p.Id == id && p.UserId == userId))
            return NotFound("Portfolio not found");
        var item = await _context.PortfolioItems
            .FirstOrDefaultAsync(i => i.Id == itemId && i.PortfolioId == id);
        if (item == null) return NotFound("Item not found");
        _context.PortfolioItems.Remove(item);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var portfolio = await _context.Portfolios
            .FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);
        if (portfolio == null) return NotFound();
        _context.Portfolios.Remove(portfolio);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    private async Task<TMutationResult> ExecuteMutationWithExecutionStrategyAsync<TMutationResult>(
        Func<CancellationToken, Task<TMutationResult>> mutation,
        CancellationToken cancellationToken)
    {
        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = _context.Database.IsRelational()
                ? await _context.Database.BeginTransactionAsync(cancellationToken)
                : null;

            try
            {
                var result = await mutation(cancellationToken);
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                return result;
            }
            catch
            {
                if (transaction is not null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }

                throw;
            }
        });
    }

    private sealed record AddItemMutationResult(
        ActionResult<PortfolioItem>? ActionResult = null,
        PortfolioItem? Item = null);
}

public class CreatePortfolioDto
{
    public string Name { get; set; } = string.Empty;
}

public class AddItemDto
{
    public int StockId { get; set; }
    public decimal Quantity { get; set; }
    public decimal BuyPrice { get; set; }
}
