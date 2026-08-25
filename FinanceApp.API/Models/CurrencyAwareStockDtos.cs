namespace FinanceApp.API.Models;

public sealed class StockQuoteResponse
{
    public string Symbol { get; init; } = string.Empty;
    public decimal RawCurrentPrice { get; init; }
    public decimal RawPreviousClose { get; init; }
    public decimal RawChange { get; init; }
    public string? Currency { get; init; }
    public string? FinancialCurrency { get; init; }
    public string? NormalizedQuoteCurrency { get; init; }
    public decimal QuoteUnitMultiplier { get; init; }
    public decimal NormalizedCurrentPrice { get; init; }
    public decimal NormalizedPreviousClose { get; init; }
    public decimal NormalizedChange { get; init; }
    public decimal? CurrentPriceEur { get; init; }
    public decimal? ChangeEur { get; init; }
    public decimal PercentChange { get; init; }
    /// <summary>Current regular-session day high in raw quote units. Null when unavailable.</summary>
    public decimal? RawDayHigh { get; init; }
    /// <summary>Current regular-session day low in raw quote units. Null when unavailable.</summary>
    public decimal? RawDayLow { get; init; }
    /// <summary>Day high after quote-unit normalization (e.g. GBp → GBP). Null when unavailable.</summary>
    public decimal? NormalizedDayHigh { get; init; }
    /// <summary>Day low after quote-unit normalization. Null when unavailable.</summary>
    public decimal? NormalizedDayLow { get; init; }
    /// <summary>Day high converted to EUR. Null when unavailable or no rate.</summary>
    public decimal? DayHighEur { get; init; }
    /// <summary>Day low converted to EUR. Null when unavailable or no rate.</summary>
    public decimal? DayLowEur { get; init; }
    public string MarketState { get; init; } = "CLOSED";
    /// <summary>
    /// Session that the returned price belongs to (e.g. "REGULAR", "LAST").
    /// Distinct from <see cref="MarketState"/>, which reflects the current trading session.
    /// </summary>
    public string PriceSession { get; init; } = "REGULAR";
    /// <summary>
    /// UTC timestamp of the price as reported by the provider.
    /// Null when the provider did not supply a reliable timestamp.
    /// </summary>
    public DateTime? PriceTimestampUtc { get; init; }
    /// <summary>
    /// True when <see cref="PriceTimestampUtc"/> is present and more than
    /// <c>24 hours</c> old at the time of the request, or when the quote is
    /// flagged as delayed during an active trading session (see <see cref="DelayWarning"/>).
    /// </summary>
    public bool IsStale { get; init; }
    /// <summary>
    /// Identifies which quote provider supplied the <see cref="RawCurrentPrice"/>.
    /// Populated only for experimental or non-default providers (e.g. <c>"finanzen.net"</c>).
    /// Null when the primary provider (Yahoo/Finnhub) is used.
    /// </summary>
    public string? PriceSource { get; init; }
    /// <summary>
    /// Human-readable Russian-language warning when the provider price appears delayed
    /// during an active trading session (intraday lag exceeded the configured threshold).
    /// Null when the quote is considered fresh.
    /// </summary>
    public string? DelayWarning { get; init; }
    public decimal? RateToEur { get; init; }
    public DateTime? RateTimestampUtc { get; init; }
    public string? RateSource { get; init; }
    public string? ConversionWarning { get; init; }
}

public sealed class StockHistoryResponse
{
    public string Range { get; init; } = string.Empty;
    public string Interval { get; init; } = string.Empty;
    public string? Currency { get; init; }
    public string? FinancialCurrency { get; init; }
    public string? NormalizedQuoteCurrency { get; init; }
    public decimal QuoteUnitMultiplier { get; init; } = 1m;
    public decimal? RateToEur { get; init; }
    public DateTime? RateTimestampUtc { get; init; }
    public string? RateSource { get; init; }
    public string? ConversionWarning { get; init; }
    public DateTime? AsOfUtc { get; init; }
    public DateTime? WindowStartUtc { get; init; }
    public DateTime? WindowEndUtc { get; init; }
    public DateTime? PreviousSessionStartUtc { get; init; }
    public DateTime? PreviousSessionEndUtc { get; init; }
    public DateTime? CurrentSessionStartUtc { get; init; }
    public DateTime? CurrentSessionEndUtc { get; init; }
    public bool? CurrentSessionHasCandles { get; init; }
    public bool IsPotentiallyStale { get; init; }
    public string? StaleReason { get; init; }
    public string? UnavailableReason { get; init; }
    public StockHistoryVolumeMetricsResponse VolumeMetrics { get; init; } = new();
    public IReadOnlyList<StockHistoryPointResponse> Points { get; init; } = Array.Empty<StockHistoryPointResponse>();
}

