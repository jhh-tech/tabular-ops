using TabularOps.Core.Model;

namespace TabularOps.Core.Connection;

/// <summary>
/// Represents a configured tenant/server connection. Holds identity and state
/// but not the live server objects — those live in ConnectionManager.
/// </summary>
public sealed class TenantContext
{
    /// <summary>Human-readable label shown in the sidebar and topbar.</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// For Power BI: powerbi://api.powerbi.com/v1.0/myorg/&lt;workspace&gt;
    /// For SSAS/AAS: full AMO connection string
    /// </summary>
    public required string ConnectionString { get; init; }

    public required EndpointType EndpointType { get; init; }

    /// <summary>
    /// Absolute path to the per-tenant MSAL token cache file.
    /// Null for SSAS connections that use Windows auth.
    /// </summary>
    public string? TokenCacheFilePath { get; init; }

    /// <summary>
    /// True when the XMLA endpoint is read-only for the authenticated user.
    /// Set after first write attempt fails with insufficient permissions.
    /// Disables refresh/partition action buttons in the UI.
    /// </summary>
    public bool IsReadOnly { get; set; }

    /// <summary>The model the user has currently selected in the sidebar.</summary>
    public ModelRef? ActiveModel { get; set; }

    /// <summary>Unique key used for per-tenant SQLite DB file naming and MSAL cache keying.</summary>
    public string TenantId => EndpointType == EndpointType.PowerBi
        ? DeriveWorkspaceId(ConnectionString)
        : ConnectionString;

    private static string DeriveWorkspaceId(string connectionString)
    {
        // Extract workspace name from powerbi://api.powerbi.com/v1.0/myorg/<workspace>
        var uri = new Uri(connectionString);
        return uri.Segments.LastOrDefault()?.TrimEnd('/') ?? connectionString;
    }
}
