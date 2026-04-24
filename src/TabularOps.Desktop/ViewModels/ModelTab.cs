using CommunityToolkit.Mvvm.ComponentModel;
using TabularOps.Core.Model;

namespace TabularOps.Desktop.ViewModels;

/// <summary>
/// Represents one open model tab in the topbar tab strip.
/// Each tab owns its own OverviewViewModel and PartitionMapViewModel so their
/// loaded state is preserved independently — switching tabs is instant after
/// the first load.
/// </summary>
public sealed partial class ModelTab : ObservableObject
{
    [ObservableProperty] private bool _isActive;

    public ModelNodeViewModel    Node         { get; }
    public ModelRef              Model        => Node.Model;
    public OverviewViewModel     Overview     { get; }
    public PartitionMapViewModel PartitionMap { get; }

    /// <summary>Short label displayed on the tab — the database name.</summary>
    public string DisplayName   => Model.DatabaseName;

    /// <summary>Shown as tooltip — the workspace name.</summary>
    public string WorkspaceName => Model.WorkspaceName;

    public ModelTab(ModelNodeViewModel node, OverviewViewModel overview, PartitionMapViewModel partitionMap)
    {
        Node         = node;
        Overview     = overview;
        PartitionMap = partitionMap;
    }

    /// <summary>True when this tab represents the given model.</summary>
    public bool Matches(string tenantId, string databaseId) =>
        Model.TenantId == tenantId && Model.DatabaseId == databaseId;
}
