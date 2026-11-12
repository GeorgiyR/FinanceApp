namespace FinanceApp.Blazor.Components.Models;

public sealed class TrackedStockViewModel
{
    public int Id { get; set; }
    public string Ticker { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string CommonName { get; set; } = string.Empty;
    public string Exchange { get; set; } = string.Empty;
    public decimal CurrentPrice { get; set; }
    public decimal? CurrentPriceChange { get; set; }
    public decimal? CurrentPriceChangePercent { get; set; }
    public DateTime? CurrentPriceAt { get; set; }
    public bool CurrentPriceIsDelayed { get; set; }
    public string? CurrentPriceDelayWarning { get; set; }
    public string? Wkn { get; set; }
    public string? Isin { get; set; }
    public string? FinanzenNetSlug { get; set; }
    public string? ProviderSymbol { get; set; }
    public int? SectorId { get; set; }
    public int? IndustryId { get; set; }
    public List<int> MarketIndexIds { get; set; } = [];
    public TrackedStockSectorViewModel? Sector { get; set; }
    public TrackedStockIndustryViewModel? Industry { get; set; }
    public int TrackingStatus { get; set; }
    public int PurchaseCandidatePriority { get; set; }
    public StockLiveQuoteViewModel? LiveQuote { get; set; }
}

public sealed class TrackedStockSectorViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsArchived { get; set; }
    public List<TrackedStockIndustryViewModel> Industries { get; set; } = [];
}

public sealed class TrackedStockIndustryViewModel
{
    public int Id { get; set; }
    public int SectorId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsArchived { get; set; }
    public TrackedStockSectorViewModel? Sector { get; set; }
}

public sealed class TrackedStockFiltersViewModel
{
    public List<string> Exchanges { get; set; } = [];
    public List<int> SectorIds { get; set; } = [];
    public List<int> IndustryIds { get; set; } = [];
}

public sealed class TrackedSectorDirectoryViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsArchived { get; set; }
    public List<TrackedIndustryDirectoryViewModel> Industries { get; set; } = [];
}

public sealed class TrackedIndustryDirectoryViewModel
{
    public int Id { get; set; }
    public int SectorId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsArchived { get; set; }
}

public sealed class TrackedMarketIndexDirectoryViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsArchived { get; set; }
}

public sealed class StockPerformanceResponse
{
    public List<StockPerformanceItem> Items { get; set; } = [];
}

public sealed class StockPerformanceItem
{
    public int StockId { get; set; }
    public decimal? ChangePercent { get; set; }
}

public sealed class StockLiveQuoteViewModel
{
    public decimal RawCurrentPrice { get; set; }
    public string? Currency { get; set; }
    public string? NormalizedQuoteCurrency { get; set; }
    public decimal QuoteUnitMultiplier { get; set; } = 1m;
    public decimal NormalizedCurrentPrice { get; set; }
    public decimal? CurrentPriceEur { get; set; }
    public decimal? ChangeEur { get; set; }
    public decimal? PercentChange { get; set; }
    public DateTime? PriceTimestampUtc { get; set; }
    public bool IsStale { get; set; }
    public string? DelayWarning { get; set; }
    public string? ConversionWarning { get; set; }
    public string MarketState { get; set; } = "CLOSED";
}

public sealed class TrackedPortfolioViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<TrackedPortfolioItemViewModel> Items { get; set; } = [];
}

public sealed class TrackedPortfolioItemViewModel
{
    public int StockId { get; set; }
}

public sealed class TrackedStockQuoteUpdateViewModel
{
    public int StockId { get; set; }
    public decimal CurrentPrice { get; set; }
    public decimal? CurrentPriceChange { get; set; }
    public decimal? CurrentPriceChangePercent { get; set; }
    public DateTime? CurrentPriceAt { get; set; }
    public bool CurrentPriceIsDelayed { get; set; }
    public string? CurrentPriceDelayWarning { get; set; }
}