public sealed class StockHistoryVolumeMetricsResponse
{
    public decimal? AverageVolume20 { get; init; }
    public decimal? AverageVolume50 { get; init; }
    public decimal? RelativeVolume { get; init; }
    /// <summary>
    /// Latest close × latest volume using the same display-price basis as the chart:
    /// EUR when conversion is available, otherwise normalized quote currency units.
    /// </summary>
    public decimal? Turnover { get; init; }
    /// <summary>
    /// Currency code for <see cref="Turnover"/>. Usually EUR when conversion is available;
    /// otherwise the normalized quote currency (or raw quote currency when normalization
    /// leaves the unit unchanged but no normalized code is available).
    /// </summary>
    public string? TurnoverCurrency { get; init; }
    public DateTime? LatestMetricsTimestamp { get; init; }
    /// <summary>
    /// True when the service could reliably exclude an in-progress candle and picked
    /// a completed candle for the latest volume metrics. False when it falls back to
    /// the latest returned candle because completion cannot be determined reliably.
    /// </summary>
    public bool UsesCompletedCandle { get; init; }
}


public sealed class StockHistoryRefreshResponse
{
    public int StockId { get; init; }
    public int DeletedPoints { get; init; }
    public int ImportedPoints { get; init; }
    public bool RateLimited { get; init; }
    public string? AppliedTier { get; init; }
    public bool SkippedNotDue { get; init; }
    public bool StockNotFound { get; init; }
    public DateTime? NextDueAtUtc { get; init; }
}

public sealed class FrankfurtAggregateRebuildRequest
{
    public int BatchSize { get; init; } = 25;
    public int? AfterStockId { get; init; }
}

