using System.Windows.Threading;
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
    private readonly BackupService _backupService;
    private readonly BackupStore _backupStore;
    private ModelRef? _currentModel;

    private DispatcherTimer? _progressTimer;
    private DateTimeOffset _operationStarted;
    private string _operationBaseMessage = "";

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isProcessing;
    [ObservableProperty] private bool _isBackingUp;
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
    [ObservableProperty] private bool _isProcessPopupOpen;

    /// <summary>True when this is a Power BI workspace on a named capacity.</summary>
    public bool HasCapacityInfo => CapacitySku is not null;

    public bool IsBusy     => IsProcessing || IsBackingUp;
    public bool CanProcess => _currentModel is not null && !IsProcessing && !IsLoading && !IsBackingUp;
    public bool CanBackup  => _currentModel is not null && !IsProcessing && !IsLoading && !IsBackingUp;

    public OverviewViewModel(
        ConnectionManager connectionManager,
        TomRefreshEngine refreshEngine,
        BackupService backupService,
        BackupStore backupStore)
    {
        _connectionManager = connectionManager;
        _refreshEngine     = refreshEngine;
        _backupService     = backupService;
        _backupStore       = backupStore;
    }

    public async Task LoadAsync(ModelRef model, CancellationToken ct = default)
    {
        _currentModel = model;
        IsLoading = true;
        ErrorMessage = null;
        ProcessStatus = null;
        NotifyCanExecuteChanged();

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

            // ── SQLite: last successful backup ────────────────────────────────
            var lastRun = await _backupStore.GetLastBackupAsync(model.TenantId, model.DatabaseName, ct);
            LastBackup = lastRun?.CompletedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";

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
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
            NotifyCanExecuteChanged();
        }
    }

    // ── Process whole model ───────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanProcess))]
    private async Task ProcessModelWithMode(RefreshTypeOption option)
    {
        if (_currentModel is null || option is null) return;

        IsProcessPopupOpen = false;   // dismiss dropdown immediately
        IsProcessing = true;
        StartProgressTimer($"Processing ({option.DisplayName})");

        try
        {
            await _refreshEngine.RefreshModelAsync(
                _currentModel.TenantId,
                _currentModel.DatabaseName,
                option.Mode);

            var elapsed = FormatElapsed(DateTimeOffset.UtcNow - _operationStarted);
            ProcessStatus = $"Process completed ({option.DisplayName}) — {elapsed}";
            await LoadAsync(_currentModel);
        }
        catch (OperationCanceledException)
        {
            ProcessStatus = "Cancelled";
        }
        catch (Exception ex)
        {
            ProcessStatus = $"Failed: {TrimXmlaError(ex.Message)}";
        }
        finally
        {
            StopProgressTimer();
            IsProcessing = false;
            NotifyCanExecuteChanged();
        }
    }

    // ── Backup ────────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanBackup))]
    private async Task BackupModel()
    {
        if (_currentModel is null) return;

        IsBackingUp = true;
        StartProgressTimer("Backing up");

        try
        {
            var run = await _backupService.BackupAsync(
                _currentModel.TenantId,
                _currentModel.DatabaseName);

            var elapsed = FormatElapsed(DateTimeOffset.UtcNow - _operationStarted);
            ProcessStatus = $"Backup complete — {run.FileName} ({elapsed})";
            LastBackup = run.CompletedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";
        }
        catch (Exception ex)
        {
            // Common cause: backup storage not configured on the server/workspace.
            // XMLA errors append " Technical Details: RootActivityId: <guid> Date (UTC): ..."
            // which is noise — strip it and show only the human-readable sentence.
            ProcessStatus = $"Backup failed: {TrimXmlaError(ex.Message)}";
        }
        finally
        {
            StopProgressTimer();
            IsBackingUp = false;
            NotifyCanExecuteChanged();
        }
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────

    private void NotifyCanExecuteChanged()
    {
        ProcessModelWithModeCommand.NotifyCanExecuteChanged();
        BackupModelCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanProcess));
        OnPropertyChanged(nameof(CanBackup));
        OnPropertyChanged(nameof(IsBusy));
    }

    partial void OnIsProcessingChanged(bool value) => NotifyCanExecuteChanged();
    partial void OnIsLoadingChanged(bool value)    => NotifyCanExecuteChanged();
    partial void OnIsBackingUpChanged(bool value)  => NotifyCanExecuteChanged();

    private void StartProgressTimer(string baseMessage)
    {
        _operationBaseMessage = baseMessage;
        _operationStarted     = DateTimeOffset.UtcNow;
        ProcessStatus         = $"{baseMessage}…";

        _progressTimer?.Stop();
        _progressTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            OnProgressTimerTick,
            System.Windows.Application.Current.Dispatcher);
    }

    private void OnProgressTimerTick(object? sender, EventArgs e)
    {
        var elapsed = DateTimeOffset.UtcNow - _operationStarted;
        ProcessStatus = $"{_operationBaseMessage}… {FormatElapsed(elapsed)}";
    }

    private void StopProgressTimer()
    {
        _progressTimer?.Stop();
        _progressTimer = null;
    }

    private static string FormatElapsed(TimeSpan t) =>
        t.TotalMinutes >= 1
            ? $"{(int)t.TotalMinutes}m {t.Seconds}s"
            : $"{(int)t.TotalSeconds}s";

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Power BI / AAS XMLA errors append " Technical Details: RootActivityId: &lt;guid&gt;
    /// Date (UTC): ..." after the human-readable message. Strip everything from
    /// "Technical Details:" onward so the status bar stays readable.
    /// </summary>
    private static string TrimXmlaError(string message)
    {
        var cut = message.IndexOf("Technical Details:", StringComparison.OrdinalIgnoreCase);
        return cut > 0 ? message[..cut].Trim().TrimEnd('.', ',', ' ') : message;
    }

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
