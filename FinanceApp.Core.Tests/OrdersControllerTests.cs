using System.Security.Claims;
using FinanceApp.API.Controllers;
using FinanceApp.Core.Models;
using FinanceApp.Data.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FinanceApp.Core.Tests;

public class OrdersControllerTests
{
    [Fact]
    public async Task Update_ExecutedBuyOrder_SnapshotsIsinQuantityAndUnitPrice_AndRemainsIdempotent()
    {
        await using var context = CreateContext();
        context.Portfolios.Add(new Portfolio { Id = 1, Name = "Main", UserId = 1, CreatedAt = DateTime.UtcNow });
        context.Stocks.Add(new Stock
        {
            Id = 10,
            Ticker = "AAPL",
            Isin = " us0378331005 ",
            Name = "Apple",
            CommonName = "Apple",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 100m,
            UpdatedAt = DateTime.UtcNow,
        });
        context.Orders.Add(new Order
        {
            Id = 100,
            PortfolioId = 1,
            StockId = 10,
            Type = OrderType.Buy,
            Status = OrderStatus.Pending,
            Quantity = 2.5m,
            Price = 123.45m,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context, 1);
        var dto = new UpdateOrderDto
        {
            Type = OrderType.Buy,
            Status = OrderStatus.Executed,
            Quantity = 2.5m,
            Price = 123.45m,
        };

        var result = await controller.Update(1, 100, dto);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<Order>(ok.Value);

        var transaction = await context.Transactions.SingleAsync();
        Assert.Equal(TransactionType.Buy, transaction.Type);
        Assert.Equal("US0378331005", transaction.InstrumentCode);
        Assert.Equal(InstrumentCodeType.ISIN, transaction.InstrumentCodeType);
        Assert.Equal(2.5m, transaction.Quantity);
        Assert.Equal(123.45m, transaction.UnitPrice);
        Assert.Equal(100, transaction.OrderId);

        await controller.Update(1, 100, dto);
        Assert.Equal(1, await context.Transactions.CountAsync());
    }

    [Fact]
    public async Task Update_ExecutedSellOrder_FallsBackToTickerSnapshot()
    {
        await using var context = CreateContext();
        context.Portfolios.Add(new Portfolio { Id = 2, Name = "Main", UserId = 1, CreatedAt = DateTime.UtcNow });
        context.Stocks.Add(new Stock
        {
            Id = 11,
            Ticker = " NVDA ",
            Isin = "   ",
            Name = "NVIDIA",
            CommonName = "NVIDIA",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 100m,
            UpdatedAt = DateTime.UtcNow,
        });
        context.PortfolioItems.Add(new PortfolioItem
        {
            Id = 1,
            PortfolioId = 2,
            StockId = 11,
            Quantity = 4m,
            BuyPrice = 100m,
            BoughtAt = DateTime.UtcNow,
        });
        context.Orders.Add(new Order
        {
            Id = 101,
            PortfolioId = 2,
            StockId = 11,
            Type = OrderType.Sell,
            Status = OrderStatus.Pending,
            Quantity = 1.25m,
            Price = 210.10m,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context, 1);

        var result = await controller.Update(2, 101, new UpdateOrderDto
        {
            Type = OrderType.Sell,
            Status = OrderStatus.Executed,
            Quantity = 1.25m,
            Price = 210.10m,
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<Order>(ok.Value);

        var transaction = await context.Transactions.SingleAsync();
        Assert.Equal(TransactionType.Sell, transaction.Type);
        Assert.Equal("NVDA", transaction.InstrumentCode);
        Assert.Equal(InstrumentCodeType.Ticker, transaction.InstrumentCodeType);
        Assert.Equal(1.25m, transaction.Quantity);
        Assert.Equal(210.10m, transaction.UnitPrice);
    }

    [Fact]
    public async Task Update_ExecutedBuyOrder_CreatesPositionAndResetsPurchaseCandidatePriority()
    {
        await using var context = CreateContext();
        context.Portfolios.Add(new Portfolio { Id = 3, Name = "Main", UserId = 1, CreatedAt = DateTime.UtcNow });
        context.Stocks.Add(new Stock
        {
            Id = 12,
            Ticker = "MSFT",
            Name = "Microsoft",
            CommonName = "Microsoft",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 100m,
            TrackingStatus = StockTrackingStatus.Tracked,
            PurchaseCandidatePriority = StockPurchaseCandidatePriority.Candidate,
            UpdatedAt = DateTime.UtcNow,
        });
        context.Orders.Add(new Order
        {
            Id = 102,
            PortfolioId = 3,
            StockId = 12,
            Type = OrderType.Buy,
            Status = OrderStatus.Pending,
            Quantity = 1.1m,
            Price = 222.22m,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context, 1);
        var result = await controller.Update(3, 102, new UpdateOrderDto
        {
            Type = OrderType.Buy,
            Status = OrderStatus.Executed,
            Quantity = 1.1m,
            Price = 222.22m,
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<Order>(ok.Value);

        Assert.Equal(1, await context.PortfolioItems.CountAsync(x => x.PortfolioId == 3 && x.StockId == 12));
        var stock = await context.Stocks.AsNoTracking().SingleAsync(x => x.Id == 12);
        Assert.Equal(StockPurchaseCandidatePriority.None, stock.PurchaseCandidatePriority);
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new AppDbContext(options);
    }

    private static OrdersController CreateController(AppDbContext context, int userId)
    {
        return new OrdersController(context)
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
