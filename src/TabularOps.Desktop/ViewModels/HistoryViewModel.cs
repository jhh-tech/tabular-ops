using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Microsoft.PowerBI.Api;
using TabularOps.Core.Connection;
using TabularOps.Core.Model;
using TabularOps.Core.Refresh;

namespace TabularOps.Desktop.ViewModels;

public sealed class RefreshRunViewModel
{
    public long Id { get; }
    public string TableName { get; }
    public string PartitionName { get; }
    public string StartedAt { get; }
    public string Duration { get; }
    public RefreshStatus Status { get; }
    public string? ErrorMessage { get; }
    public string Source { get; }
    public string RefreshType { get; }

    public bool IsCompleted  => Status == RefreshStatus.Completed;
    public bool IsFailed     => Status == RefreshStatus.Failed;
    public bool IsCancelled  => Status == RefreshStatus.Cancelled;
    public bool IsRunning    => Status == RefreshStatus.Running;
    public bool IsWorkspace  => Source == "Workspace";
    public bool IsFullModel  { get; }

    // For chart rendering
    public double StartedAtHour  { get; }   // 0–24, fractional
    public double? DurationSeconds { get; }

    // Display helpers
    public string DisplayScope { get; }  // "Full model" or "TableName · PartitionName"

    public RefreshRunViewModel(RefreshRun run)
    {
        Id = run.Id;
        TableName = run.TableName;
        PartitionName = run.PartitionName;
        StartedAt = run.StartedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        Duration = run.Duration.HasValue
            ? run.Duration.Value.TotalSeconds < 60
                ? $"{run.Duration.Value.TotalSeconds:F1}s"
                : $"{run.Duration.Value.TotalMinutes:F1}m"
            : "—";
        Status = run.Status;
        ErrorMessage = run.ErrorMessage;
        Source = run.Source;
        RefreshType = string.IsNullOrEmpty(run.RefreshType) ? "" : $" · {run.RefreshType}";

        IsFullModel    = run.TableName == "*" && run.PartitionName == "*";
        DisplayScope   = IsFullModel ? "Full model" : $"{run.TableName} · {run.PartitionName}";
        StartedAtHour  = run.StartedAt.LocalDateTime.TimeOfDay.TotalHours;
        DurationSeconds = run.Duration?.TotalSeconds;
    }
}

public partial class HistoryViewModel : ObservableObject
{
    private readonly RefreshHistoryStore _store;
    private readonly ConnectionManager _connectionManager;
    private string? _tenantId;
    private string? _workspaceName;
    private string? _databaseId;
    private string? _databaseName;
    private EndpointType _endpointType;

    [ObservableProperty] private ObservableCollection<RefreshRunViewModel> _runs = [];
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isSyncing;
    [ObservableProperty] private string? _syncError;

    // Sync requires a specific dataset — workspace-level (no databaseId) can't sync
    public bool CanSyncFromWorkspace => _endpointType == EndpointType.PowerBi && _databaseId is not null;
    public string? ModelName => _databaseName;

    public HistoryViewModel(RefreshHistoryStore store, ConnectionManager connectionManager)
    {
        _store = store;
        _connectionManager = connectionManager;
    }

    /// <summary>
    /// Sets filter context.
    /// Call with only tenantId/workspaceName to show all history for a workspace.
    /// Supply databaseId/databaseName to narrow to a specific semantic model.
    /// </summary>
    public void SetContext(
        string? tenantId,
        string? workspaceName,
        string? databaseId    = null,
        string? databaseName  = null,
        EndpointType endpointType = EndpointType.Ssas)
    {
        _tenantId      = tenantId;
        _workspaceName = workspaceName;
        _databaseId    = databaseId;
        _databaseName  = databaseName;
        _endpointType  = endpointType;
        OnPropertyChanged(nameof(CanSyncFromWorkspace));
        OnPropertyChanged(nameof(ModelName));
    }

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        try
        {
            var runs = await _store.GetRecentAsync(_tenantId, _databaseName, limit: 200, ct);
            Runs = new ObservableCollection<RefreshRunViewModel>(
                runs.Select(r => new RefreshRunViewModel(r)));
        }
        catch { }
        finally { IsLoading = false; }
    }

    /// <summary>
    /// Calls the Power BI REST API to fetch refresh history for the current dataset
    /// and imports the results into the local store. Idempotent — deduplicates by request ID.
    /// </summary>
    [RelayCommand]
    public async Task SyncFromWorkspaceAsync(CancellationToken ct = default)
    {
        if (!CanSyncFromWorkspace || _workspaceName is null || _databaseId is null) return;

        IsSyncing = true;
        SyncError = null;
        try
        {
            using var pbi = await _connectionManager.CreatePowerBiClientAsync(ct);

            // Resolve workspace name → group GUID via REST API
            var groups = await pbi.Groups.GetGroupsAsync(
                filter: $"name eq '{_workspaceName.Replace("'", "''")}'",
                cancellationToken: ct);
            var group = groups?.Value?.FirstOrDefault();
            if (group is null)
            {
                SyncError = $"Workspace '{_workspaceName}' not found in Power BI.";
                return;
            }

            var groupId   = group.Id;
            var datasetId = _databaseId;

            // Power BI returns up to 60 entries by default; request the maximum
            var history = await pbi.Datasets.GetRefreshHistoryInGroupAsync(
                groupId, datasetId, top: 60, cancellationToken: ct);

            if (history?.Value is null) return;

            foreach (var r in history.Value)
            {
                if (r.RequestId is null) continue;

                var status = r.Status switch
                {
                    "Completed" => RefreshStatus.Completed,
                    "Failed"    => RefreshStatus.Failed,
                    "Cancelled" => RefreshStatus.Cancelled,
                    "Unknown"   => RefreshStatus.Running,
                    _           => RefreshStatus.Running,
                };

                var started   = r.StartTime.HasValue ? new DateTimeOffset(r.StartTime.Value) : DateTimeOffset.UtcNow;
                var completed = r.EndTime.HasValue   ? new DateTimeOffset(r.EndTime.Value)   : (DateTimeOffset?)null;
                var errMsg    = r.ServiceExceptionJson;

                await _store.ImportWorkspaceRunAsync(
                    _tenantId!, _databaseName!,
                    externalId: r.RequestId,
                    refreshType: r.RefreshType?.ToString() ?? "",
                    startedAt: started, completedAt: completed,
                    status: status, errorMessage: errMsg, ct);
            }

            // Reload the list to show imported entries
            await RefreshAsync(ct);
        }
        catch (Exception ex)
        {
            SyncError = ex.Message;
        }
        finally
        {
            IsSyncing = false;
        }
    }
}
