namespace FinanceApp.Core.Models;

public enum SystemProcessRunStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    CompletedWithErrors = 3,
    Failed = 4,
    Cancelled = 5,
    Interrupted = 6,
    Deferred = 7,
}

public enum SystemProcessTrigger
{
    Scheduled = 0,
    StartupCatchUp = 1,
    Automatic = 2,
    Manual = 3,
    ApiRepair = 4,
    SystemRecovery = 5,
}

public sealed class SystemProcessRun
{
    public long Id { get; set; }
    public string ProcessType { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public SystemProcessRunStatus Status { get; set; } = SystemProcessRunStatus.Pending;
    public SystemProcessTrigger Trigger { get; set; } = SystemProcessTrigger.Automatic;
    public string? InitiatedByUserId { get; set; }
    public string? CorrelationId { get; set; }
    public string? ExternalRunKey { get; set; }
    public string InstanceId { get; set; } = string.Empty;
    public DateTime QueuedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? HeartbeatAtUtc { get; set; }
    public int? TotalItems { get; set; }
    public int ProcessedItems { get; set; }
    public int SucceededItems { get; set; }
    public int FailedItems { get; set; }
    public int SkippedItems { get; set; }
    public string? ResultSummary { get; set; }
    public string? ErrorSummary { get; set; }
    public string? LastProcessedEntity { get; set; }
    public string? DetailsJson { get; set; }
}
