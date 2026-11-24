using System.Security.Claims;
using FinanceApp.API.Controllers;
using FinanceApp.API.Services;
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
    public async Task Update_ExecutedBuyOrder_SnapshotsIsinQuantityAndUnitPrice()
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
            Side = OrderSide.Buy,
            Kind = OrderKind.Limit,
            Status = OrderStatus.Pending,
            Quantity = 2.5m,
            Price = 123.45m,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context, 1);
        var dto = new UpdateOrderDto
        {
            Type = OrderSide.Buy,
            Status = OrderStatus.Executed,
            Quantity = 2.5m,
            Price = 123.45m,
        };

        var result = await controller.Update(1, 100, dto);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<OrderDto>(ok.Value);

        var transaction = await context.Transactions.SingleAsync();
        Assert.Equal(TransactionType.Buy, transaction.Type);
        Assert.Equal("US0378331005", transaction.InstrumentCode);
        Assert.Equal(InstrumentCodeType.ISIN, transaction.InstrumentCodeType);
        Assert.Equal(2.5m, transaction.Quantity);
        Assert.Equal(123.45m, transaction.UnitPrice);
        Assert.Equal(100, transaction.OrderId);

        var order = await context.Orders.SingleAsync();
        Assert.Equal(OrderStatus.Executed, order.Status);
        Assert.Equal(OrderExecutionSource.Manual, order.ExecutionSource);
    }

    [Fact]
    public async Task Update_ExecutedOrder_IsNotExecutedTwice()
    {
        await using var context = CreateContext();
        context.Portfolios.Add(new Portfolio { Id = 1, Name = "Main", UserId = 1, CreatedAt = DateTime.UtcNow });
        context.Stocks.Add(new Stock
        {
            Id = 10,
            Ticker = "AAPL",
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
            Side = OrderSide.Buy,
            Kind = OrderKind.Limit,
            Status = OrderStatus.Pending,
            Quantity = 2.5m,
            Price = 123.45m,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context, 1);
        var dto = new UpdateOrderDto
        {
            Status = OrderStatus.Executed,
            Quantity = 2.5m,
            Price = 123.45m,
        };

        await controller.Update(1, 100, dto);
        var second = await controller.Update(1, 100, dto);

        Assert.IsType<ConflictObjectResult>(second.Result);
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
            Side = OrderSide.Sell,
            Kind = OrderKind.Limit,
            Status = OrderStatus.Pending,
            Quantity = 1.25m,
            Price = 210.10m,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context, 1);

        var result = await controller.Update(2, 101, new UpdateOrderDto
        {
            Type = OrderSide.Sell,
            Status = OrderStatus.Executed,
            Quantity = 1.25m,
            Price = 210.10m,
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<OrderDto>(ok.Value);

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
            Side = OrderSide.Buy,
            Kind = OrderKind.Limit,
            Status = OrderStatus.Pending,
            Quantity = 1.1m,
            Price = 222.22m,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context, 1);
        var result = await controller.Update(3, 102, new UpdateOrderDto
        {
            Type = OrderSide.Buy,
            Status = OrderStatus.Executed,
            Quantity = 1.1m,
            Price = 222.22m,
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<OrderDto>(ok.Value);

        Assert.Equal(1, await context.PortfolioItems.CountAsync(x => x.PortfolioId == 3 && x.StockId == 12));
        var stock = await context.Stocks.AsNoTracking().SingleAsync(x => x.Id == 12);
        Assert.Equal(StockPurchaseCandidatePriority.None, stock.PurchaseCandidatePriority);
    }

    [Fact]
    public async Task Create_LegacyTypeOnly_CreatesLimitOrder()
    {
        await using var context = CreateContext();
        context.Portfolios.Add(new Portfolio { Id = 4, Name = "Main", UserId = 1, CreatedAt = DateTime.UtcNow });
        context.Stocks.Add(new Stock
        {
            Id = 13,
            Ticker = "SAP",
            Name = "SAP",
            CommonName = "SAP",
            Exchange = StockExchanges.Frankfurt,
            CurrentPrice = 100m,
            UpdatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context, 1);
        var result = await controller.Create(4, new CreateOrderDto
        {
            StockId = 13,
            Type = OrderSide.Buy,
            Quantity = 3m,
            Price = 95m,
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<OrderDto>(ok.Value);
        Assert.Equal(OrderSide.Buy, dto.Side);
        Assert.Equal(OrderKind.Limit, dto.Kind);
        Assert.Equal(OrderStatus.Pending, dto.Status);
        Assert.Equal(3m, dto.Quantity);
    }

    [Fact]
    public async Task Create_MarketOrder_ExecutesImmediately()
    {
        await using var context = CreateContext();
        context.Portfolios.Add(new Portfolio { Id = 5, Name = "Main", UserId = 1, CreatedAt = DateTime.UtcNow });
        context.Stocks.Add(new Stock
        {
            Id = 14,
            Ticker = "SAP",
            Name = "SAP",
            CommonName = "SAP",
            Exchange = StockExchanges.Frankfurt,
            CurrentPrice = 101.5m,
            UpdatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context, 1);
        var result = await controller.Create(5, new CreateOrderDto
        {
            StockId = 14,
            Side = OrderSide.Buy,
            Kind = OrderKind.Market,
            Quantity = 2m,
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<OrderDto>(ok.Value);
        Assert.Equal(OrderStatus.Executed, dto.Status);
        Assert.Equal(101.5m, dto.AverageFillPrice);

        var order = await context.Orders.SingleAsync(o => o.Id == dto.Id);
        Assert.Equal(OrderStatus.Executed, order.Status);
        Assert.Equal(101.5m, order.AverageFillPrice);
        Assert.Single(await context.Transactions.ToListAsync());
        Assert.Single(await context.OrderEventsOutbox.ToListAsync());
    }

    [Fact]
    public async Task Create_StopLimitWithoutStopPrice_IsRejected()
    {
        await using var context = CreateContext();
        context.Portfolios.Add(new Portfolio { Id = 6, Name = "Main", UserId = 1, CreatedAt = DateTime.UtcNow });
        context.Stocks.Add(new Stock
        {
            Id = 15,
            Ticker = "SAP",
            Name = "SAP",
            CommonName = "SAP",
            Exchange = StockExchanges.Frankfurt,
            CurrentPrice = 100m,
            UpdatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context, 1);
        var result = await controller.Create(6, new CreateOrderDto
        {
            StockId = 15,
            Side = OrderSide.Buy,
            Kind = OrderKind.StopLimit,
            Quantity = 1m,
            Price = 100m,
        });

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Cancel_PendingOrder_MarksCancelled()
    {
        await using var context = CreateContext();
        context.Portfolios.Add(new Portfolio { Id = 7, Name = "Main", UserId = 1, CreatedAt = DateTime.UtcNow });
        context.Stocks.Add(new Stock
        {
            Id = 16,
            Ticker = "SAP",
            Name = "SAP",
            CommonName = "SAP",
            Exchange = StockExchanges.Frankfurt,
            CurrentPrice = 100m,
            UpdatedAt = DateTime.UtcNow,
        });
        context.Orders.Add(new Order
        {
            Id = 200,
            PortfolioId = 7,
            StockId = 16,
            Side = OrderSide.Buy,
            Kind = OrderKind.Limit,
            Status = OrderStatus.Pending,
            Quantity = 1m,
            Price = 90m,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context, 1);
        var result = await controller.Cancel(7, 200);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<OrderDto>(ok.Value);
        Assert.Equal(OrderStatus.Cancelled, dto.Status);
        Assert.NotNull(dto.CancelledAt);
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new AppDbContext(options);
    }

    [Fact]
    public async Task GetAll_CalculatesAveragePurchasePriceOfUnsoldShares()
    {
        await using var context = CreateContext();
        context.Portfolios.Add(new Portfolio { Id = 1, Name = "Main", UserId = 1, CreatedAt = DateTime.UtcNow });
        context.Stocks.Add(new Stock
        {
            Id = 10,
            Ticker = "AAPL",
            Name = "Apple",
            CommonName = "Apple",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 100m,
            UpdatedAt = DateTime.UtcNow,
        });
        context.Stocks.Add(new Stock
        {
            Id = 11,
            Ticker = "MSFT",
            Name = "Microsoft",
            CommonName = "Microsoft",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 100m,
            UpdatedAt = DateTime.UtcNow,
        });

        var day = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);
        context.Orders.AddRange(
            new Order
            {
                Id = 1,
                PortfolioId = 1,
                StockId = 10,
                Side = OrderSide.Buy,
                Kind = OrderKind.Market,
                Status = OrderStatus.Executed,
                Quantity = 10m,
                FilledQuantity = 10m,
                Price = 100m,
                AverageFillPrice = 100m,
                CreatedAt = day,
                ExecutedAt = day,
            },
            new Order
            {
                Id = 2,
                PortfolioId = 1,
                StockId = 10,
                Side = OrderSide.Buy,
                Kind = OrderKind.Market,
                Status = OrderStatus.Executed,
                Quantity = 10m,
                FilledQuantity = 10m,
                Price = 200m,
                AverageFillPrice = 200m,
                CreatedAt = day.AddHours(1),
                ExecutedAt = day.AddHours(1),
            },
            new Order
            {
                Id = 3,
                PortfolioId = 1,
                StockId = 10,
                Side = OrderSide.Sell,
                Kind = OrderKind.Market,
                Status = OrderStatus.Executed,
                Quantity = 10m,
                FilledQuantity = 10m,
                Price = 250m,
                AverageFillPrice = 250m,
                CreatedAt = day.AddHours(2),
                ExecutedAt = day.AddHours(2),
            },
            new Order
            {
                Id = 4,
                PortfolioId = 1,
                StockId = 10,
                Side = OrderSide.Buy,
                Kind = OrderKind.Limit,
                Status = OrderStatus.Pending,
                Quantity = 5m,
                Price = 50m,
                CreatedAt = day.AddHours(3),
            },
            new Order
            {
                Id = 5,
                PortfolioId = 1,
                StockId = 11,
                Side = OrderSide.Buy,
                Kind = OrderKind.Market,
                Status = OrderStatus.Executed,
                Quantity = 1m,
                FilledQuantity = 1m,
                Price = 400m,
                AverageFillPrice = 400m,
                CreatedAt = day,
                ExecutedAt = day,
            },
            new Order
            {
                Id = 6,
                PortfolioId = 1,
                StockId = 11,
                Side = OrderSide.Sell,
                Kind = OrderKind.Market,
                Status = OrderStatus.Executed,
                Quantity = 1m,
                FilledQuantity = 1m,
                Price = 450m,
                AverageFillPrice = 450m,
                CreatedAt = day.AddHours(1),
                ExecutedAt = day.AddHours(1),
            });
        await context.SaveChangesAsync();

        var controller = CreateController(context, 1);

        var result = await controller.GetAll(1);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var orders = Assert.IsAssignableFrom<IEnumerable<OrderDto>>(ok.Value).ToList();

        // 10 @ 100 and 10 @ 200, then 10 sold -> remaining cost basis is 10 @ 150.
        Assert.All(orders.Where(o => o.StockId == 10), o => Assert.Equal(150m, o.AveragePurchasePrice));
        // The position is fully sold, so no average purchase price remains.
        Assert.All(orders.Where(o => o.StockId == 11), o => Assert.Null(o.AveragePurchasePrice));
    }

    private static OrdersController CreateController(AppDbContext context, int userId)
    {
        var executionService = new OrderExecutionService(context, TimeProvider.System);
        return new OrdersController(context, executionService, TimeProvider.System)
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
