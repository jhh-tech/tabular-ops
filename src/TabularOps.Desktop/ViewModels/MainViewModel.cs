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
    public StatusBarViewModel StatusBar { get; } = new();
    public PartitionMapViewModel PartitionMap { get; }

    [ObservableProperty] private TenantNodeViewModel? _activeTenant;
    [ObservableProperty] private ModelNodeViewModel? _activeModel;
    [ObservableProperty] private bool _isRestoring;

    public MainViewModel(ConnectionManager connectionManager, ConnectionStore connectionStore)
    {
        _connectionManager = connectionManager;
        _connectionStore = connectionStore;
        PartitionMap = new PartitionMapViewModel(connectionManager);
    }

    partial void OnActiveTenantChanged(TenantNodeViewModel? value)
    {
        StatusBar.UpdateFromContext(value?.Context);
    }

    partial void OnActiveModelChanged(ModelNodeViewModel? value)
    {
        if (ActiveTenant is null || value is null) return;
        ActiveTenant.Context.ActiveModel = value.Model;
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
                    saved.ConnectionString, saved.DisplayName, ct)
                : await _connectionManager.AddSsasTenantAsync(
                    saved.ConnectionString, saved.DisplayName, ct);

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
        if (ActiveModel is not null)
            ActiveModel.IsActive = false;

        ActiveModel = model;
        model.IsActive = true;

        var ownerTenant = Tenants.FirstOrDefault(t => t.Models.Contains(model));
        if (ownerTenant is not null && ownerTenant != ActiveTenant)
        {
            ActiveTenant = ownerTenant;
            await _connectionManager.SetActiveTenantAsync(ownerTenant.TenantId);
        }

        _ = PartitionMap.LoadAsync(model.Model);
    }

    [RelayCommand]
    private async Task RemoveTenant(TenantNodeViewModel tenant)
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
        await SaveConnectionsAsync();
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
