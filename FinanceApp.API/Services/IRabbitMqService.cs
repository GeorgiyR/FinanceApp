using FinanceApp.Core.Models.RabbitMQ.Events;

namespace FinanceApp.API.Services;

public interface IRabbitMqService
{
    Task PublishTransactionCreatedAsync(
        TransactionCreatedEvent message,
        CancellationToken cancellationToken = default);
}