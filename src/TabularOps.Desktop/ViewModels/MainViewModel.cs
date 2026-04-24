using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Windows;
using TabularOps.Core.Connection;
using TabularOps.Core.Model;

namespace TabularOps.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ConnectionManager _connectionManager;
    private readonly ConnectionStore _connectionStore;

    public ObservableCollection<TenantNodeViewModel> Tenants { get; } = [];
    public ObservableCollection<ModelTab> ModelTabs { get; } = [];
    public StatusBarViewModel StatusBar { get; } = new();
    public HistoryViewModel History { get; }
    public TraceViewModel Trace { get; }

    /// <summary>Overview VM for the currently active tab (null when no tab is open).</summary>
    public OverviewViewModel?    ActiveOverview     => ActiveModelTab?.Overview;
    /// <summary>Partition map VM for the currently active tab (null when no tab is open).</summary>
    public PartitionMapViewModel? ActivePartitionMap => ActiveModelTab?.PartitionMap;

    [ObservableProperty] private TenantNodeViewModel? _activeTenant;
    [ObservableProperty] private ModelNodeViewModel? _activeModel;
    [ObservableProperty] private ModelTab? _activeModelTab;
    [ObservableProperty] private bool _isRestoring;
    [ObservableProperty] private string _activeTab = "Partitions";

    public bool IsOverviewTab   => ActiveTab == "Overview";
    public bool IsPartitionsTab => ActiveTab == "Partitions";
    public bool IsTraceTab      => ActiveTab == "Trace";
    public bool IsLineageTab    => ActiveTab == "Lineage";
    public bool IsHistoryTab    => ActiveTab == "History";

    /// <summary>
    /// UPN of the connected Entra account (e.g. user@contoso.com).
    /// Derived from the first workspace that has a resolved UPN.
    /// </summary>
    public string? EntraAccount => Tenants
        .Select(t => t.UserPrincipalName)
        .FirstOrDefault(u => u is not null);

    public bool HasPowerBiTenants => Tenants.Any(t => t.Context.EndpointType == EndpointType.PowerBi);

    public int TotalModelCount => Tenants.Sum(t => t.Models.Count);

    public string ContextBreadcrumb => ActiveModel is null
        ? ActiveTenant?.DisplayName ?? ""
        : ActiveTenant?.DisplayName == ActiveModel.WorkspaceName
            ? $"{ActiveModel.WorkspaceName} / {ActiveModel.DisplayName}"
            : $"{ActiveTenant?.DisplayName} / {ActiveModel.WorkspaceName} / {ActiveModel.DisplayName}";

    public int PartitionCount => ActiveModelTab?.PartitionMap.VisibleTables.Sum(t => t.Partitions.Count) ?? 0;

    public int HistoryCount => History.Runs.Count;

    public bool IsTraceRunning => Trace.IsRunning;

    // ── Active model context bar ──────────────────────────────────────────────

    public bool HasActiveModel => ActiveModel is not null;

    public string? ActiveEndpointLabel => ActiveModel?.Model.EndpointType switch
    {
        EndpointType.PowerBi => "Power BI",
        EndpointType.Aas     => "Azure AS",
        EndpointType.Ssas    => "SQL Server AS",
        _ => null,
    };

    public string? ActiveCompatibilityLabel =>
        ActiveModel is null ? null : $"CL {ActiveModel.Model.CompatibilityLevel}";

    public int ActiveWorkspaceModelCount =>
        ActiveModel is null
            ? 0
            : Tenants.FirstOrDefault(t => t.Models.Contains(ActiveModel))?.Models.Count ?? 0;

    // Capacity info — populated for Power BI workspaces on dedicated capacity
    public string? ActiveCapacityName   => ActiveTenant?.Context.CapacityName;
    public string? ActiveCapacitySku    => ActiveTenant?.Context.CapacitySku;
    public string? ActiveCapacityRegion => ActiveTenant?.Context.CapacityRegion;

    public bool HasCapacityInfo =>
        ActiveModel?.Model.EndpointType == EndpointType.PowerBi
        && ActiveCapacitySku is not null;

    private void RaiseActiveContextChanged()
    {
        OnPropertyChanged(nameof(HasActiveModel));
        OnPropertyChanged(nameof(ActiveEndpointLabel));
        OnPropertyChanged(nameof(ActiveCompatibilityLabel));
        OnPropertyChanged(nameof(ActiveWorkspaceModelCount));
        OnPropertyChanged(nameof(ActiveCapacityName));
        OnPropertyChanged(nameof(ActiveCapacitySku));
        OnPropertyChanged(nameof(ActiveCapacityRegion));
        OnPropertyChanged(nameof(HasCapacityInfo));
        OnPropertyChanged(nameof(EntraAccount));
        OnPropertyChanged(nameof(ContextBreadcrumb));
        OnPropertyChanged(nameof(PartitionCount));
        OnPropertyChanged(nameof(HistoryCount));
        OnPropertyChanged(nameof(IsTraceRunning));
        OnPropertyChanged(nameof(IsOverviewTab));
        OnPropertyChanged(nameof(IsLineageTab));
        OnPropertyChanged(nameof(TotalModelCount));
        OnPropertyChanged(nameof(HasPowerBiTenants));
        OnPropertyChanged(nameof(ActiveOverview));
        OnPropertyChanged(nameof(ActivePartitionMap));
    }

    public MainViewModel(ConnectionManager connectionManager, ConnectionStore connectionStore)
    {
        _connectionManager = connectionManager;
        _connectionStore   = connectionStore;
        History = new HistoryViewModel(App.RefreshHistory, connectionManager);
        Trace   = new TraceViewModel(connectionManager);
    }

    /// <summary>
    /// Creates a new ModelTab with its own dedicated Overview and PartitionMap VMs.
    /// Each tab owns its VMs so loaded state survives switching away and back.
    /// </summary>
    private ModelTab CreateTab(ModelNodeViewModel node) => new(
        node,
        new OverviewViewModel(_connectionManager, App.RefreshEngine, App.BackupService, App.BackupStore),
        new PartitionMapViewModel(_connectionManager, App.RefreshEngine, App.PartitionCache));

    [RelayCommand]
    private async Task SwitchTab(string tab)
    {
        ActiveTab = tab;
        OnPropertyChanged(nameof(IsOverviewTab));
        OnPropertyChanged(nameof(IsPartitionsTab));
        OnPropertyChanged(nameof(IsTraceTab));
        OnPropertyChanged(nameof(IsLineageTab));
        OnPropertyChanged(nameof(IsHistoryTab));

        // Overview data is owned per-tab and was already loaded when the tab was
        // first activated — no reload needed when clicking the Overview tab button.
        if (tab == "History")
        {
            UpdateHistoryContext();
            await History.RefreshAsync();
        }
        else if (tab == "Trace")
        {
            if (ActiveModel is not null)
                Trace.SetContext(ActiveModel.Model.TenantId, ActiveModel.Model.DatabaseName);
            else if (ActiveTenant is not null)
                Trace.SetContext(ActiveTenant.TenantId, null);

            if (!Trace.IsRunning)
                await Trace.StartTraceAsync();
        }
        // Trace intentionally keeps running when switching to other tabs —
        // the user may navigate to Overview or Partitions while monitoring.
        // Trace is stopped only when the active model changes (see OnActiveModelChanged).
    }

    partial void OnActiveModelTabChanged(ModelTab? value)
    {
        // Keep IsActive flags in sync when the tab is changed directly (e.g. closed via null)
        foreach (var t in ModelTabs) t.IsActive = t == value;
        // The active tab's VMs replace what the content area binds to
        OnPropertyChanged(nameof(ActiveOverview));
        OnPropertyChanged(nameof(ActivePartitionMap));
        OnPropertyChanged(nameof(PartitionCount));
    }

    partial void OnActiveTenantChanged(TenantNodeViewModel? value)
    {
        StatusBar.UpdateFromContext(value?.Context);
        RaiseActiveContextChanged();
    }

    partial void OnActiveModelChanged(ModelNodeViewModel? value)
    {
        RaiseActiveContextChanged();

        // Stop the trace when the user switches to a different model — the old
        // subscription is for the previous model's server context.
        if (Trace.IsRunning)
            _ = Trace.StopTraceAsync();

        if (ActiveTenant is null || value is null) return;
        ActiveTenant.Context.ActiveModel = value.Model;

        if (ActiveTab == "History")
        {
            UpdateHistoryContext();
            _ = History.RefreshAsync();
        }
    }

    /// <summary>
    /// Passes the right filter context to HistoryViewModel:
    /// workspace-level when no model is selected, model-level otherwise.
    /// </summary>
    private void UpdateHistoryContext()
    {
        if (ActiveModel is not null)
        {
            History.SetContext(
                ActiveModel.Model.TenantId,
                ActiveModel.Model.WorkspaceName,
                ActiveModel.Model.DatabaseId,
                ActiveModel.Model.DatabaseName,
                ActiveModel.Model.EndpointType);
        }
        else
        {
            History.SetContext(
                ActiveTenant?.TenantId,
                ActiveTenant?.Context.DisplayName);
        }
    }

    /// <summary>
    /// Called on startup. Restores saved connections and begins loading models
    /// for each. Re-authentication happens silently from the MSAL cache; if the
    /// token has expired the sidebar shows an error dot until the user reconnects.
    /// </summary>
    public async Task RestoreConnectionsAsync(CancellationToken ct = default)
    {
        var saved = await _connectionStore.LoadAsync(ct);
        if (saved.Count == 0) return;

        // Create all sidebar nodes immediately so the user sees them right away
        foreach (var context in saved)
            RegisterTenant(context);

        // Connect all tenants concurrently; show a loading indicator until done
        IsRestoring = true;
        try
        {
            await Task.WhenAll(saved.Select(c => RestoreConnectionAsync(c, ct)));
        }
        finally
        {
            IsRestoring = false;
        }
    }

    /// <summary>
    /// Registers a saved connection with ConnectionManager (acquires token silently,
    /// opens TOM/ADOMD connections) and then enumerates models.
    /// </summary>
    private async Task RestoreConnectionAsync(TenantContext saved, CancellationToken ct)
    {
        var tenant = Tenants.FirstOrDefault(t => t.TenantId == saved.TenantId);
        if (tenant is null) return;

        try
        {
            // This registers the tenant in ConnectionManager._tenants and connects.
            // For Power BI it acquires the token silently from the MSAL cache on disk.
            var registered = saved.EndpointType == EndpointType.PowerBi
                ? await _connectionManager.AddPowerBiTenantAsync(
                    saved.ConnectionString, saved.DisplayName, ct: ct)
                : await _connectionManager.AddSsasTenantAsync(
                    saved.ConnectionString, saved.DisplayName, ct);

            // Restore capacity info that was persisted by ConnectionStore
            if (saved.CapacityName is not null)
            {
                registered.CapacityName   = saved.CapacityName;
                registered.CapacityRegion = saved.CapacityRegion;
                registered.CapacitySku    = saved.CapacitySku;
            }

            // Propagate UPN so the sidebar can show which Entra account owns this workspace
            if (registered.UserPrincipalName is not null)
            {
                var node = Tenants.FirstOrDefault(t => t.TenantId == registered.TenantId);
                if (node is not null)
                {
                    node.UserPrincipalName = registered.UserPrincipalName;
                    OnPropertyChanged(nameof(EntraAccount));
                }
            }

            await LoadModelsAsync(registered, ct);
        }
        catch (Exception ex)
        {
            tenant.Status = TenantConnectionStatus.Error;
            tenant.ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task AddConnection()
    {
        var dialog = new Views.AddConnectionDialog { Owner = App.Current.MainWindow };
        if (dialog.ShowDialog() == true && dialog.ResultContexts is not null)
        {
            foreach (var context in dialog.ResultContexts)
            {
                RegisterTenant(context);

                if (context.UserPrincipalName is not null)
                {
                    var node = Tenants.FirstOrDefault(t => t.TenantId == context.TenantId);
                    if (node is not null)
                    {
                        node.UserPrincipalName = context.UserPrincipalName;
                        OnPropertyChanged(nameof(EntraAccount));
                    }
                }

                _ = LoadModelsAsync(context);
            }

            await SaveConnectionsAsync();
        }
    }

    private async Task LoadModelsAsync(TenantContext context, CancellationToken ct = default)
    {
        var tenant = Tenants.FirstOrDefault(t => t.TenantId == context.TenantId);
        if (tenant is null) return;

        tenant.Status = TenantConnectionStatus.Connecting;
        try
        {
            var server = await _connectionManager.GetTomServerAsync(context.TenantId, ct);
            var models = server.Databases
                .Cast<Microsoft.AnalysisServices.Database>()
                .Where(db => db.CompatibilityLevel >= 1500)
                .Select(db => new ModelRef(
                    TenantId: context.TenantId,
                    WorkspaceId: context.TenantId,
                    WorkspaceName: context.DisplayName,
                    DatabaseId: db.ID,
                    DatabaseName: db.Name,
                    EndpointType: context.EndpointType,
                    CompatibilityLevel: db.CompatibilityLevel))
                .ToList();

            PopulateModels(context.TenantId, models);
            await _connectionManager.SetActiveTenantAsync(context.TenantId, ct);
            ActiveTenant ??= Tenants.FirstOrDefault(t => t.TenantId == context.TenantId);
        }
        catch (Exception ex)
        {
            tenant.Status = TenantConnectionStatus.Error;
            tenant.ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task SelectModelAsync(ModelNodeViewModel model)
    {
        var tab = ModelTabs.FirstOrDefault(t => t.Node == model);
        if (tab is null)
        {
            tab = CreateTab(model);
            ModelTabs.Add(tab);
        }
        await ActivateModelTabAsync(tab);
    }

    /// <summary>
    /// Core tab-activation path. Updates sidebar IsActive flags, sets ActiveModel/ActiveTenant,
    /// and kicks off Overview + PartitionMap loads.
    /// </summary>
    private async Task ActivateModelTabAsync(ModelTab tab)
    {
        // Sync tab highlight flags
        foreach (var t in ModelTabs) t.IsActive = false;
        tab.IsActive = true;
        ActiveModelTab = tab;

        var node = tab.Node;

        // Update sidebar selection
        if (ActiveModel is not null && ActiveModel != node)
            ActiveModel.IsActive = false;
        ActiveModel = node;
        node.IsActive = true;

        // Switch tenant context when the model belongs to a different workspace
        var ownerTenant = Tenants.FirstOrDefault(t => t.Models.Contains(node));
        if (ownerTenant is not null && ownerTenant != ActiveTenant)
        {
            ActiveTenant = ownerTenant;
            await _connectionManager.SetActiveTenantAsync(ownerTenant.TenantId);
        }

        if (ActiveTenant is not null)
            ActiveTenant.Context.ActiveModel = node.Model;

        ActiveTab = "Overview";
        OnPropertyChanged(nameof(IsOverviewTab));
        OnPropertyChanged(nameof(IsPartitionsTab));
        OnPropertyChanged(nameof(IsTraceTab));
        OnPropertyChanged(nameof(IsLineageTab));
        OnPropertyChanged(nameof(IsHistoryTab));
        // Each tab owns its VMs — first activation loads data; subsequent switches
        // hit the identity guard and return immediately (no network round-trip).
        _ = tab.Overview.LoadAsync(node.Model);
        _ = tab.PartitionMap.LoadAsync(node.Model);
    }

    [RelayCommand]
    private void SwitchModelTab(ModelTab tab)
    {
        if (tab == ActiveModelTab) return;
        _ = ActivateModelTabAsync(tab);
    }

    [RelayCommand]
    private void CloseModelTab(ModelTab tab)
    {
        var idx = ModelTabs.IndexOf(tab);
        ModelTabs.Remove(tab);

        if (tab != ActiveModelTab) return;

        if (ModelTabs.Count == 0)
        {
            if (ActiveModel is not null) ActiveModel.IsActive = false;
            ActiveModel    = null;
            ActiveModelTab = null;
        }
        else
        {
            var newIdx = Math.Max(0, Math.Min(idx, ModelTabs.Count - 1));
            _ = ActivateModelTabAsync(ModelTabs[newIdx]);
        }
    }

    [RelayCommand]
    private async Task RemoveTenant(TenantNodeViewModel tenant)
    {
        // Close any open tabs for models belonging to this tenant
        var orphaned = ModelTabs.Where(t => tenant.Models.Contains(t.Node)).ToList();
        bool activeTabRemoved = ActiveModelTab is not null && orphaned.Contains(ActiveModelTab);
        foreach (var t in orphaned)
            ModelTabs.Remove(t);

        Tenants.Remove(tenant);

        if (ActiveTenant == tenant)
        {
            ActiveTenant = Tenants.FirstOrDefault();
            if (ActiveModel is not null) ActiveModel.IsActive = false;
            ActiveModel    = null;
            ActiveModelTab = null;
        }
        else if (ActiveModel is not null && tenant.Models.Contains(ActiveModel))
        {
            if (ActiveModel is not null) ActiveModel.IsActive = false;
            ActiveModel    = null;
            ActiveModelTab = null;
        }
        else if (activeTabRemoved && ModelTabs.Count > 0)
        {
            _ = ActivateModelTabAsync(ModelTabs[0]);
        }

        // Always refresh derived counts — RaiseActiveContextChanged is only called
        // via OnActiveTenantChanged when the active tenant changed, which doesn't
        // happen when removing a non-active workspace.
        OnPropertyChanged(nameof(TotalModelCount));
        OnPropertyChanged(nameof(HasPowerBiTenants));

        await _connectionManager.RemoveTenantAsync(tenant.TenantId);
        await SaveConnectionsAsync();
    }

    [RelayCommand]
    private async Task SignOutPowerBi()
    {
        var pbiTenants = Tenants
            .Where(t => t.Context.EndpointType == EndpointType.PowerBi)
            .ToList();

        foreach (var tenant in pbiTenants)
        {
            Tenants.Remove(tenant);
            if (ActiveTenant == tenant)
            {
                ActiveTenant = Tenants.FirstOrDefault();
                ActiveModel = null;
            }
            else if (ActiveModel is not null && tenant.Models.Contains(ActiveModel))
            {
                ActiveModel = null;
            }
            await _connectionManager.RemoveTenantAsync(tenant.TenantId);
        }

        await _connectionManager.SignOutPowerBiAsync();
        await SaveConnectionsAsync();
        RaiseActiveContextChanged();
    }

    public void RegisterTenant(TenantContext context)
    {
        if (Tenants.Any(t => t.TenantId == context.TenantId)) return;
        Tenants.Add(new TenantNodeViewModel(context));
    }

    public void PopulateModels(string tenantId, IEnumerable<ModelRef> models)
    {
        var tenant = Tenants.FirstOrDefault(t => t.TenantId == tenantId);
        if (tenant is null) return;

        tenant.Models.Clear();
        foreach (var m in models)
            tenant.Models.Add(new ModelNodeViewModel(m));

        tenant.Status = TenantConnectionStatus.Connected;
    }

    private Task SaveConnectionsAsync() =>
        _connectionStore.SaveAsync(_connectionManager.Tenants);
}
