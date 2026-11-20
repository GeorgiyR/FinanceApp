using System.Security.Claims;
using FinanceApp.API.Controllers;
using FinanceApp.API.Services;
using FinanceApp.Core.Models;
using FinanceApp.Data.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Xunit;

namespace FinanceApp.Core.Tests;

public class FinanceControllerTests
{
    [Fact]
    public async Task CreateTransaction_AllowsManualBuyWithoutStock_AndPreservesCreatedAt()
    {
        await using var context = CreateContext();
        context.Portfolios.Add(new Portfolio { Id = 10, Name = "Main", UserId = 1, CreatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var controller = CreateController(context, userId: 1);
        var createdAtUtc = new DateTime(2026, 8, 1, 9, 30, 0, DateTimeKind.Utc);

        var result = await controller.CreateTransaction(10, new CreateTransactionDto
        {
            Type = TransactionType.Buy,
            Amount = 125.50m,
            CreatedAt = createdAtUtc,
            Description = "Ручная покупка",
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var transaction = Assert.IsType<Transaction>(ok.Value);

        Assert.Null(transaction.StockId);
        Assert.Equal(createdAtUtc, transaction.CreatedAt);
        Assert.Equal(createdAtUtc, await context.Transactions.Select(t => t.CreatedAt).SingleAsync());
    }

    [Fact]
    public async Task UpdateTransaction_ConvertsLocalCreatedAtToUtc_AndAllowsClearingStock()
    {
        await using var context = CreateContext();
        var stock = new Stock
        {
            Id = 50,
            Ticker = "GS",
            Name = "Goldman Sachs Group",
            CommonName = "Goldman Sachs Group",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 100m,
            UpdatedAt = DateTime.UtcNow,
        };
        var portfolio = new Portfolio { Id = 11, Name = "Main", UserId = 1, CreatedAt = DateTime.UtcNow };
        var transaction = new Transaction
        {
            Id = 70,
            PortfolioId = 11,
            Type = TransactionType.Dividend,
            Amount = 10m,
            SignedAmount = 10m,
            StockId = 50,
            CreatedAt = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc),
        };

        context.Stocks.Add(stock);
        context.Portfolios.Add(portfolio);
        context.Transactions.Add(transaction);
        await context.SaveChangesAsync();

        var controller = CreateController(context, userId: 1);
        var localCreatedAt = new DateTime(2026, 8, 1, 14, 45, 0, DateTimeKind.Local);

        var result = await controller.UpdateTransaction(11, 70, new UpdateTransactionDto
        {
            Type = TransactionType.Dividend,
            Amount = 11m,
            CreatedAt = localCreatedAt,
            StockId = null,
            Description = "Ручной дивиденд",
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var updated = Assert.IsType<Transaction>(ok.Value);

        Assert.Null(updated.StockId);
        Assert.Equal(localCreatedAt.ToUniversalTime(), updated.CreatedAt);
        Assert.Equal(localCreatedAt.ToUniversalTime(), await context.Transactions.Select(t => t.CreatedAt).SingleAsync());
    }

    [Fact]
    public async Task CreateTransaction_MissingCreatedAt_ReturnsBadRequest()
    {
        await using var context = CreateContext();
        context.Portfolios.Add(new Portfolio { Id = 12, Name = "Main", UserId = 1, CreatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var controller = CreateController(context, userId: 1);

        var result = await controller.CreateTransaction(12, new CreateTransactionDto
        {
            Type = TransactionType.Deposit,
            Amount = 50m,
            CreatedAt = default,
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("CreatedAt is required.", badRequest.Value);
    }

    [Fact]
    public async Task CreateTransaction_WithExplicitSnapshotFields_PersistsNormalizedValues()
    {
        await using var context = CreateContext();
        context.Portfolios.Add(new Portfolio { Id = 17, Name = "Main", UserId = 1, CreatedAt = DateTime.UtcNow });
        context.Stocks.Add(new Stock
        {
            Id = 71,
            Ticker = "AAPL",
            Isin = "US0378331005",
            Name = "Apple",
            CommonName = "Apple",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 100m,
            UpdatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context, userId: 1);

        var result = await controller.CreateTransaction(17, new CreateTransactionDto
        {
            Type = TransactionType.Buy,
            Amount = 125.50m,
            CreatedAt = new DateTime(2026, 8, 2, 9, 30, 0, DateTimeKind.Utc),
            StockId = 71,
            InstrumentCode = " us0378331005 ",
            InstrumentCodeType = InstrumentCodeType.ISIN,
            Quantity = 1.23456789m,
            UnitPrice = 101.23456789m,
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var transaction = Assert.IsType<Transaction>(ok.Value);

        Assert.Equal("US0378331005", transaction.InstrumentCode);
        Assert.Equal(InstrumentCodeType.ISIN, transaction.InstrumentCodeType);
        Assert.Equal(1.23456789m, transaction.Quantity);
        Assert.Equal(101.23456789m, transaction.UnitPrice);
        Assert.Equal(-125.50m, transaction.SignedAmount);
    }

    [Fact]
    public async Task UpdateTransaction_WithExplicitSnapshot_DoesNotOverwriteFromStockMetadata()
    {
        await using var context = CreateContext();
        context.Portfolios.Add(new Portfolio { Id = 18, Name = "Main", UserId = 1, CreatedAt = DateTime.UtcNow });
        context.Stocks.Add(new Stock
        {
            Id = 72,
            Ticker = "NVDA",
            Isin = "US67066G1040",
            Name = "NVIDIA",
            CommonName = "NVIDIA",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 100m,
            UpdatedAt = DateTime.UtcNow,
        });
        context.Transactions.Add(new Transaction
        {
            Id = 90,
            PortfolioId = 18,
            Type = TransactionType.Sell,
            Amount = 50m,
            SignedAmount = 50m,
            StockId = 72,
            CreatedAt = new DateTime(2026, 8, 2, 9, 30, 0, DateTimeKind.Utc),
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context, userId: 1);

        var result = await controller.UpdateTransaction(18, 90, new UpdateTransactionDto
        {
            Type = TransactionType.Sell,
            Amount = 55m,
            CreatedAt = new DateTime(2026, 8, 3, 9, 30, 0, DateTimeKind.Utc),
            StockId = 72,
            InstrumentCode = " HIST-123 ",
            InstrumentCodeType = InstrumentCodeType.Ticker,
            Quantity = 3m,
            UnitPrice = 18.5m,
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var transaction = Assert.IsType<Transaction>(ok.Value);

        Assert.Equal("HIST-123", transaction.InstrumentCode);
        Assert.Equal(InstrumentCodeType.Ticker, transaction.InstrumentCodeType);
        Assert.Equal(3m, transaction.Quantity);
        Assert.Equal(18.5m, transaction.UnitPrice);
    }

    [Fact]
    public async Task CreateTransaction_WhitespaceInstrumentCode_NormalizesToNull()
    {
        await using var context = CreateContext();
        context.Portfolios.Add(new Portfolio { Id = 19, Name = "Main", UserId = 1, CreatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var controller = CreateController(context, userId: 1);

        var result = await controller.CreateTransaction(19, new CreateTransactionDto
        {
            Type = TransactionType.Withdrawal,
            Amount = 50m,
            CreatedAt = new DateTime(2026, 8, 4, 9, 30, 0, DateTimeKind.Utc),
            InstrumentCode = "   ",
            InstrumentCodeType = null,
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var transaction = Assert.IsType<Transaction>(ok.Value);

        Assert.Null(transaction.InstrumentCode);
        Assert.Null(transaction.InstrumentCodeType);
        Assert.Null(transaction.Quantity);
        Assert.Null(transaction.UnitPrice);
    }

    [Fact]
    public async Task CreateTransaction_WithCodeOnly_ReturnsBadRequest()
    {
        await using var context = CreateContext();
        context.Portfolios.Add(new Portfolio { Id = 20, Name = "Main", UserId = 1, CreatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var controller = CreateController(context, userId: 1);

        var result = await controller.CreateTransaction(20, new CreateTransactionDto
        {
            Type = TransactionType.Deposit,
            Amount = 50m,
            CreatedAt = new DateTime(2026, 8, 4, 9, 30, 0, DateTimeKind.Utc),
            InstrumentCode = "AAPL",
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("InstrumentCode and InstrumentCodeType must either both be provided or both be null.", badRequest.Value);
    }

    [Fact]
    public async Task CreateTransaction_WithTypeOnly_ReturnsBadRequest()
    {
        await using var context = CreateContext();
        context.Portfolios.Add(new Portfolio { Id = 21, Name = "Main", UserId = 1, CreatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var controller = CreateController(context, userId: 1);

        var result = await controller.CreateTransaction(21, new CreateTransactionDto
        {
            Type = TransactionType.Deposit,
            Amount = 50m,
            CreatedAt = new DateTime(2026, 8, 4, 9, 30, 0, DateTimeKind.Utc),
            InstrumentCodeType = InstrumentCodeType.Ticker,
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("InstrumentCode and InstrumentCodeType must either both be provided or both be null.", badRequest.Value);
    }

    [Fact]
    public async Task CreateTransaction_InvalidIsin_ReturnsBadRequest()
    {
        await using var context = CreateContext();
        context.Portfolios.Add(new Portfolio { Id = 22, Name = "Main", UserId = 1, CreatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var controller = CreateController(context, userId: 1);

        var result = await controller.CreateTransaction(22, new CreateTransactionDto
        {
            Type = TransactionType.Buy,
            Amount = 50m,
            CreatedAt = new DateTime(2026, 8, 4, 9, 30, 0, DateTimeKind.Utc),
            InstrumentCode = "bad",
            InstrumentCodeType = InstrumentCodeType.ISIN,
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(
            "ISIN must contain exactly 12 characters: 2 uppercase letters followed by 10 uppercase alphanumeric characters.",
            badRequest.Value);
    }

    [Fact]
    public async Task CreateTransaction_EmptyTicker_ReturnsBadRequest()
    {
        await using var context = CreateContext();
        context.Portfolios.Add(new Portfolio { Id = 23, Name = "Main", UserId = 1, CreatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var controller = CreateController(context, userId: 1);

        var result = await controller.CreateTransaction(23, new CreateTransactionDto
        {
            Type = TransactionType.Buy,
            Amount = 50m,
            CreatedAt = new DateTime(2026, 8, 4, 9, 30, 0, DateTimeKind.Utc),
            InstrumentCode = "   ",
            InstrumentCodeType = InstrumentCodeType.Ticker,
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("InstrumentCode and InstrumentCodeType must either both be provided or both be null.", badRequest.Value);
    }

    [Fact]
    public async Task CreateTransaction_OverlongTicker_ReturnsBadRequest()
    {
        await using var context = CreateContext();
        context.Portfolios.Add(new Portfolio { Id = 24, Name = "Main", UserId = 1, CreatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var controller = CreateController(context, userId: 1);

        var result = await controller.CreateTransaction(24, new CreateTransactionDto
        {
            Type = TransactionType.Buy,
            Amount = 50m,
            CreatedAt = new DateTime(2026, 8, 4, 9, 30, 0, DateTimeKind.Utc),
            InstrumentCode = new string('T', 33),
            InstrumentCodeType = InstrumentCodeType.Ticker,
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("InstrumentCode must be at most 32 characters.", badRequest.Value);
    }

    [Fact]
    public async Task CreateTransaction_NegativeQuantity_ReturnsBadRequest()
    {
        await using var context = CreateContext();
        context.Portfolios.Add(new Portfolio { Id = 25, Name = "Main", UserId = 1, CreatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var controller = CreateController(context, userId: 1);

        var result = await controller.CreateTransaction(25, new CreateTransactionDto
        {
            Type = TransactionType.Buy,
            Amount = 50m,
            CreatedAt = new DateTime(2026, 8, 4, 9, 30, 0, DateTimeKind.Utc),
            Quantity = -1m,
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Quantity cannot be negative.", badRequest.Value);
    }

    [Fact]
    public async Task CreateTransaction_NegativeUnitPrice_ReturnsBadRequest()
    {
        await using var context = CreateContext();
        context.Portfolios.Add(new Portfolio { Id = 26, Name = "Main", UserId = 1, CreatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var controller = CreateController(context, userId: 1);

        var result = await controller.CreateTransaction(26, new CreateTransactionDto
        {
            Type = TransactionType.Buy,
            Amount = 50m,
            CreatedAt = new DateTime(2026, 8, 4, 9, 30, 0, DateTimeKind.Utc),
            UnitPrice = -1m,
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("UnitPrice cannot be negative.", badRequest.Value);
    }

    [Fact]
    public async Task CreateTransaction_AllowsZeroQuantityAndUnitPrice()
    {
        await using var context = CreateContext();
        context.Portfolios.Add(new Portfolio { Id = 27, Name = "Main", UserId = 1, CreatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var controller = CreateController(context, userId: 1);

        var result = await controller.CreateTransaction(27, new CreateTransactionDto
        {
            Type = TransactionType.Deposit,
            Amount = 50m,
            CreatedAt = new DateTime(2026, 8, 4, 9, 30, 0, DateTimeKind.Utc),
            Quantity = 0m,
            UnitPrice = 0m,
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var transaction = Assert.IsType<Transaction>(ok.Value);

        Assert.Equal(0m, transaction.Quantity);
        Assert.Equal(0m, transaction.UnitPrice);
    }

    [Fact]
    public async Task CreateTransaction_WithStockIdAndOmittedSnapshot_ResolvesIsin()
    {
        await using var context = CreateContext();
        context.Portfolios.Add(new Portfolio { Id = 28, Name = "Main", UserId = 1, CreatedAt = DateTime.UtcNow });
        context.Stocks.Add(new Stock
        {
            Id = 73,
            Ticker = "AAPL",
            Isin = " us0378331005 ",
            Name = "Apple",
            CommonName = "Apple",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 100m,
            UpdatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context, userId: 1);

        var result = await controller.CreateTransaction(28, new CreateTransactionDto
        {
            Type = TransactionType.Buy,
            Amount = 50m,
            CreatedAt = new DateTime(2026, 8, 4, 9, 30, 0, DateTimeKind.Utc),
            StockId = 73,
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var transaction = Assert.IsType<Transaction>(ok.Value);

        Assert.Equal("US0378331005", transaction.InstrumentCode);
        Assert.Equal(InstrumentCodeType.ISIN, transaction.InstrumentCodeType);
    }

    [Fact]
    public async Task CreateTransaction_WithStockIdAndMissingIsin_FallsBackToTicker()
    {
        await using var context = CreateContext();
        context.Portfolios.Add(new Portfolio { Id = 29, Name = "Main", UserId = 1, CreatedAt = DateTime.UtcNow });
        context.Stocks.Add(new Stock
        {
            Id = 74,
            Ticker = " NVDA ",
            Isin = "   ",
            Name = "NVIDIA",
            CommonName = "NVIDIA",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 100m,
            UpdatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context, userId: 1);

        var result = await controller.CreateTransaction(29, new CreateTransactionDto
        {
            Type = TransactionType.Buy,
            Amount = 50m,
            CreatedAt = new DateTime(2026, 8, 4, 9, 30, 0, DateTimeKind.Utc),
            StockId = 74,
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var transaction = Assert.IsType<Transaction>(ok.Value);

        Assert.Equal("NVDA", transaction.InstrumentCode);
        Assert.Equal(InstrumentCodeType.Ticker, transaction.InstrumentCodeType);
    }

    [Fact]
    public async Task CreateTransaction_WithStockIdAndNoUsableInstrumentCode_LeavesSnapshotNull()
    {
        await using var context = CreateContext();
        context.Portfolios.Add(new Portfolio { Id = 30, Name = "Main", UserId = 1, CreatedAt = DateTime.UtcNow });
        context.Stocks.Add(new Stock
        {
            Id = 75,
            Ticker = "   ",
            Isin = "   ",
            Name = "Unknown",
            CommonName = "Unknown",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 100m,
            UpdatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context, userId: 1);

        var result = await controller.CreateTransaction(30, new CreateTransactionDto
        {
            Type = TransactionType.Buy,
            Amount = 50m,
            CreatedAt = new DateTime(2026, 8, 4, 9, 30, 0, DateTimeKind.Utc),
            StockId = 75,
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var transaction = Assert.IsType<Transaction>(ok.Value);

        Assert.Null(transaction.InstrumentCode);
        Assert.Null(transaction.InstrumentCodeType);
    }

    [Fact]
    public async Task SerializeTransactionInstrumentCodeType_UsesExactEnumStrings()
    {
        var transactionJson = JsonSerializer.Serialize(new Transaction
        {
            InstrumentCodeType = InstrumentCodeType.ISIN,
        });
        var dtoJson = JsonSerializer.Serialize(new UpdateTransactionDto
        {
            InstrumentCodeType = InstrumentCodeType.Ticker,
        });

        Assert.Contains("\"InstrumentCodeType\":\"ISIN\"", transactionJson);
        Assert.Contains("\"InstrumentCodeType\":\"Ticker\"", dtoJson);
    }

    [Fact]
    public async Task UpdateBalance_PersistsCashAdjustment_AndReturnsCalculatedTotals()
    {
        await using var context = CreateContext();
        var stock = new Stock
        {
            Id = 60,
            Ticker = "MSFT",
            Name = "Microsoft",
            CommonName = "Microsoft",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 200m,
            UpdatedAt = DateTime.UtcNow,
        };
        var portfolio = new Portfolio
        {
            Id = 13,
            Name = "Main",
            UserId = 1,
            CreatedAt = DateTime.UtcNow,
            BrokerCredit = 5m,
        };

        context.Stocks.Add(stock);
        context.Portfolios.Add(portfolio);
        context.PortfolioItems.Add(new PortfolioItem
        {
            Id = 1,
            PortfolioId = 13,
            StockId = 60,
            Stock = stock,
            Quantity = 2m,
            BuyPrice = 150m,
            BoughtAt = DateTime.UtcNow,
        });
        context.Transactions.Add(new Transaction
        {
            Id = 80,
            PortfolioId = 13,
            Type = TransactionType.Deposit,
            Amount = 40m,
            SignedAmount = 40m,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context, userId: 1);

        // New API: only CashBalance is sent; BrokerCredit omitted (null)
        var updateResult = await controller.UpdateBalance(13, new UpdatePortfolioBalanceDto
        {
            CashBalance = 70.126m,
        });

        var ok = Assert.IsType<OkObjectResult>(updateResult.Result);
        var balance = Assert.IsType<PortfolioBalance>(ok.Value);

        // Cash adjustment = desired cash (70.13) − transaction sum (40) = 30.13
        Assert.Equal(30.13m, await context.Portfolios.Select(p => p.CashBalanceAdjustment).SingleAsync());
        // BrokerCredit unchanged since it was not sent
        Assert.Equal(5m, await context.Portfolios.Select(p => p.BrokerCredit).SingleAsync());
        Assert.Equal(70.13m, balance.CashBalance);
        // StocksValue = 200 * 2 = 400
        Assert.Equal(400m, balance.StocksValue);
        // TotalPortfolioValue = stocksValue + cashBalance (broker credit NOT included)
        Assert.Equal(470.13m, balance.TotalPortfolioValue);
        // TotalBalance equals cashBalance (broker credit excluded from totals)
        Assert.Equal(70.13m, balance.TotalBalance);
    }

    [Fact]
    public async Task UpdateBalance_StoredBrokerCreditDoesNotAffectTotals()
    {
        await using var context = CreateContext();
        var stock = new Stock
        {
            Id = 61,
            Ticker = "AAPL",
            Name = "Apple",
            CommonName = "Apple",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 100m,
            UpdatedAt = DateTime.UtcNow,
        };
        var portfolio = new Portfolio
        {
            Id = 15,
            Name = "BrokerTest",
            UserId = 1,
            CreatedAt = DateTime.UtcNow,
            BrokerCredit = 1000m, // Large broker credit that must NOT affect totals
        };

        context.Stocks.Add(stock);
        context.Portfolios.Add(portfolio);
        context.PortfolioItems.Add(new PortfolioItem
        {
            Id = 2,
            PortfolioId = 15,
            StockId = 61,
            Stock = stock,
            Quantity = 1m,
            BuyPrice = 90m,
            BoughtAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context, userId: 1);

        var result = await controller.GetBalance(15);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var balance = Assert.IsType<PortfolioBalance>(ok.Value);

        Assert.Equal(0m, balance.CashBalance);
        Assert.Equal(100m, balance.StocksValue);
        // TotalPortfolioValue must NOT include the 1000 broker credit
        Assert.Equal(100m, balance.TotalPortfolioValue);
        // BrokerCredit is still returned for compat but does not affect the total
        Assert.Equal(1000m, balance.BrokerCredit);
    }

    [Fact]
    public async Task UpdateBalance_CashEditsPersistedViaCashBalanceAdjustment()
    {
        await using var context = CreateContext();
        var portfolio = new Portfolio { Id = 16, Name = "AdjTest", UserId = 1, CreatedAt = DateTime.UtcNow };
        context.Portfolios.Add(portfolio);
        context.Transactions.Add(new Transaction
        {
            Id = 81,
            PortfolioId = 16,
            Type = TransactionType.Deposit,
            Amount = 100m,
            SignedAmount = 100m,
            CreatedAt = DateTime.UtcNow,
        });
        context.Transactions.Add(new Transaction
        {
            Id = 82,
            PortfolioId = 16,
            Type = TransactionType.Withdrawal,
            Amount = 20m,
            SignedAmount = -20m,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context, userId: 1);

        // Transaction cash = 100 - 20 = 80. Desired cash = 95.
        var result = await controller.UpdateBalance(16, new UpdatePortfolioBalanceDto { CashBalance = 95m });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var balance = Assert.IsType<PortfolioBalance>(ok.Value);

        // Adjustment = 95 - 80 = 15
        Assert.Equal(15m, await context.Portfolios.Select(p => p.CashBalanceAdjustment).SingleAsync());
        Assert.Equal(95m, balance.CashBalance);
        // Verify that a subsequent GetBalance returns the same cash
        var getResult = await controller.GetBalance(16);
        var getOk = Assert.IsType<OkObjectResult>(getResult.Result);
        var getBalance = Assert.IsType<PortfolioBalance>(getOk.Value);
        Assert.Equal(95m, getBalance.CashBalance);
    }

    [Fact]
    public async Task UpdateBalance_ForAnotherUsersPortfolio_ReturnsNotFound()
    {
        await using var context = CreateContext();
        context.Portfolios.Add(new Portfolio { Id = 14, Name = "Other", UserId = 2, CreatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var controller = CreateController(context, userId: 1);

        var result = await controller.UpdateBalance(14, new UpdatePortfolioBalanceDto
        {
            CashBalance = 10m,
        });

        Assert.IsType<NotFoundResult>(result.Result);
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new AppDbContext(options);
    }

    private static FinanceController CreateController(AppDbContext context, int userId)
    {
        return new FinanceController(context, new OrderExecutionService(context, TimeProvider.System))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                    ], "TestAuth"))
                }
            }
        };
    }
}
