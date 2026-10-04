using FinanceApp.Core.Models.RabbitMQ.Events;
using RabbitMQ.Client;
using System.Text;
using System.Text.Json;

namespace FinanceApp.API.Services;

public class RabbitMqService : IRabbitMqService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<RabbitMqService> _logger;

    public RabbitMqService(
        IConfiguration configuration,
        ILogger<RabbitMqService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task PublishTransactionCreatedAsync(
        TransactionCreatedEvent message,
        CancellationToken cancellationToken = default)
    {
        var hostName = _configuration["RabbitMQ:HostName"]
            ?? throw new InvalidOperationException("RabbitMQ:HostName is not configured.");

        var port = _configuration.GetValue<int?>("RabbitMQ:Port") ?? 5672;

        var userName = _configuration["RabbitMQ:UserName"]
            ?? throw new InvalidOperationException("RabbitMQ:UserName is not configured.");

        var password = _configuration["RabbitMQ:Password"]
            ?? throw new InvalidOperationException("RabbitMQ:Password is not configured.");

        var virtualHost = _configuration["RabbitMQ:VirtualHost"]
            ?? "/";

        var exchange = _configuration["RabbitMQ:Exchange"]
            ?? throw new InvalidOperationException("RabbitMQ:Exchange is not configured.");

        var queue = _configuration["RabbitMQ:TransactionCreatedQueue"]
            ?? throw new InvalidOperationException(
                "RabbitMQ:TransactionCreatedQueue is not configured.");

        const string routingKey = "transaction.created";

        var factory = new ConnectionFactory
        {
            HostName = hostName,
            Port = port,
            UserName = userName,
            Password = password,
            VirtualHost = virtualHost
        };

        await using var connection =
            await factory.CreateConnectionAsync(cancellationToken);

        await using var channel =
            await connection.CreateChannelAsync(
                cancellationToken: cancellationToken);

        await channel.ExchangeDeclareAsync(
            exchange: exchange,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(
            queue: queue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);

        await channel.QueueBindAsync(
            queue: queue,
            exchange: exchange,
            routingKey: routingKey,
            cancellationToken: cancellationToken);

        var json = JsonSerializer.Serialize(message);
        var body = Encoding.UTF8.GetBytes(json);

        var properties = new BasicProperties
        {
            Persistent = true,
            ContentType = "application/json"
        };

        await channel.BasicPublishAsync(
            exchange: exchange,
            routingKey: routingKey,
            mandatory: false,
            basicProperties: properties,
            body: body,
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "Published TransactionCreated event. TransactionId: {TransactionId}, PortfolioId: {PortfolioId}",
            message.TransactionId,
            message.PortfolioId);
    }
}