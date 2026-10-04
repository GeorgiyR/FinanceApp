using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using FinanceApp.Data.Data;
using FinanceApp.Core.Models;
using System.Security.Claims;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using FinanceApp.API.Extensions;

namespace FinanceApp.API.Controllers;

[ApiController]
[Route("api/Portfolios/{portfolioId}/finance")]
[Authorize]
public class FinanceController : ControllerBase
{
    private readonly AppDbContext _context;
    public FinanceController(AppDbContext context) { _context = context; }

    private int GetUserId() => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private async Task<bool> PortfolioBelongsToUser(int portfolioId) =>
        await _context.Portfolios.AnyAsync(p => p.Id == portfolioId && p.UserId == GetUserId());

    [HttpGet("balance")]
    public async Task<ActionResult<PortfolioBalance>> GetBalance(int portfolioId)
    {
        if (!await PortfolioBelongsToUser(portfolioId)) return NotFound();

        var portfolio = await _context.Portfolios
            .Include(p => p.Items)
            .ThenInclude(i => i.Stock)
            .FirstOrDefaultAsync(p => p.Id == portfolioId);
        if (portfolio == null) return NotFound();

        var transactionCashBalance = await GetTransactionCashBalance(portfolioId);
        var cashBalance = transactionCashBalance + portfolio.CashBalanceAdjustment;

        return Ok(BuildBalance(portfolio, cashBalance));
    }

    [HttpPut("balance")]
    public async Task<ActionResult<PortfolioBalance>> UpdateBalance(int portfolioId, UpdatePortfolioBalanceDto dto)
    {
        if (!await PortfolioBelongsToUser(portfolioId)) return NotFound();

        var portfolio = await _context.Portfolios
            .Include(p => p.Items)
            .ThenInclude(i => i.Stock)
            .FirstOrDefaultAsync(p => p.Id == portfolioId);
        if (portfolio == null) return NotFound();

        var transactionCashBalance = await GetTransactionCashBalance(portfolioId);
        var normalizedCashBalance = NormalizeMoney(dto.CashBalance);

        portfolio.CashBalanceAdjustment = normalizedCashBalance - transactionCashBalance;

        // Preserve stored broker credit only when explicitly provided by legacy clients.
        if (dto.BrokerCredit.HasValue)
        {
            portfolio.BrokerCredit = NormalizeMoney(dto.BrokerCredit.Value);
        }

        await _context.SaveChangesAsync();

        return Ok(BuildBalance(portfolio, normalizedCashBalance));
    }

    [HttpGet("transactions")]
    public async Task<ActionResult<IEnumerable<Transaction>>> GetTransactions(int portfolioId)
    {
        if (!await PortfolioBelongsToUser(portfolioId)) return NotFound();
        return await _context.Transactions
            .Where(t => t.PortfolioId == portfolioId)
            .Include(t => t.Stock)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();
    }

    [HttpPost("transactions")]
    public async Task<ActionResult<Transaction>> CreateTransaction(int portfolioId, CreateTransactionDto dto)
    {
        if (!await PortfolioBelongsToUser(portfolioId)) return NotFound();

        var normalizedSnapshot = await ValidateAndNormalizeTransactionAsync(
            dto.Amount,
            dto.StockId,
            dto.CreatedAt,
            dto.InstrumentCode,
            dto.InstrumentCodeType,
            dto.Quantity,
            dto.UnitPrice);
        if (normalizedSnapshot.Error != null) return normalizedSnapshot.Error;

        // Derive signed amount from type (enforced server-side, positive user amount)
        var signedAmount = TransactionDirection.DeriveSignedAmount(dto.Type, dto.Amount);

        var transaction = new Transaction
        {
            PortfolioId = portfolioId,
            Type = dto.Type,
            Amount = dto.Amount > 0 ? dto.Amount : decimal.Abs(dto.Amount),
            SignedAmount = signedAmount,
            StockId = dto.StockId,
            Description = dto.Description,
            CreatedAt = NormalizeClientDateTime(dto.CreatedAt),
            InstrumentCode = normalizedSnapshot.InstrumentCode,
            InstrumentCodeType = normalizedSnapshot.InstrumentCodeType,
            Quantity = normalizedSnapshot.Quantity,
            UnitPrice = normalizedSnapshot.UnitPrice,
        };
        _context.Transactions.Add(transaction);
        await _context.SaveChangesAsync();

        if (transaction.StockId.HasValue)
            await _context.Entry(transaction).Reference(t => t.Stock).LoadAsync();

        // Update portfolio items (position quantities) to reflect the transaction
        _context.UpdatePortfolioItemsForTransaction(portfolioId, transaction);

        return Ok(transaction);
    }

