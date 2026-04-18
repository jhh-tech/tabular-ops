namespace TabularOps.Core.Refresh;

public enum RefreshStatus { Running, Completed, Failed, Cancelled }

/// <summary>
/// Immutable snapshot of a single partition refresh attempt.
/// Source = "App" for refreshes triggered from this tool; "Workspace" for those
/// imported from the Power BI service refresh history.
/// </summary>
public sealed record RefreshRun(
    long Id,
    string TenantId,
    string DatabaseName,
    string TableName,
    string PartitionName,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    RefreshStatus Status,
    string? ErrorMessage,
    string Source = "App",
    string RefreshType = "")
{
    public TimeSpan? Duration => CompletedAt.HasValue ? CompletedAt.Value - StartedAt : null;
}
