using System.ComponentModel.DataAnnotations.Schema;

namespace FinanceApp.Core.Models;

public class Portfolio
{
    public const int MonetaryScale = 2;

    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int UserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsDemo { get; set; } = false;
    [Column(TypeName = "decimal(18,2)")]
    public decimal BrokerCredit { get; set; } = 0;
    [Column(TypeName = "decimal(18,2)")]
    // Stores a manual adjustment on top of transaction-derived cash so inline balance edits
    // never rewrite or fabricate transaction history.
    public decimal CashBalanceAdjustment { get; set; } = 0;
    public User User { get; set; } = null!;
    public List<PortfolioItem> Items { get; set; } = new();
    public List<Order> Orders { get; set; } = new();
    public List<Transaction> Transactions { get; set; } = new();
    public List<Dividend> Dividends { get; set; } = new();
}