public sealed class FrankfurtAggregateRebuildResponse
{
    public int BatchSize { get; init; }
    public int? StartedAfterStockId { get; init; }
    public int? NextAfterStockId { get; init; }
    public int ProcessedStocks { get; init; }
    public int RebuiltStocks { get; init; }
    public int FailedStocks { get; init; }
    public bool StoppedDueToRateLimit { get; init; }
    public bool HasMore { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

public sealed class StockHistoryProviderSymbolValidationRequest
{
    public string? CandidateProviderSymbol { get; init; }
}

public sealed class StockHistoryHardResetRequest
{
    public string? CandidateProviderSymbol { get; init; }
    public string ConfirmationText { get; init; } = string.Empty;
}

public sealed record StockHistoryRepairIntervalDiagnosticsResponse
{
    public string Interval { get; init; } = string.Empty;
    public int RequestedCount { get; init; }
    public int FetchedCount { get; init; }
    public int AcceptedCount { get; init; }
    public int RejectedCount { get; init; }
    public int InsertedCount { get; init; }
    public int FinalCount { get; init; }
    public DateTime? FirstTimestampUtc { get; init; }
    public DateTime? LastTimestampUtc { get; init; }
    public bool IsUnavailable { get; init; }
    public string? Message { get; init; }
}

public sealed record StockHistoryRepairDiagnosticsResponse
{
    public int StockId { get; init; }
    public string Ticker { get; init; } = string.Empty;
    public string Exchange { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? ConfiguredProviderSymbol { get; init; }
    public string EffectiveProviderSymbol { get; init; } = string.Empty;
    public string? CandidateOverrideSymbol { get; init; }
    public string Provider { get; init; } = "yahoo";
    /// <summary>success | partial | empty | notFound | rateLimited | validationFailed | failed</summary>
    public string ResultBucket { get; init; } = "failed";
    public string? ProviderQuoteSymbol { get; init; }
    public string? ProviderCurrency { get; init; }
    public DateTime? ProviderPriceTimestampUtc { get; init; }
    public int? RetryAfterSeconds { get; init; }
    public int DeletedRows { get; init; }
    public int InsertedRows { get; init; }
    public int FinalRows { get; init; }
    public bool ResetPerformed { get; init; }
    public IReadOnlyList<StockHistoryRepairIntervalDiagnosticsResponse> Intervals { get; init; } = Array.Empty<StockHistoryRepairIntervalDiagnosticsResponse>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

public sealed class StockHistoryPointResponse
{
    public DateTime Timestamp { get; init; }
    public string Interval { get; init; } = string.Empty;
    public decimal OpenRaw { get; init; }
    public decimal HighRaw { get; init; }
    public decimal LowRaw { get; init; }
    public decimal CloseRaw { get; init; }
    public decimal OpenNormalized { get; init; }
    public decimal HighNormalized { get; init; }
    public decimal LowNormalized { get; init; }
    public decimal CloseNormalized { get; init; }
    public decimal? OpenEur { get; init; }
    public decimal? HighEur { get; init; }
    public decimal? LowEur { get; init; }
    public decimal? CloseEur { get; init; }
    public long Volume { get; init; }
    public bool IsQuoteDerived { get; init; }
}

public sealed class UpdateStockQuoteRequest
{
    public decimal CurrentPrice { get; init; }
    public decimal? CurrentPriceChange { get; init; }
    public decimal? CurrentPriceChangePercent { get; init; }
    public DateTime? CurrentPriceAt { get; init; }
    public bool CurrentPriceIsDelayed { get; init; }
    public string? CurrentPriceDelayWarning { get; init; }
}

public sealed class UpdateStockQuoteResponse
{
    public int StockId { get; init; }
    public decimal CurrentPrice { get; init; }
    public decimal? CurrentPriceChange { get; init; }
    public decimal? CurrentPriceChangePercent { get; init; }
    public DateTime? CurrentPriceAt { get; init; }
    public bool CurrentPriceIsDelayed { get; init; }
    public string? CurrentPriceDelayWarning { get; init; }
    public bool SnapshotApplied { get; init; }
    public bool HistoryApplied { get; init; }
    public bool Applied { get; init; }
}

/// <summary>
/// Request DTO for updating non-identity metadata of an existing stock.
/// Ticker and Exchange are intentionally excluded — they are immutable identity fields.
/// </summary>
public sealed class UpdateStockMetadataRequest
{
    public string Name { get; init; } = string.Empty;
    public string? CommonName { get; init; }
    public string? Wkn { get; init; }
    public string? Isin { get; init; }
    public string? FinanzenNetSlug { get; init; }
    public decimal CurrentPrice { get; init; }
    public int? SectorId { get; init; }
    public int? IndustryId { get; init; }
    public List<int>? MarketIndexIds { get; init; }
}

public sealed class UpdateStockIdentityRequest
{
    public string Ticker { get; init; } = string.Empty;
    public string Exchange { get; init; } = string.Empty;
    public string ConfirmationText { get; init; } = string.Empty;
    public bool RetainProviderSymbol { get; init; }
}

public sealed class StockIdentityChangeResponse
{
    public int StockId { get; init; }
    public string OldTicker { get; init; } = string.Empty;
    public string OldExchange { get; init; } = string.Empty;
    public string NewTicker { get; init; } = string.Empty;
    public string NewExchange { get; init; } = string.Empty;
    public bool IdentityChanged { get; init; }
    public int ClearedHistoryRows { get; init; }
    public bool ClearedFundamentalsSnapshot { get; init; }
    public int ClearedEnrichmentResultRows { get; init; }
    public bool RefreshScheduled { get; init; }
    public string? RefreshWarning { get; init; }
}

public sealed class DeleteStockPermanentRequest
{
    public string ConfirmationText { get; init; } = string.Empty;
}

public sealed class StockDependencyBlockerResponse
{
    public string Category { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public int Count { get; init; }
    public IReadOnlyList<string> RelatedNames { get; init; } = Array.Empty<string>();
}

public sealed class StockDependencyDiagnosticsResponse
{
    public int StockId { get; init; }
    public bool HasBlockers { get; init; }
    public IReadOnlyList<StockDependencyBlockerResponse> Blockers { get; init; } = Array.Empty<StockDependencyBlockerResponse>();
}

public sealed class StockMutationBlockedResponse
{
    public string Message { get; init; } = string.Empty;
    public StockDependencyDiagnosticsResponse Diagnostics { get; init; } = new();
}
