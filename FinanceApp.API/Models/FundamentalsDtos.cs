namespace FinanceApp.API.Models;

public sealed class FundamentalsResponse
{
    public int StockId { get; init; }
    public string State { get; init; } = string.Empty;
    public string? WarningMessage { get; init; }
    public FundamentalsSnapshotDto? Snapshot { get; init; }
    public IReadOnlyList<FinancialPeriodDto> Periods { get; init; } = Array.Empty<FinancialPeriodDto>();
    public IReadOnlyList<EarningsEventDto> EarningsEvents { get; init; } = Array.Empty<EarningsEventDto>();
}

public sealed class FundamentalsSnapshotDto
{
    public int Id { get; init; }
    public string SourceSymbol { get; init; } = string.Empty;
    public decimal? MarketCap { get; init; }
    public decimal? EnterpriseValue { get; init; }
    public decimal? TotalDebt { get; init; }
    public decimal? CashAndEquivalents { get; init; }
    public decimal? RevenueTtm { get; init; }
    public decimal? NetIncomeTtm { get; init; }
    public decimal? EbitdaTtm { get; init; }
    public decimal? OperatingIncomeTtm { get; init; }
    public decimal? FreeCashFlowTtm { get; init; }
    public decimal? TotalAssets { get; init; }
    public decimal? TotalLiabilities { get; init; }
    public decimal? PeRatio { get; init; }
    public decimal? ForwardPeRatio { get; init; }
    public decimal? PbRatio { get; init; }
    public decimal? DividendYield { get; init; }
    public string? Currency { get; init; }
    public string Source { get; init; } = "Yahoo Finance";
    public DateTime? AsOfDate { get; init; }
    public DateTime FetchedAtUtc { get; init; }
}

public sealed class FinancialPeriodDto
{
    public int Id { get; init; }
    public string PeriodType { get; init; } = string.Empty;
    public int? FiscalYear { get; init; }
    public int? FiscalQuarter { get; init; }
    public DateTime? PeriodEndDate { get; init; }
    public string? ReportedCurrency { get; init; }
    public decimal? Revenue { get; init; }
    public decimal? OperatingIncome { get; init; }
    public decimal? NetIncome { get; init; }
    public decimal? EpsReported { get; init; }
    public decimal? EpsEstimate { get; init; }
    public decimal? Ebitda { get; init; }
    public decimal? TotalDebt { get; init; }
    public decimal? TotalAssets { get; init; }
    public decimal? TotalLiabilities { get; init; }
    public decimal? FreeCashFlow { get; init; }
    public string Source { get; init; } = "Yahoo Finance";
    public DateTime? AsOfDate { get; init; }
    public DateTime FetchedAtUtc { get; init; }
}

public sealed class EarningsEventDto
{
    public int Id { get; init; }
    public DateTime? ReportDate { get; init; }
    public DateTime? FiscalPeriodEnd { get; init; }
    public DateTime? ReportDateEnd { get; init; }
    public string DateStatus { get; init; } = string.Empty;
    public decimal? EpsEstimate { get; init; }
    public decimal? EpsReported { get; init; }
    public decimal? RevenueEstimate { get; init; }
    public decimal? RevenueReported { get; init; }
    public string? FiscalPeriod { get; init; }
    public string Source { get; init; } = "Yahoo Finance";
    public DateTime FetchedAtUtc { get; init; }
}
