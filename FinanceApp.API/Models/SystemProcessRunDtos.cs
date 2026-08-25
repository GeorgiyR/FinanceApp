using FinanceApp.Core.Models;
using System.Text.Json.Serialization;

namespace FinanceApp.API.Models;

public sealed class SystemProcessRunListQuery
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
    public string? Statuses { get; init; }
    public string? ProcessType { get; init; }
    public string? Trigger { get; init; }
    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc { get; init; }
    public string? Search { get; init; }
    public bool ActiveOnly { get; init; }
}

public sealed class SystemProcessRunListResponse
{
    public required int Page { get; init; }
    public required int PageSize { get; init; }
    public required int TotalCount { get; init; }
    public required DateTime ServerNowUtc { get; init; }
    public required IReadOnlyList<SystemProcessRunListItemDto> Items { get; init; }
}

public class SystemProcessRunListItemDto
{
    public long Id { get; init; }
    public string ProcessType { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SystemProcessRunStatus Status { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SystemProcessTrigger Trigger { get; init; }
    public DateTime QueuedAtUtc { get; init; }
    public DateTime? StartedAtUtc { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
    public string? CorrelationId { get; init; }
    public int? TotalItems { get; init; }
    public int ProcessedItems { get; init; }
    public int SucceededItems { get; init; }
    public int FailedItems { get; init; }
    public int SkippedItems { get; init; }
    public string? ResultSummary { get; init; }
    public string? ErrorSummary { get; init; }
    public double? DurationSeconds { get; init; }
    public double? ProgressPercent { get; init; }
}

public sealed class SystemProcessRunDetailsDto : SystemProcessRunListItemDto
{
    public string? ExternalRunKey { get; init; }
    public string? InitiatedByUserId { get; init; }
    public string? LastProcessedEntity { get; init; }
    public string? DetailsJson { get; init; }
}

public sealed class SystemProcessRunSummaryDto
{
    public DateTime GeneratedAtUtc { get; init; }
    public int ActiveCount { get; init; }
    public int FailedLast24Hours { get; init; }
    public int CompletedLast24Hours { get; init; }
    public int TotalLast24Hours { get; init; }
}
