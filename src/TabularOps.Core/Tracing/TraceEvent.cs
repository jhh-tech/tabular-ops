namespace TabularOps.Core.Tracing;

/// <summary>
/// A single trace event captured from a tabular model via XMLA.
/// </summary>
public sealed record TraceEvent
{
    public long Id { get; init; }

    public DateTimeOffset Time { get; init; }

    /// <summary>
    /// Profiler-style event class (e.g. "Progress", "Query Begin", "Error").
    /// </summary>
    public string EventClass { get; init; } = string.Empty;

    /// <summary>
    /// Subclassification within the event class (e.g. "VertiPaq Query" within Progress).
    /// </summary>
    public string? EventSubclass { get; init; }

    /// <summary>
    /// Text of the operation, query, or error message.
    /// </summary>
    public string? Text { get; init; }

    /// <summary>
    /// Partition being processed, if applicable.
    /// </summary>
    public string? PartitionName { get; init; }

    /// <summary>
    /// Table being processed, if applicable.
    /// </summary>
    public string? TableName { get; init; }

    /// <summary>
    /// Duration in milliseconds, if reported by the server.
    /// </summary>
    public long? DurationMs { get; init; }

    /// <summary>
    /// CPU time in milliseconds, if reported.
    /// </summary>
    public long? CpuMs { get; init; }

    /// <summary>
    /// Rows processed, if reported (e.g. VertiPaq rows).
    /// </summary>
    public long? RowCount { get; init; }

    /// <summary>
    /// Error code, if this is an error event.
    /// </summary>
    public int? ErrorCode { get; init; }

    /// <summary>
    /// The database / catalog this event belongs to.
    /// </summary>
    public string? DatabaseName { get; init; }

    /// <summary>
    /// The session ID that generated this event (for cancellation correlation).
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Human-readable form of EventClass + EventSubclass.
    /// </summary>
    public string Summary => string.IsNullOrEmpty(EventSubclass)
        ? EventClass
        : $"{EventClass} / {EventSubclass}";
}
