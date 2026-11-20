using FinanceApp.API.Services;
using FinanceApp.Core.Models;
using FinanceApp.Data.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FinanceApp.Core.Tests;

public class OrderTransactionSynchronizationTests
{
    [Fact]
    public async Task Sync_MatchesPendingOrderByIsinQuantityAndCondition_ExecutesIt()
    {
        await using var context = CreateContext();
        await SeedPortfolioAsync(context, 1);
        await SeedStockAsync(context, 10, isin: " de0007164600 ");
        context.Orders.Add(new Order
        {
            Id = 100,
            PortfolioId = 1,
            StockId = 10,
            Side = OrderSide.Buy,
            Kind = OrderKind.Limit,
            Status = OrderStatus.Pending,
            Quantity = 2m,
            Price = 95m,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var transaction = new Transaction
        {
            Id = 200,
            PortfolioId = 1,
            Type = TransactionType.Buy,
            Amount = 180m,
            SignedAmount = -180m,
            StockId = 10,
            Quantity = 2m,
            UnitPrice = 90m,
            InstrumentCode = "DE0007164600",
            InstrumentCodeType = InstrumentCodeType.ISIN,
            CreatedAt = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc),
        };
        context.Transactions.Add(transaction);
        await context.SaveChangesAsync();

        var service = new OrderExecutionService(context, TimeProvider.System);
        var synced = await service.SyncFromTransactionAsync(transaction);

        Assert.NotNull(synced);
        Assert.Equal(100, synced!.Id);
        Assert.Equal(OrderStatus.Executed, synced.Status);
        Assert.Equal(90m, synced.AverageFillPrice);
        Assert.Equal(synced.Id, transaction.OrderId);
        Assert.Single(await context.OrderEventsOutbox.ToListAsync());
    }

    [Fact]
    public async Task Sync_WithoutMatchingOrder_CreatesExecutedOrderAndLinksTransaction()
    {
        await using var context = CreateContext();
        await SeedPortfolioAsync(context, 1);
        await SeedStockAsync(context, 11, isin: "US0378331005");

        var transaction = new Transaction
        {
            Id = 201,
            PortfolioId = 1,
            Type = TransactionType.Sell,
            Amount = 1000m,
            SignedAmount = 1000m,
            StockId = 11,
            Quantity = 5m,
            UnitPrice = 200m,
            InstrumentCode = "US0378331005",
            InstrumentCodeType = InstrumentCodeType.ISIN,
            CreatedAt = new DateTime(2026, 11, 20, 11, 0, 0, DateTimeKind.Utc),
        };
        context.Transactions.Add(transaction);
        await context.SaveChangesAsync();

        var service = new OrderExecutionService(context, TimeProvider.System);
        var synced = await service.SyncFromTransactionAsync(transaction);

        Assert.NotNull(synced);
        Assert.NotEqual(0, synced!.Id);
        Assert.Equal(OrderSide.Sell, synced.Side);
        Assert.Equal(OrderKind.Limit, synced.Kind);
        Assert.Equal(OrderStatus.Executed, synced.Status);
        Assert.Equal(5m, synced.FilledQuantity);
        Assert.Equal(200m, synced.AverageFillPrice);
        Assert.Equal(synced.Id, transaction.OrderId);
        Assert.Single(await context.OrderEventsOutbox.ToListAsync());
    }

    [Fact]
    public async Task Sync_QuantityMismatch_CreatesSeparateOrderInsteadOfExecuting()
    {
        await using var context = CreateContext();
        await SeedPortfolioAsync(context, 1);
        await SeedStockAsync(context, 12, isin: "DE0007164600");
        context.Orders.Add(new Order
        {
            Id = 101,
            PortfolioId = 1,
            StockId = 12,
            Side = OrderSide.Buy,
            Kind = OrderKind.Limit,
            Status = OrderStatus.Pending,
            Quantity = 3m,
            Price = 95m,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var transaction = new Transaction
        {
            Id = 202,
            PortfolioId = 1,
            Type = TransactionType.Buy,
            Amount = 200m,
            SignedAmount = -200m,
            StockId = 12,
            Quantity = 2m,
            UnitPrice = 99m,
            InstrumentCode = "DE0007164600",
            InstrumentCodeType = InstrumentCodeType.ISIN,
            CreatedAt = DateTime.UtcNow,
        };
        context.Transactions.Add(transaction);
        await context.SaveChangesAsync();

        var service = new OrderExecutionService(context, TimeProvider.System);
        var synced = await service.SyncFromTransactionAsync(transaction);

        Assert.NotNull(synced);
        Assert.NotEqual(101, synced!.Id);
        var pending = await context.Orders.SingleAsync(o => o.Id == 101);
        Assert.Equal(OrderStatus.Pending, pending.Status);
    }

    [Fact]
    public async Task Sync_LinkedTransaction_IsIgnored()
    {
        await using var context = CreateContext();
        await SeedPortfolioAsync(context, 1);
        await SeedStockAsync(context, 13, isin: "US0378331005");
        context.Orders.Add(new Order
        {
            Id = 102,
            PortfolioId = 1,
            StockId = 13,
            Side = OrderSide.Buy,
            Kind = OrderKind.Market,
            Status = OrderStatus.Executed,
            Quantity = 1m,
            FilledQuantity = 1m,
            Price = 100m,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var transaction = new Transaction
        {
            Id = 203,
            PortfolioId = 1,
            Type = TransactionType.Buy,
            Amount = 100m,
            SignedAmount = -100m,
            StockId = 13,
            Quantity = 1m,
            UnitPrice = 100m,
            OrderId = 102,
            CreatedAt = DateTime.UtcNow,
        };
        context.Transactions.Add(transaction);
        await context.SaveChangesAsync();

        var service = new OrderExecutionService(context, TimeProvider.System);
        var synced = await service.SyncFromTransactionAsync(transaction);

        Assert.Null(synced);
        Assert.Equal(1, await context.Orders.CountAsync());
        Assert.Empty(await context.OrderEventsOutbox.ToListAsync());
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new AppDbContext(options);
    }

    private static async Task SeedPortfolioAsync(AppDbContext context, int id)
    {
        context.Portfolios.Add(new Portfolio { Id = id, Name = "Main", UserId = 1, CreatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();
    }

    private static async Task SeedStockAsync(AppDbContext context, int id, string isin)
    {
        context.Stocks.Add(new Stock
        {
            Id = id,
            Ticker = $"T{id}",
            Name = $"Stock {id}",
            CommonName = $"Stock {id}",
            Exchange = StockExchanges.Frankfurt,
            Isin = isin,
            CurrentPrice = 100m,
            UpdatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
    }
}
