namespace FinanceApp.Core.Models;

/// <summary>Per-user watchlist membership. A stock may be tracked by multiple users.</summary>
public class UserTrackedStock
{
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public int StockId { get; set; }
    public Stock Stock { get; set; } = null!;
    public DateTime CreatedAtUtc { get; set; }
}
