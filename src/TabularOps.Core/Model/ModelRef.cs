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
    int CompatibilityLevel
);
