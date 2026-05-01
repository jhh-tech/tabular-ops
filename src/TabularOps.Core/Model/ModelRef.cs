namespace TabularOps.Core.Model;

/// <summary>
/// Lightweight reference to a deployed tabular model (database on the server).
/// Does not hold a live connection — use ConnectionManager to get one.
/// </summary>
public sealed record ModelRef(
    string TenantId,
    string WorkspaceId,
    string WorkspaceName,
    string DatabaseId,
    string DatabaseName,
    EndpointType EndpointType,
    int CompatibilityLevel,
    /// <summary>
    /// Power BI workspace GUID (e.g. "3e20f6dc-ab12-...").
    /// Required for the Enhanced Refresh REST API. Null for SSAS/AAS.
    /// </summary>
    string? WorkspaceGuid = null
);
