using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using TabularOps.Core.Connection;
using TabularOps.Core.Dmv;
using TabularOps.Core.Model;
using TabularOps.Core.Refresh;

namespace TabularOps.Desktop.ViewModels;

public enum PartitionFilter { All, Stale, Failed, Refreshing }

public sealed record RefreshTypeOption(string DisplayName, RefreshMode Mode)
{
    public override string ToString() => DisplayName;
}

// ─────────────────────────────────────────────────────────────────────────────
// Cell-level view model — one per partition
// ─────────────────────────────────────────────────────────────────────────────

public sealed partial class PartitionCellViewModel : ObservableObject
{
    public string TableName { get; }
    public PartitionState State { get; }
    public string PartitionName { get; }
    public string? LastError { get; }
    public DateTimeOffset? LastRefreshed { get; }
    public double FillRatio { get; }
    public string DisplayRowCount { get; }
    public string DisplaySize { get; }
    public string DisplayLastRefreshed { get; }
    public string ToolTip { get; }

    [ObservableProperty] private bool _isSelected;

    public PartitionCellViewModel(PartitionRef partition, long maxTableSizeBytes, bool isSelected = false)
    {
        TableName = partition.TableName;
        State = partition.State;
        PartitionName = partition.PartitionName;
        LastError = partition.LastError;
        LastRefreshed = partition.LastRefreshed;
        _isSelected = isSelected;

        FillRatio = maxTableSizeBytes > 0
            ? Math.Clamp((double)(partition.SizeBytes ?? 0) / maxTableSizeBytes, 0.04, 1.0)
            : 0.04;

        DisplayRowCount = partition.RowCount.HasValue ? FormatCount(partition.RowCount.Value) : "—";
        DisplaySize = partition.SizeBytes.HasValue ? FormatBytes(partition.SizeBytes.Value) : "—";
        DisplayLastRefreshed = partition.LastRefreshed.HasValue
            ? partition.LastRefreshed.Value.LocalDateTime.ToString("MM-dd HH:mm")
            : "never";
        ToolTip = BuildToolTip(partition);
    }

    private static string BuildToolTip(PartitionRef p)
    {
        var parts = new List<string>
        {
            p.PartitionName,
            $"State:     {p.State}",
            $"Rows:      {(p.RowCount.HasValue ? FormatCount(p.RowCount.Value) : "—")}",
            $"Size:      {(p.SizeBytes.HasValue ? FormatBytes(p.SizeBytes.Value) : "—")}",
            $"Refreshed: {(p.LastRefreshed.HasValue ? p.LastRefreshed.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm") : "Never")}",
        };
        if (!string.IsNullOrEmpty(p.LastError))
            parts.Add($"\nError:\n{p.LastError}");
        return string.Join(Environment.NewLine, parts);
    }

    internal static string FormatCount(long n) => n switch
    {
        >= 1_000_000_000 => $"{n / 1_000_000_000.0:F1}B",
        >= 1_000_000     => $"{n / 1_000_000.0:F1}M",
        >= 1_000         => $"{n / 1_000.0:F1}K",
        _                => n.ToString()
    };

    internal static string FormatBytes(long b) => b switch
    {
        0                => "—",
        >= 1_073_741_824 => $"{b / 1_073_741_824.0:F1} GB",
        >= 1_048_576     => $"{b / 1_048_576.0:F1} MB",
        >= 1_024         => $"{b / 1_024.0:F1} KB",
        _                => $"{b} B"
    };
}

// ─────────────────────────────────────────────────────────────────────────────
// Table-level view model — one per table
// ─────────────────────────────────────────────────────────────────────────────

public sealed class TableViewModel
{
    public string TableName { get; }
    public string PartitionLabel { get; }
    public string DisplayRowCount { get; }
    public string DisplaySize { get; }
    public string DisplayLastRefreshed { get; }
    public IReadOnlyList<PartitionCellViewModel> Partitions { get; }
    internal TableSnapshot Snapshot { get; }

    public TableViewModel(TableSnapshot snapshot, HashSet<(string, string)> selectedKeys)
    {
        Snapshot = snapshot;
        TableName = snapshot.TableName;
        PartitionLabel = snapshot.PartitionCount == 1 ? "1 partition" : $"{snapshot.PartitionCount} partitions";
        DisplayRowCount = FormatCount(snapshot.TotalRowCount);
        DisplaySize = FormatBytes(snapshot.TotalSizeBytes);

        var mostRecent = snapshot.Partitions
            .Where(p => p.LastRefreshed.HasValue)
            .Select(p => p.LastRefreshed!.Value)
            .OrderByDescending(d => d)
            .FirstOrDefault();
        DisplayLastRefreshed = mostRecent != default
            ? mostRecent.LocalDateTime.ToString("MM-dd HH:mm")
            : "—";

        var maxSize = snapshot.MaxPartitionSizeBytes;
        Partitions = snapshot.Partitions
            .Select(p => new PartitionCellViewModel(
                p, maxSize,
                isSelected: selectedKeys.Contains((p.TableName, p.PartitionName))))
            .ToList();
    }

