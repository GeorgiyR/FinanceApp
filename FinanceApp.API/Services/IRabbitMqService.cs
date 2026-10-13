using FinanceApp.Core.Models.RabbitMQ.Commands;

namespace FinanceApp.API.Services;

public interface IRabbitMqService
{
    Task PublishUpdatePricesAsync(
        UpdatePricesCommand message,
        CancellationToken cancellationToken = default);
}