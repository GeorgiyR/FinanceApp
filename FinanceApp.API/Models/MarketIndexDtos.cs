using System.Text.Json.Serialization;

namespace FinanceApp.API.Models;

public sealed class MarketIndexDto
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string NormalizedName { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public string NormalizedCode { get; init; } = string.Empty;
    public string? ProviderSymbol { get; init; }
    public string Description { get; init; } = string.Empty;
    public string CountryOrRegion { get; init; } = string.Empty;
    public int SortOrder { get; init; }
    public bool IsArchived { get; init; }
    public bool ShowInNavigation { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public sealed class UpsertMarketIndexRequest
{
    public string Name { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public string? ProviderSymbol { get; init; }
    public string? Description { get; init; }
    public string? CountryOrRegion { get; init; }
    public int SortOrder { get; init; }
    public bool? ShowInNavigation { get; init; }
}

public sealed class MarketIndexHistoryPointDto
{
    public DateTime Timestamp { get; init; }
    public string Interval { get; init; } = string.Empty;
    public decimal Open { get; init; }
    public decimal High { get; init; }
    public decimal Low { get; init; }
    public decimal Close { get; init; }
    public long? Volume { get; init; }
}

public sealed class MarketIndexHistoryResponse
{
    public int MarketIndexId { get; init; }
    public string Range { get; init; } = string.Empty;
    public string Interval { get; init; } = string.Empty;
    public decimal? CurrentPrice { get; init; }
    public DateTime? CurrentPriceAt { get; init; }
    public bool CurrentPriceIsDelayed { get; init; }
    public string? CurrentPriceDelayWarning { get; init; }
    public bool IsStale { get; init; }
    public string? StaleReason { get; init; }
    public IReadOnlyList<MarketIndexHistoryPointDto> Points { get; init; } = Array.Empty<MarketIndexHistoryPointDto>();
}

public sealed class MarketIndexRefreshResponse
{
    public int MarketIndexId { get; init; }
    public int DeletedPoints { get; init; }
    public int ImportedPoints { get; init; }
}

/// <summary>DTO representing one current or historical constituent of an index.</summary>
public sealed class IndexConstituentDto
{
    public int StockId { get; init; }
    public string Ticker { get; init; } = string.Empty;
    public string? ProviderSymbol { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? CommonName { get; init; }
    public string? Sector { get; init; }
    public string? Industry { get; init; }
    public string Exchange { get; init; } = string.Empty;
    public string? Isin { get; init; }
    public string? Wkn { get; init; }
    public string? FinanzenNetSlug { get; init; }
    public decimal? CurrentPrice { get; init; }
    public decimal? CurrentPriceChange { get; init; }
    public decimal? CurrentPriceChangePercent { get; init; }
    public DateTime? CurrentPriceAt { get; init; }
    public bool CurrentPriceIsDelayed { get; init; }
    public string? CurrentPriceDelayWarning { get; init; }
    /// <summary>"CatalogOnly" or "Tracked"</summary>
    public string TrackingStatus { get; init; } = string.Empty;
    public string? Source { get; init; }
    public string? ProviderConstituentKey { get; init; }
    public DateTime? EffectiveFrom { get; init; }
    public DateTime? EffectiveTo { get; init; }
    public DateTime? LastVerifiedAt { get; init; }
    public DateTime ImportedAt { get; init; }
}

/// <summary>Response for GET /api/market-indices/{id}/constituents</summary>
public sealed class IndexConstituentsResponse
{
    public int MarketIndexId { get; init; }
    public string IndexName { get; init; } = string.Empty;
    public int TotalCount { get; init; }
    public string? Source { get; init; }
    public DateTime? AsOfDate { get; init; }
    public bool IsCuratedSnapshot { get; init; }
    public bool IsStale { get; init; }
    public string? StaleReason { get; init; }
    public IReadOnlyList<IndexConstituentDto> Constituents { get; init; } = Array.Empty<IndexConstituentDto>();
}

/// <summary>Response for POST /api/market-indices/{id}/constituents/refresh</summary>
public sealed class IndexConstituentsRefreshResponse
{
    public int MarketIndexId { get; init; }
    public string ProviderStatus { get; init; } = string.Empty;
    public string? ProviderName { get; init; }
    public string? ProviderMessage { get; init; }
    public DateTime? FetchedAt { get; init; }
    public DateTime? AsOfDate { get; init; }
    public string? SourceUrl { get; init; }
    public bool IsCuratedSnapshot { get; init; }
    public bool IsStale { get; init; }
    public int Added { get; init; }
    public int Updated { get; init; }
    public int Unchanged { get; init; }
    public int Closed { get; init; }
    public int Conflicts { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IndexConstituentHistoryRefreshJobState
{
    Queued,
    Running,
    Succeeded,
    RateLimited,
    Failed,
    Interrupted
}

public sealed class IndexConstituentHistoryRefreshJobResponse
{
    public string JobId { get; init; } = string.Empty;
    public int MarketIndexId { get; init; }
    public int StockId { get; init; }
    public IndexConstituentHistoryRefreshJobState State { get; init; }
    public bool ReusedActiveJob { get; init; }
    public string? StatusUrl { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime? StartedAtUtc { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
    public DateTime? ExpiresAtUtc { get; init; }
    public int DeletedPoints { get; init; }
    public int ImportedPoints { get; init; }
    public string? Error { get; init; }
}

public sealed class IndexConstituentHistoryRefreshBatchResponse
{
    public int MarketIndexId { get; init; }
    public int Total { get; init; }
    public int Attempted { get; init; }
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public int RateLimited { get; init; }
    public int SkippedRateLimited { get; init; }
    public bool StoppedDueToRateLimit { get; init; }
    public bool DetailsTruncated { get; init; }
    public IReadOnlyList<IndexConstituentHistoryRefreshItemResponse> Results { get; init; } = Array.Empty<IndexConstituentHistoryRefreshItemResponse>();
}

public sealed class IndexConstituentHistoryRefreshItemResponse
{
    public int StockId { get; init; }
    public string Ticker { get; init; } = string.Empty;
    public string Exchange { get; init; } = string.Empty;
    /// <summary>Succeeded, Failed, RateLimited, SkippedRateLimited</summary>
    public string Status { get; init; } = string.Empty;
    public int DeletedPoints { get; init; }
    public int ImportedPoints { get; init; }
    public string? Error { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IndexConstituentsBatchQuoteRefreshJobState
{
    Queued,
    Running,
    Succeeded,
    RateLimited,
    Failed,
    Interrupted
}

/// <summary>Performance result for one current constituent over a requested range.</summary>
public sealed class IndexConstituentPerformanceItemDto
{
    public int StockId { get; init; }
    public decimal? StartPrice { get; init; }
    public decimal? EndPrice { get; init; }
    public double? ChangePercent { get; init; }
    public DateTime? StartAtUtc { get; init; }
    public DateTime? EndAtUtc { get; init; }
    public ConstituentPerformanceDataStatus DataStatus { get; init; } = ConstituentPerformanceDataStatus.InsufficientData;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConstituentPerformanceDataStatus
{
    Available,
    InsufficientData,
}

/// <summary>Response for GET /api/market-indices/{indexId}/constituents/performance?range=...</summary>
public sealed class IndexConstituentPerformanceResponse
{
    public int MarketIndexId { get; init; }
    public string Range { get; init; } = string.Empty;
    public DateTime GeneratedAtUtc { get; init; }
    public IReadOnlyList<IndexConstituentPerformanceItemDto> Items { get; init; } = Array.Empty<IndexConstituentPerformanceItemDto>();
}

public enum IndexConstituentsBatchQuoteRefreshJobEnqueueStatus
{
    Enqueued,
    ReusedActiveJob,
    QueueFull
}

public sealed class IndexConstituentsBatchQuoteRefreshJobEnqueueResult
{
    public required IndexConstituentsBatchQuoteRefreshJobEnqueueStatus Status { get; init; }
    public IndexConstituentsBatchQuoteRefreshJobResponse? Job { get; init; }
}

public sealed class IndexConstituentsBatchQuoteRefreshJobResponse
{
    public string JobId { get; init; } = string.Empty;
    public int MarketIndexId { get; init; }
    public IndexConstituentsBatchQuoteRefreshJobState State { get; init; }
    public bool ReusedActiveJob { get; init; }
    public string? StatusUrl { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime? StartedAtUtc { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
    public DateTime? ExpiresAtUtc { get; init; }
    public int Total { get; init; }
    public int Processed { get; init; }
    public int Remaining { get; init; }
    public int Succeeded { get; init; }
    public int Delayed { get; init; }
    public int NoEurConversion { get; init; }
    public int StaleRejected { get; init; }
    public int ProviderFailed { get; init; }
    public int PersistFailed { get; init; }
    public int RateLimited { get; init; }
    public int RateLimitRetries { get; init; }
    public int RateLimitedSkipped { get; init; }
    public bool IsWaitingForRetry { get; init; }
    public DateTime? NextRetryAtUtc { get; init; }
    public string? Error { get; init; }
}