    private static string FormatCount(long n) => n switch
    {
        >= 1_000_000_000 => $"{n / 1_000_000_000.0:F1}B rows",
        >= 1_000_000     => $"{n / 1_000_000.0:F1}M rows",
        >= 1_000         => $"{n / 1_000.0:F1}K rows",
        0                => "0 rows",
        _                => $"{n} rows"
    };

    private static string FormatBytes(long b) => b switch
    {
        0                => "—",
        >= 1_073_741_824 => $"{b / 1_073_741_824.0:F1} GB",
        >= 1_048_576     => $"{b / 1_048_576.0:F1} MB",
        >= 1_024         => $"{b / 1_024.0:F1} KB",
        _                => $"{b} B"
    };
}

// ─────────────────────────────────────────────────────────────────────────────
// Page-level view model — drives PartitionMapView
// ─────────────────────────────────────────────────────────────────────────────

public partial class PartitionMapViewModel : ObservableObject
{
    private readonly ConnectionManager _connectionManager;
    private readonly TomRefreshEngine _refreshEngine;
    private readonly PartitionCacheStore _cache;
    private ModelRef? _currentModel;
    private IReadOnlyList<TableSnapshot> _snapshots = [];

    // Persisted selection — survives ApplyFilter() rebuilds
    private readonly HashSet<(string Table, string Partition)> _selectedKeys = [];

    public static IReadOnlyList<RefreshTypeOption> RefreshTypeOptions { get; } =
    [
        new("Default",   RefreshMode.Automatic),
        new("Full",      RefreshMode.Full),
        new("Data only", RefreshMode.DataOnly),
        new("Calculate", RefreshMode.Calculate),
        new("Clear",     RefreshMode.ClearValues),
    ];

    [ObservableProperty] private ObservableCollection<TableViewModel> _visibleTables = [];
    [ObservableProperty] private PartitionFilter _activeFilter = PartitionFilter.All;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isBackgroundRefreshing;
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _modelLabel;
    [ObservableProperty] private string? _refreshStatus;
    [ObservableProperty] private RefreshTypeOption _selectedRefreshType = RefreshTypeOptions[0];


    public bool IsFilterAll        => ActiveFilter == PartitionFilter.All;
    public bool IsFilterStale      => ActiveFilter == PartitionFilter.Stale;
    public bool IsFilterFailed     => ActiveFilter == PartitionFilter.Failed;
    public bool IsFilterRefreshing => ActiveFilter == PartitionFilter.Refreshing;

    public int SelectedCount => _selectedKeys.Count;

    /// <summary>
    /// Callback set by the view to show a confirmation dialog before a refresh.
    /// Receives (partitions, refreshTypeName); returns true to proceed, false to cancel.
    /// Defaults to always-proceed when null.
    /// </summary>
    public Func<IReadOnlyList<(string Table, string Partition)>, string, bool>? ConfirmRefresh { get; set; }

    public PartitionMapViewModel(ConnectionManager connectionManager, TomRefreshEngine refreshEngine, PartitionCacheStore cache)
    {
        _connectionManager = connectionManager;
        _refreshEngine = refreshEngine;
        _cache = cache;
    }


    // -------------------------------------------------------------------------
    // Loading
    // -------------------------------------------------------------------------

    public async Task LoadAsync(ModelRef model, CancellationToken ct = default)
    {
        _currentModel = model;
        ModelLabel = model.DatabaseName;
        ErrorMessage = null;
        ActiveFilter = PartitionFilter.All;

        _selectedKeys.Clear();
        OnPropertyChanged(nameof(SelectedCount));
        RefreshSelectedCommand.NotifyCanExecuteChanged();

        // Phase 0: show cached partitions immediately so the UI feels instant
        var cached = await _cache.LoadAsync(model.TenantId, model.DatabaseName, ct);
        if (cached.Count > 0)
        {
            _snapshots = cached;
            ApplyFilter();
            IsBackgroundRefreshing = true;
        }
        else
        {
            IsLoading = true;
        }

        try
        {
            // Phase 1: TOM structure → live partition names and states
            _snapshots = await PartitionService.GetTableStructureAsync(
                _connectionManager, model.TenantId, model.DatabaseName, ct);
            ApplyFilter();
            IsLoading = false;

            // Phase 2: DMV stats → row counts and sizes
            try
            {
                var storage = await DmvQueries.GetPartitionStorageAsync(
                    _connectionManager, model.TenantId, model.DatabaseName, ct);
                _snapshots = PartitionService.EnrichWithStorage(_snapshots, storage);
                ApplyFilter();
            }
            catch { /* stats unavailable — tiles remain without stats */ }

            // Persist the enriched snapshot so the next switch is instant
            try { await _cache.SaveAsync(model.TenantId, model.DatabaseName, _snapshots, ct); }
            catch { /* cache write failures are non-fatal */ }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            var msg = ex.Message;
            var inner = ex.InnerException;
            while (inner is not null) { msg += "\n→ " + inner.Message; inner = inner.InnerException; }
            ErrorMessage = msg;
        }
        finally
        {
            IsLoading = false;
            IsBackgroundRefreshing = false;
        }
    }

