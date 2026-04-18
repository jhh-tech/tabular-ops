namespace TabularOps.Core.Model;

/// <summary>Lightweight workspace reference returned by the Power BI REST API.</summary>
public sealed record WorkspaceInfo(
    string Id,
    string Name,
    string? CapacityId = null,
    string? CapacityName = null,
    string? CapacityRegion = null,
    string? CapacitySku = null
);