    [HttpPut("transactions/{id}")]
    public async Task<ActionResult<Transaction>> UpdateTransaction(int portfolioId, int id, UpdateTransactionDto dto)
    {
        if (!await PortfolioBelongsToUser(portfolioId)) return NotFound();

        var transaction = await _context.Transactions
            .FirstOrDefaultAsync(t => t.Id == id && t.PortfolioId == portfolioId);
        if (transaction == null) return NotFound();

        // Prevent editing order-linked transactions (they are auto-generated)
        if (transaction.OrderId.HasValue)
            return BadRequest("Order-linked transactions cannot be edited directly.");

        var normalizedSnapshot = await ValidateAndNormalizeTransactionAsync(
            dto.Amount,
            dto.StockId,
            dto.CreatedAt,
            dto.InstrumentCode,
            dto.InstrumentCodeType,
            dto.Quantity,
            dto.UnitPrice);
        if (normalizedSnapshot.Error != null) return normalizedSnapshot.Error;

        var signedAmount = TransactionDirection.DeriveSignedAmount(dto.Type, dto.Amount);

        transaction.Type = dto.Type;
        transaction.Amount = dto.Amount > 0 ? dto.Amount : decimal.Abs(dto.Amount);
        transaction.SignedAmount = signedAmount;
        transaction.StockId = dto.StockId;
        transaction.Description = dto.Description;
        transaction.CreatedAt = NormalizeClientDateTime(dto.CreatedAt);
        transaction.InstrumentCode = normalizedSnapshot.InstrumentCode;
        transaction.InstrumentCodeType = normalizedSnapshot.InstrumentCodeType;
        transaction.Quantity = normalizedSnapshot.Quantity;
        transaction.UnitPrice = normalizedSnapshot.UnitPrice;

        await _context.SaveChangesAsync();

        if (transaction.StockId.HasValue)
            await _context.Entry(transaction).Reference(t => t.Stock).LoadAsync();

        return Ok(transaction);
    }

    [HttpDelete("transactions/{id}")]
    public async Task<IActionResult> DeleteTransaction(int portfolioId, int id)
    {
        if (!await PortfolioBelongsToUser(portfolioId)) return NotFound();
        var transaction = await _context.Transactions
            .FirstOrDefaultAsync(t => t.Id == id && t.PortfolioId == portfolioId);
        if (transaction == null) return NotFound();
        _context.Transactions.Remove(transaction);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    private async Task<decimal> GetTransactionCashBalance(int portfolioId)
    {
        var transactions = await _context.Transactions
            .Where(t => t.PortfolioId == portfolioId)
            .ToListAsync();

        return transactions.Sum(t => t.GetEffectiveSignedAmount());
    }

    private PortfolioBalance BuildBalance(Portfolio portfolio, decimal cashBalance)
    {
        var stocksValue = portfolio.Items.Sum(i => i.Stock.CurrentPrice * i.Quantity);

        return new PortfolioBalance
        {
            CashBalance = cashBalance,
            // Retained for backward-compatible deserialization; not included in totals.
            BrokerCredit = portfolio.BrokerCredit,
            // TotalBalance no longer includes broker credit.
            TotalBalance = cashBalance,
            StocksValue = stocksValue,
            TotalPortfolioValue = stocksValue + cashBalance,
        };
    }

    private async Task<NormalizedTransactionSnapshot> ValidateAndNormalizeTransactionAsync(
        decimal amount,
        int? stockId,
        DateTime createdAt,
        string? instrumentCode,
        InstrumentCodeType? instrumentCodeType,
        decimal? quantity,
        decimal? unitPrice)
    {
        if (amount <= 0)
            return NormalizedTransactionSnapshot.WithError(BadRequest("Amount must be greater than zero."));

        if (createdAt == default)
            return NormalizedTransactionSnapshot.WithError(BadRequest("CreatedAt is required."));

        if (quantity < 0m)
            return NormalizedTransactionSnapshot.WithError(BadRequest("Quantity cannot be negative."));

        if (unitPrice < 0m)
            return NormalizedTransactionSnapshot.WithError(BadRequest("UnitPrice cannot be negative."));

        Stock? stock = null;
        if (stockId.HasValue)
        {
            stock = await _context.Stocks
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == stockId.Value);

            if (stock == null)
                return NormalizedTransactionSnapshot.WithError(BadRequest("Stock not found."));
        }

        var normalizedInstrumentCode = TransactionInstrumentSnapshot.NormalizeInstrumentCode(instrumentCode, instrumentCodeType);
        var normalizedInstrumentCodeType = instrumentCodeType;

        if (normalizedInstrumentCode == null && normalizedInstrumentCodeType == null && stock != null)
        {
            (normalizedInstrumentCode, normalizedInstrumentCodeType) = TransactionInstrumentSnapshot.ResolveFromStock(stock);
        }

        if ((normalizedInstrumentCode is null) != (normalizedInstrumentCodeType is null))
            return NormalizedTransactionSnapshot.WithError(BadRequest("InstrumentCode and InstrumentCodeType must either both be provided or both be null."));

        if (normalizedInstrumentCode != null)
        {
            if (normalizedInstrumentCode.Length > 32)
                return NormalizedTransactionSnapshot.WithError(BadRequest("InstrumentCode must be at most 32 characters."));

            if (normalizedInstrumentCodeType == InstrumentCodeType.ISIN)
            {
                if (!StockIdentifiers.IsValidIsin(normalizedInstrumentCode))
                {
                    return NormalizedTransactionSnapshot.WithError(BadRequest(
                        "ISIN must contain exactly 12 characters: 2 uppercase letters followed by 10 uppercase alphanumeric characters."));
                }
            }
        }

        return new NormalizedTransactionSnapshot(null, normalizedInstrumentCode, normalizedInstrumentCodeType, quantity, unitPrice);
    }

