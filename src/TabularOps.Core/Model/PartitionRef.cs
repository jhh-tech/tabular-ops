namespace TabularOps.Core.Model;

public enum PartitionState
{
    Ok,
    Stale,
    Queued,       // triggered but waiting on MaxParallelism slot
    Refreshing,   // server has begun processing (ProgressReportBegin received)
    Failed,
}

/// <summary>
/// Lightweight snapshot of a partition's identity and last-known state.
/// Populated from TOM and DMV data; does not hold a live connection.
/// </summary>
public sealed record PartitionRef(
    string TableName,
    string PartitionName,
    PartitionState State,
    DateTimeOffset? LastRefreshed,
    long? RowCount,
    long? SizeBytes,
    string? LastError
);
