namespace FinanceApp.Core.Models;

public enum EarningsDateStatus
{
    Estimated,
    Confirmed,
    Unknown
}

public class EarningsEvent
{
    public int Id { get; set; }
    public int SnapshotId { get; set; }
    /// <summary>Actual earnings release date, when provided by the provider calendar.</summary>
    public DateTime? ReportDate { get; set; }
    /// <summary>Fiscal period end date the report refers to.</summary>
    public DateTime? FiscalPeriodEnd { get; set; }
    /// <summary>End of a report date range, if the provider returns a range.</summary>
    public DateTime? ReportDateEnd { get; set; }
    public EarningsDateStatus DateStatus { get; set; } = EarningsDateStatus.Unknown;
    public decimal? EpsEstimate { get; set; }
    public decimal? EpsReported { get; set; }
    public decimal? RevenueEstimate { get; set; }
    public decimal? RevenueReported { get; set; }
    /// <summary>Fiscal year label, e.g. "4Q2024".</summary>
    public string? FiscalPeriod { get; set; }
    public string Source { get; set; } = "Yahoo Finance";
    public DateTime FetchedAtUtc { get; set; }

    public CompanyFundamentalsSnapshot? Snapshot { get; set; }
}