    private static DateTime NormalizeClientDateTime(DateTime value)
    {
        if (value.Kind == DateTimeKind.Utc)
            return value;

        if (value.Kind == DateTimeKind.Local)
            return value.ToUniversalTime();

        return DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime();
    }

    private static decimal NormalizeMoney(decimal value) =>
        Math.Round(value, Portfolio.MonetaryScale, MidpointRounding.AwayFromZero);

    [HttpGet("dividends")]
    public async Task<ActionResult<IEnumerable<Dividend>>> GetDividends(int portfolioId)
    {
        if (!await PortfolioBelongsToUser(portfolioId)) return NotFound();
        return await _context.Dividends
            .Where(d => d.PortfolioId == portfolioId)
            .Include(d => d.Stock)
            .OrderByDescending(d => d.PaidAt)
            .ToListAsync();
    }

    [HttpPost("dividends")]
    public async Task<ActionResult<Dividend>> CreateDividend(int portfolioId, CreateDividendDto dto)
    {
        if (!await PortfolioBelongsToUser(portfolioId)) return NotFound();
        if (!await _context.Stocks.AnyAsync(s => s.Id == dto.StockId))
            return BadRequest("Stock not found");

        var dividend = new Dividend
        {
            PortfolioId = portfolioId,
            StockId = dto.StockId,
            Amount = dto.Amount,
            PaidAt = dto.PaidAt,
            CreatedAt = DateTime.UtcNow,
        };
        _context.Dividends.Add(dividend);
        await _context.SaveChangesAsync();
        await _context.Entry(dividend).Reference(d => d.Stock).LoadAsync();
        return Ok(dividend);
    }

    [HttpDelete("dividends/{id}")]
    public async Task<IActionResult> DeleteDividend(int portfolioId, int id)
    {
        if (!await PortfolioBelongsToUser(portfolioId)) return NotFound();
        var dividend = await _context.Dividends
            .FirstOrDefaultAsync(d => d.Id == id && d.PortfolioId == portfolioId);
        if (dividend == null) return NotFound();
        _context.Dividends.Remove(dividend);
        await _context.SaveChangesAsync();
        return NoContent();
    }
}

public class CreateTransactionDto
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TransactionType Type { get; set; }
    public decimal Amount { get; set; }
    public DateTime CreatedAt { get; set; }
    public int? StockId { get; set; }
    public string? Description { get; set; }
    public string? InstrumentCode { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public InstrumentCodeType? InstrumentCodeType { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? UnitPrice { get; set; }
}

public class UpdateTransactionDto
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TransactionType Type { get; set; }
    public decimal Amount { get; set; }
    public DateTime CreatedAt { get; set; }
    public int? StockId { get; set; }
    public string? Description { get; set; }
    public string? InstrumentCode { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public InstrumentCodeType? InstrumentCodeType { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? UnitPrice { get; set; }
}

public class UpdatePortfolioBalanceDto
{
    [Range(typeof(decimal), "-9999999999999999.99", "9999999999999999.99")]
    public decimal CashBalance { get; set; }

    /// <summary>
    /// Deprecated: broker credit is no longer included in portfolio totals.
    /// Accepted for backward compatibility but has no effect on calculations.
    /// </summary>
    [Range(typeof(decimal), "-9999999999999999.99", "9999999999999999.99")]
    public decimal? BrokerCredit { get; set; }
}

public class CreateDividendDto
{
    public int StockId { get; set; }
    public decimal Amount { get; set; }
    public DateTime PaidAt { get; set; }
}

internal sealed record NormalizedTransactionSnapshot(
    ActionResult? Error,
    string? InstrumentCode,
    InstrumentCodeType? InstrumentCodeType,
    decimal? Quantity,
    decimal? UnitPrice)
{
    public static NormalizedTransactionSnapshot WithError(ActionResult error) =>
        new(error, null, null, null, null);
}
