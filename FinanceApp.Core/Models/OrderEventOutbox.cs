namespace FinanceApp.Core.Models;

/// <summary>
/// Transactional outbox for order lifecycle events. The row is written in the same
/// database transaction as the order mutation and published asynchronously.
/// </summary>
public sealed class OrderEventOutbox
{
    public int Id { get; set; }
    public Guid EventId { get; set; } = Guid.NewGuid();
    public string EventType { get; set; } = string.Empty;
    public int OrderId { get; set; }
    public int PortfolioId { get; set; }
    public string Payload { get; set; } = string.Empty;
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}
