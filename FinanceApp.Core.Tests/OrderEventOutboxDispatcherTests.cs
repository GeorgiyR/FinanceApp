using System.Text.Json;
using FinanceApp.API.Services;
using FinanceApp.Core.Models;
using FinanceApp.Core.Models.RabbitMQ.Events;
using FinanceApp.Data.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FinanceApp.Core.Tests;

public class OrderEventOutboxDispatcherTests
{
    [Fact]
    public async Task Cycle_PublishesPendingEvent_AndMarksItProcessed()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(databaseName));
        var rabbitMq = new FakeRabbitMqService();
        var handler = new FakeOrderExecutedHandler();
        services.AddSingleton<IRabbitMqService>(rabbitMq);
        services.AddSingleton<IOrderExecutedHandler>(handler);

        await using var provider = services.BuildServiceProvider();
        await using (var seedScope = provider.CreateAsyncScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.OrderEventsOutbox.Add(new OrderEventOutbox
            {
                EventId = Guid.NewGuid(),
                EventType = OrderExecutionService.OrderExecutedEventType,
                OrderId = 1,
                PortfolioId = 2,
                Payload = JsonSerializer.Serialize(new OrderExecutedEvent(
                    Guid.NewGuid(),
                    OrderId: 1,
                    PortfolioId: 2,
                    UserId: 3,
                    StockId: 4,
                    Ticker: "SAP",
                    StockName: "SAP SE",
                    Side: OrderSide.Buy,
                    Kind: OrderKind.Limit,
                    Quantity: 2m,
                    FillPrice: 100m,
                    Total: 200m,
                    ExecutedAt: DateTime.UtcNow,
                    Source: OrderExecutionSource.Automatic,
                    TriggerReason: "test"), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                OccurredAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var dispatcher = new OrderEventOutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            Options.Create(new OrderEventOutboxOptions { Enabled = true }),
            NullLogger<OrderEventOutboxDispatcher>.Instance);

        var published = await dispatcher.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, published);
        Assert.Single(rabbitMq.Published);
        Assert.Equal("orders.executed", rabbitMq.Published[0].RoutingKey);
        Assert.Single(handler.Events);

        await using var verifyScope = provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entry = await verifyDb.OrderEventsOutbox.SingleAsync();
        Assert.NotNull(entry.ProcessedAt);
        Assert.Equal(0, entry.Attempts);
    }

    private sealed class FakeRabbitMqService : IRabbitMqService
    {
        public List<(string RoutingKey, string Queue, string Payload)> Published { get; } = [];

        public Task PublishUpdatePricesAsync(
            FinanceApp.Core.Models.RabbitMQ.Commands.UpdatePricesCommand message,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PublishOrderEventAsync(
            string routingKey,
            string queueName,
            string payload,
            CancellationToken cancellationToken = default)
        {
            Published.Add((routingKey, queueName, payload));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeOrderExecutedHandler : IOrderExecutedHandler
    {
        public List<OrderExecutedEvent> Events { get; } = [];

        public Task HandleAsync(OrderExecutedEvent orderEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(orderEvent);
            return Task.CompletedTask;
        }
    }
}