    // -------------------------------------------------------------------------
    // Selection
    // -------------------------------------------------------------------------

    /// <summary>
    /// Clicking the table info panel selects all partitions if any are unselected,
    /// or deselects all if every partition is already selected.
    /// </summary>
    [RelayCommand]
    private void ToggleTableSelection(TableViewModel? table)
    {
        if (table is null || table.Partitions.Count == 0) return;

        bool allSelected = table.Partitions.All(p => p.IsSelected);

        foreach (var cell in table.Partitions)
        {
            var key = (cell.TableName, cell.PartitionName);
            if (allSelected)
            {
                _selectedKeys.Remove(key);
                cell.IsSelected = false;
            }
            else
            {
                _selectedKeys.Add(key);
                cell.IsSelected = true;
            }
        }

        OnPropertyChanged(nameof(SelectedCount));
        RefreshSelectedCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ToggleSelection(PartitionCellViewModel? cell)
    {
        if (cell is null) return;
        var key = (cell.TableName, cell.PartitionName);
        if (_selectedKeys.Contains(key))
        {
            _selectedKeys.Remove(key);
            cell.IsSelected = false;
        }
        else
        {
            _selectedKeys.Add(key);
            cell.IsSelected = true;
        }
        OnPropertyChanged(nameof(SelectedCount));
        RefreshSelectedCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        _selectedKeys.Clear();
        foreach (var tvm in VisibleTables)
            foreach (var cell in tvm.Partitions)
                cell.IsSelected = false;
        OnPropertyChanged(nameof(SelectedCount));
        RefreshSelectedCommand.NotifyCanExecuteChanged();
    }

    // -------------------------------------------------------------------------
    // Refresh
    // -------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanRefreshSelected))]
    private async Task RefreshSelected(CancellationToken ct)
    {
        if (_currentModel is null || _selectedKeys.Count == 0) return;

        // Show confirmation dialog if one is wired up
        var partitionList = _selectedKeys.ToList();
        if (ConfirmRefresh is not null && !ConfirmRefresh(partitionList, SelectedRefreshType.DisplayName))
            return;

        IsRefreshing = true;
        RefreshStatus = $"Refreshing {partitionList.Count} partition(s)…";

        try
        {
            await _refreshEngine.RefreshAsync(
                _currentModel.TenantId,
                _currentModel.DatabaseName,
                partitionList,
                mode: SelectedRefreshType.Mode,
                progress: null,
                ct);

            RefreshStatus = $"Refresh completed — {partitionList.Count} partition(s)";
        }
        catch (OperationCanceledException)
        {
            RefreshStatus = "Refresh cancelled";
        }
        catch (Exception ex)
        {
            RefreshStatus = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            IsRefreshing = false;
        }

        // Reload partition data to reflect new state
        if (_currentModel is not null)
            await LoadAsync(_currentModel, ct);
    }

    private bool CanRefreshSelected() => _selectedKeys.Count > 0 && !IsRefreshing && !IsLoading;

    partial void OnIsRefreshingChanged(bool value)          => RefreshSelectedCommand.NotifyCanExecuteChanged();
    partial void OnIsLoadingChanged(bool value)             => RefreshSelectedCommand.NotifyCanExecuteChanged();
    partial void OnIsBackgroundRefreshingChanged(bool value) => RefreshSelectedCommand.NotifyCanExecuteChanged();

    // -------------------------------------------------------------------------
    // Filter
    // -------------------------------------------------------------------------

    [RelayCommand]
    private void SetFilter(PartitionFilter filter)
    {
        ActiveFilter = filter;
        ApplyFilter();
    }

    [RelayCommand]
    private async Task Reload()
    {
        if (_currentModel is null) return;
        await LoadAsync(_currentModel);
    }

    partial void OnActiveFilterChanged(PartitionFilter value)
    {
        OnPropertyChanged(nameof(IsFilterAll));
        OnPropertyChanged(nameof(IsFilterStale));
        OnPropertyChanged(nameof(IsFilterFailed));
        OnPropertyChanged(nameof(IsFilterRefreshing));
    }

    private void ApplyFilter()
    {
        IEnumerable<TableSnapshot> source = _snapshots;

        if (ActiveFilter != PartitionFilter.All)
        {
            var matchState = ActiveFilter switch
            {
                PartitionFilter.Stale      => PartitionState.Stale,
                PartitionFilter.Failed     => PartitionState.Failed,
                PartitionFilter.Refreshing => PartitionState.Refreshing,
                _                          => throw new InvalidOperationException()
            };
            source = _snapshots
                .Select(s =>
                {
                    var filtered = s.Partitions.Where(p => p.State == matchState).ToList();
                    return s with
                    {
                        Partitions            = filtered,
                        PartitionCount        = filtered.Count,
                        TotalRowCount         = filtered.Sum(p => p.RowCount ?? 0),
                        TotalSizeBytes        = filtered.Sum(p => p.SizeBytes ?? 0),
                    };
                })
                .Where(s => s.Partitions.Any());
        }

        VisibleTables = new ObservableCollection<TableViewModel>(
            source.Select(s => new TableViewModel(s, _selectedKeys)));
    }
}
