using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.AnalysisServices.Tabular;
using TabularOps.Core.Connection;
using TabularOps.Core.Dmv;
using TabularOps.Core.Model;
using TabularOps.Core.Refresh;

namespace TabularOps.Desktop.ViewModels;

public partial class OverviewViewModel : ObservableObject
{
    private readonly ConnectionManager _connectionManager;
    private readonly TomRefreshEngine _refreshEngine;
    private ModelRef? _currentModel;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isProcessing;
    [ObservableProperty] private string _modelName = "—";
    [ObservableProperty] private string _endpointType = "—";
    [ObservableProperty] private string _compatibilityLevel = "—";
    [ObservableProperty] private string _totalSize = "—";
    [ObservableProperty] private string _totalRows = "—";
    [ObservableProperty] private string _tableCount = "—";
    [ObservableProperty] private string _partitionCount = "—";
    [ObservableProperty] private string _lastProcessed = "—";
    [ObservableProperty] private string _lastBackup = "—";
    [ObservableProperty] private string _memoryUsage = "—";
    [ObservableProperty] private string? _capacitySku;
    [ObservableProperty] private string? _capacityName;
    [ObservableProperty] private string? _capacityRegion;
    [ObservableProperty] private string? _processStatus;
    [ObservableProperty] private string? _errorMessage;

    /// <summary>True when this is a Power BI workspace on a named capacity.</summary>
    public bool HasCapacityInfo => CapacitySku is not null;

    public OverviewViewModel(ConnectionManager connectionManager, TomRefreshEngine refreshEngine)
    {
        _connectionManager = connectionManager;
        _refreshEngine = refreshEngine;
    }

    public async Task LoadAsync(ModelRef model, CancellationToken ct = default)
    {
        _currentModel = model;
        IsLoading = true;
        ErrorMessage = null;
        ProcessStatus = null;

        try
        {
            // ── TOM: table/partition counts + last processed ──────────────────
            var server = await _connectionManager.GetOrCreateCatalogServerAsync(
                model.TenantId, model.DatabaseName, ct);

            var db = server.Databases
                .Cast<Microsoft.AnalysisServices.Database>()
                .FirstOrDefault(d => d.Name == model.DatabaseName);

            if (db is not null)
            {
                ModelName = db.Name;
                var tables = db.Model.Tables.Cast<Table>()
                    .Where(t => !t.IsHidden && t.CalculationGroup is null)
                    .ToList();

                TableCount     = tables.Count.ToString();
                PartitionCount = tables.Sum(t => t.Partitions.Count).ToString();
                LastProcessed  = db.LastProcessed != DateTime.MinValue
                    ? db.LastProcessed.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                    : "never";
            }

            CompatibilityLevel = $"CL {model.CompatibilityLevel}";
            EndpointType = model.EndpointType switch
            {
                Core.Model.EndpointType.PowerBi => "Power BI",
                Core.Model.EndpointType.Aas     => "Azure AS",
                Core.Model.EndpointType.Ssas    => "SQL Server AS",
                _                               => "—"
            };

            // ── Capacity SKU (Power BI only) ──────────────────────────────────
            var ctx = _connectionManager.Tenants.FirstOrDefault(t => t.TenantId == model.TenantId);
            CapacitySku    = ctx?.CapacitySku;
            CapacityName   = ctx?.CapacityName;
            CapacityRegion = ctx?.CapacityRegion;
            OnPropertyChanged(nameof(HasCapacityInfo));

            // ── DMV: compressed storage size + total row count ────────────────
            try
            {
                var storage = await DmvQueries.GetPartitionStorageAsync(
                    _connectionManager, model.TenantId, model.DatabaseName, ct);
                TotalSize = FormatBytes(storage.Values.Sum(v => v.SizeBytes));
                TotalRows = FormatCount(storage.Values.Sum(v => v.RowCount));
            }
            catch { /* stats unavailable on this endpoint */ }

            // ── DMV: memory usage (SSAS/AAS only) ────────────────────────────
            try
            {
                var memBytes = await DmvQueries.GetDatabaseMemoryUsageAsync(
                    _connectionManager, model.TenantId, model.DatabaseName, ct);
                MemoryUsage = memBytes.HasValue ? FormatBytes(memBytes.Value) : "—";
            }
            catch { MemoryUsage = "—"; }

            // ── Last backup: tracked in SQLite once backup is implemented ─────
            // TODO: query backup history store when implemented
            LastBackup = "—";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ── Process whole model ───────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanProcess))]
    private async Task ProcessModelWithMode(RefreshTypeOption option)
    {
        if (_currentModel is null) return;

        IsProcessing = true;
        ProcessStatus = $"Processing model ({option.DisplayName})…";

        try
        {
            await _refreshEngine.RefreshModelAsync(
                _currentModel.TenantId,
                _currentModel.DatabaseName,
                option.Mode);

            ProcessStatus = $"Process completed ({option.DisplayName})";
            await LoadAsync(_currentModel);
        }
        catch (OperationCanceledException)
        {
            ProcessStatus = "Cancelled";
        }
        catch (Exception ex)
        {
            ProcessStatus = $"Failed: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    public bool CanProcess => _currentModel is not null && !IsProcessing && !IsLoading;

    partial void OnIsProcessingChanged(bool value)
    {
        ProcessModelWithModeCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanProcess));
    }

    partial void OnIsLoadingChanged(bool value)
    {
        ProcessModelWithModeCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanProcess));
    }

    // ── Backup ────────────────────────────────────────────────────────────────

    [RelayCommand]
    private void BackupModel()
    {
        // TODO Milestone 7+: open SaveFileDialog, call TOM Database.Backup(), record date in SQLite
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string FormatBytes(long b) => b switch
    {
        0                => "—",
        >= 1_073_741_824 => $"{b / 1_073_741_824.0:F1} GB",
        >= 1_048_576     => $"{b / 1_048_576.0:F1} MB",
        >= 1_024         => $"{b / 1_024.0:F1} KB",
        _                => $"{b} B"
    };

    private static string FormatCount(long n) => n switch
    {
        >= 1_000_000_000 => $"{n / 1_000_000_000.0:F1}B rows",
        >= 1_000_000     => $"{n / 1_000_000.0:F1}M rows",
        >= 1_000         => $"{n / 1_000.0:F1}K rows",
        0                => "0 rows",
        _                => $"{n} rows"
    };
}
