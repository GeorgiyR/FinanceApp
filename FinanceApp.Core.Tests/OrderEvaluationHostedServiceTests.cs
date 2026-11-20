using FinanceApp.API.Services;
using FinanceApp.Core.Models;
using FinanceApp.Data.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FinanceApp.Core.Tests;

public class OrderEvaluationHostedServiceTests
{
    [Fact]
    public async Task Cycle_ExecutesLimitBuy_WhenQuoteIsFreshAndCashIsAvailable()
    {
        await using var harness = await OrderHarness.CreateAsync();
        await harness.SeedPortfolioWithCashAsync(portfolioId: 1, cash: 10_000m);
        await harness.SeedStockAsync(10, currentPrice: 90m, quoteAge: TimeSpan.FromMinutes(1));
        await harness.SeedOrderAsync(new Order
        {
            Id = 100,
            PortfolioId = 1,
            StockId = 10,
            Side = OrderSide.Buy,
            Kind = OrderKind.Limit,
            Status = OrderStatus.Pending,
            Quantity = 2m,
            Price = 95m,
            CreatedAt = harness.Now.UtcDateTime,
        });

        var result = await harness.Service.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, result.Executed);
        await using var scope = harness.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var order = await db.Orders.SingleAsync(o => o.Id == 100);
        Assert.Equal(OrderStatus.Executed, order.Status);
        Assert.Equal(OrderExecutionSource.Automatic, order.ExecutionSource);
        Assert.Equal(90m, order.AverageFillPrice);
        Assert.Single(await db.Transactions.Where(t => t.OrderId == 100).ToListAsync());
        Assert.Single(await db.OrderEventsOutbox.ToListAsync());
        Assert.Equal(2m, (await db.PortfolioItems.SingleAsync(i => i.StockId == 10)).Quantity);
    }

    [Fact]
    public async Task Cycle_SkipsStaleQuote()
    {
        await using var harness = await OrderHarness.CreateAsync();
        await harness.SeedPortfolioWithCashAsync(portfolioId: 1, cash: 10_000m);
        await harness.SeedStockAsync(10, currentPrice: 90m, quoteAge: TimeSpan.FromMinutes(45));
        await harness.SeedOrderAsync(new Order
        {
            Id = 101,
            PortfolioId = 1,
            StockId = 10,
            Side = OrderSide.Buy,
            Kind = OrderKind.Limit,
            Status = OrderStatus.Pending,
            Quantity = 1m,
            Price = 95m,
            CreatedAt = harness.Now.UtcDateTime,
        });

        var result = await harness.Service.RunCycleAsync(CancellationToken.None);

        Assert.Equal(0, result.Executed);
        Assert.Equal(1, result.SkippedStaleQuote);
        await using var scope = harness.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(OrderStatus.Pending, (await db.Orders.SingleAsync(o => o.Id == 101)).Status);
    }

    [Fact]
    public async Task Cycle_ActivatesStopLimit_WithoutExecuting()
    {
        await using var harness = await OrderHarness.CreateAsync();
        await harness.SeedPortfolioWithCashAsync(portfolioId: 1, cash: 10_000m);
        await harness.SeedStockAsync(10, currentPrice: 111m, quoteAge: TimeSpan.FromMinutes(1));
        await harness.SeedOrderAsync(new Order
        {
            Id = 102,
            PortfolioId = 1,
            StockId = 10,
            Side = OrderSide.Buy,
            Kind = OrderKind.StopLimit,
            Status = OrderStatus.Pending,
            Quantity = 1m,
            Price = 105m,
            StopPrice = 110m,
            CreatedAt = harness.Now.UtcDateTime,
        });

        var result = await harness.Service.RunCycleAsync(CancellationToken.None);

        Assert.Equal(0, result.Executed);
        Assert.Equal(1, result.Activated);
        await using var scope = harness.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var order = await db.Orders.SingleAsync(o => o.Id == 102);
        Assert.Equal(OrderStatus.Triggered, order.Status);
        Assert.NotNull(order.TriggeredAt);
    }

    private sealed class OrderHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        private OrderHarness(ServiceProvider provider, FixedTimeProvider timeProvider)
        {
            _provider = provider;
            TimeProvider = timeProvider;
            Services = provider;

            var options = Options.Create(new OrderEvaluationOptions
            {
                Enabled = true,
                MaxQuoteAgeMinutes = 15,
                RequireTradingSession = false,
            });

            Service = new OrderEvaluationHostedService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                timeProvider,
                options,
                NullLogger<OrderEvaluationHostedService>.Instance);
        }

        public IServiceProvider Services { get; }
        private FixedTimeProvider TimeProvider { get; }
        public DateTimeOffset Now => TimeProvider.GetUtcNow();
        public OrderEvaluationHostedService Service { get; }

        public static async Task<OrderHarness> CreateAsync()
        {
            var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
            var services = new ServiceCollection();
            var databaseName = Guid.NewGuid().ToString("N");
            services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(databaseName));
            services.AddScoped<IOrderExecutionService, OrderExecutionService>();
            services.AddSingleton<TimeProvider>(timeProvider);

            var provider = services.BuildServiceProvider();
            await using (var scope = provider.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();
            }

            return await Task.FromResult(new OrderHarness(provider, timeProvider));
        }

        public async Task SeedPortfolioWithCashAsync(int portfolioId, decimal cash)
        {
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Portfolios.Add(new Portfolio { Id = portfolioId, Name = "Main", UserId = 1, CreatedAt = Now.UtcDateTime });
            db.Transactions.Add(new Transaction
            {
                PortfolioId = portfolioId,
                Type = TransactionType.Deposit,
                Amount = cash,
                SignedAmount = cash,
                CreatedAt = Now.UtcDateTime,
            });
            await db.SaveChangesAsync();
        }

        public async Task SeedStockAsync(int stockId, decimal currentPrice, TimeSpan quoteAge)
        {
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Stocks.Add(new Stock
            {
                Id = stockId,
                Ticker = $"T{stockId}",
                Name = $"Stock {stockId}",
                CommonName = $"Stock {stockId}",
                Exchange = StockExchanges.Nyse,
                CurrentPrice = currentPrice,
                CurrentPriceAt = Now.UtcDateTime - quoteAge,
                UpdatedAt = Now.UtcDateTime - quoteAge,
                CurrentPriceIsDelayed = false,
            });
            await db.SaveChangesAsync();
        }

        public async Task SeedOrderAsync(Order order)
        {
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Orders.Add(order);
            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
        }

        private sealed class FixedTimeProvider : TimeProvider
        {
            private readonly DateTimeOffset _now;
            public FixedTimeProvider(DateTimeOffset now) => _now = now;
            public override DateTimeOffset GetUtcNow() => _now;
        }
    }
}
