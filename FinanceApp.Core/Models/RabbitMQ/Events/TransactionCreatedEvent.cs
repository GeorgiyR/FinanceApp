namespace FinanceApp.Core.Models.RabbitMQ.Events
{
    public sealed record TransactionCreatedEvent(
    int TransactionId,
    int PortfolioId,
    TransactionType Type,
    int? StockId,
    decimal Amount,
    decimal SignedAmount,
    decimal? Quantity,
    decimal? UnitPrice,
    DateTime CreatedAt);
}
