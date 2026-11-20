using System.Text.Json;
using System.Text.Json.Serialization;
using FinanceApp.Core.Models;
using FinanceApp.Core.Models.RabbitMQ.Events;
using FinanceApp.Data.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinanceApp.API.Services;

public sealed class OrderEventOutboxOptions
{
    public bool Enabled { get; init; } = true;
    public int IntervalSeconds { get; init; } = 5;
    public int InitialDelaySeconds { get; init; } = 10;
    public int BatchSize { get; init; } = 50;
    public int MaxAttempts { get; init; } = 10;
    public string QueueName { get; init; } = "financeapp.order-events";
}

/// <summary>
/// In-process consumer hook for order execution events. Invoked by the outbox
/// dispatcher before/alongside the RabbitMQ publication.
/// </summary>
public interface IOrderExecutedHandler
{
    Task HandleAsync(OrderExecutedEvent orderEvent, CancellationToken cancellationToken = default);
}

public sealed class LoggingOrderExecutedHandler : IOrderExecutedHandler
{
    private readonly ILogger<LoggingOrderExecutedHandler> _logger;

    public LoggingOrderExecutedHandler(ILogger<LoggingOrderExecutedHandler> logger)
    {
        _logger = logger;
    }

    public Task HandleAsync(OrderExecutedEvent orderEvent, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Order executed event received: order={OrderId} portfolio={PortfolioId} stock={Ticker} side={Side} kind={Kind} quantity={Quantity} price={FillPrice} source={Source}",
            orderEvent.OrderId,
            orderEvent.PortfolioId,
            orderEvent.Ticker,
            orderEvent.Side,
            orderEvent.Kind,
            orderEvent.Quantity,
            orderEvent.FillPrice,
            orderEvent.Source);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Publishes transactional outbox rows to RabbitMQ and then marks them processed.
/// Failed rows are retried on subsequent cycles with an attempt counter.
/// </summary>
public sealed class OrderEventOutboxDispatcher : BackgroundService
{
    private static readonly JsonSerializerOptions EventJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OrderEventOutboxDispatcher> _logger;
    private readonly OrderEventOutboxOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly SemaphoreSlim _runGate = new(1, 1);

    public OrderEventOutboxDispatcher(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        IOptions<OrderEventOutboxOptions> options,
        ILogger<OrderEventOutboxDispatcher> logger,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
        _delayAsync = delayAsync ?? ((delay, token) => Task.Delay(delay, token));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Order event outbox dispatcher is disabled by configuration.");
            return;
        }

        var initialDelay = TimeSpan.FromSeconds(Math.Max(0, _options.InitialDelaySeconds));
        if (initialDelay > TimeSpan.Zero)
        {
            try
            {
                await _delayAsync(initialDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.IntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Order event outbox dispatch cycle failed unexpectedly.");
            }

            try
            {
                await _delayAsync(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal async Task<int> RunCycleAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return 0;
        }

        if (!await _runGate.WaitAsync(0, cancellationToken))
        {
            return 0;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rabbitMq = scope.ServiceProvider.GetRequiredService<IRabbitMqService>();
            var handlers = scope.ServiceProvider.GetServices<IOrderExecutedHandler>().ToList();

            var pending = await context.OrderEventsOutbox
                .Where(e => e.ProcessedAt == null && e.Attempts < _options.MaxAttempts)
                .OrderBy(e => e.Id)
                .Take(Math.Max(1, _options.BatchSize))
                .ToListAsync(cancellationToken);

            var published = 0;

            foreach (var entry in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (entry.EventType == OrderExecutionService.OrderExecutedEventType && handlers.Count > 0)
                    {
                        var orderEvent = JsonSerializer.Deserialize<OrderExecutedEvent>(entry.Payload, EventJsonOptions);
                        if (orderEvent is not null)
                        {
                            foreach (var handler in handlers)
                            {
                                await handler.HandleAsync(orderEvent, cancellationToken);
                            }
                        }
                    }

                    var routingKey = ResolveRoutingKey(entry.EventType);
                    await rabbitMq.PublishOrderEventAsync(
                        routingKey,
                        _options.QueueName,
                        entry.Payload,
                        cancellationToken);

                    entry.ProcessedAt = _timeProvider.GetUtcNow().UtcDateTime;
                    entry.LastError = null;
                    published++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    entry.Attempts++;
                    entry.LastError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                    _logger.LogWarning(
                        ex,
                        "Failed to publish order event {EventId} ({EventType}); attempt {Attempts}.",
                        entry.EventId,
                        entry.EventType,
                        entry.Attempts);
                }
            }

            if (pending.Count > 0)
            {
                await context.SaveChangesAsync(cancellationToken);
            }

            return published;
        }
        finally
        {
            _runGate.Release();
        }
    }

    private static string ResolveRoutingKey(string eventType) => eventType switch
    {
        OrderExecutionService.OrderRejectedEventType => "orders.rejected",
        _ => "orders.executed",
    };
}
