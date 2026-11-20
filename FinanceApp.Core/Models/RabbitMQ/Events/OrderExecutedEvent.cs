namespace FinanceApp.Core.Models.RabbitMQ.Events
{
    public sealed record OrderExecutedEvent(
        Guid EventId,
        int OrderId,
        int PortfolioId,
        int UserId,
        int StockId,
        string Ticker,
        string StockName,
        OrderSide Side,
        OrderKind Kind,
        decimal Quantity,
        decimal FillPrice,
        decimal Total,
        DateTime ExecutedAt,
        OrderExecutionSource Source,
        string? TriggerReason);
}
