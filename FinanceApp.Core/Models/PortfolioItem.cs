using System.ComponentModel.DataAnnotations.Schema;

namespace FinanceApp.Core.Models;

public class PortfolioItem
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public Portfolio Portfolio { get; set; } = null!;
    public int StockId { get; set; }
    public Stock Stock { get; set; } = null!;
    [Column(TypeName = "decimal(18,2)")]
    public decimal Quantity { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal BuyPrice { get; set; }
    public DateTime BoughtAt { get; set; }
}
