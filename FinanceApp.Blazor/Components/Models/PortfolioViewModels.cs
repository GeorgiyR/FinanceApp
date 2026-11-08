namespace FinanceApp.Blazor.Components.Models;

public sealed class PortfolioDetailsViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<PortfolioPositionViewModel> Items { get; set; } = [];
}

public sealed class PortfolioPositionViewModel
{
    public int Id { get; set; }
    public int StockId { get; set; }
    public decimal Quantity { get; set; }
    public decimal BuyPrice { get; set; }
    public PortfolioStockViewModel? Stock { get; set; }
}

public sealed class PortfolioStockViewModel
{
    public int Id { get; set; }
    public string Ticker { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? CommonName { get; set; }
    public string? Exchange { get; set; }
    public string? Isin { get; set; }
    public decimal CurrentPrice { get; set; }
    public decimal? CurrentPriceChange { get; set; }
    public decimal? CurrentPriceChangePercent { get; set; }
    public DateTime? CurrentPriceAt { get; set; }
}

public sealed class PortfolioTransactionViewModel
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public string Type { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public decimal SignedAmount { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public int? StockId { get; set; }
    public PortfolioTransactionStockViewModel? Stock { get; set; }
    public int? OrderId { get; set; }
    public string? InstrumentCode { get; set; }
    public string? InstrumentCodeType { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? UnitPrice { get; set; }
}

public sealed class PortfolioTransactionStockViewModel
{
    public string Ticker { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? CommonName { get; set; }
    public string? Exchange { get; set; }
    public string? Isin { get; set; }
}
