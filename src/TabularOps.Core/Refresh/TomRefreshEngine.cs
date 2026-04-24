using Microsoft.AnalysisServices.Tabular;
using TabularOps.Core.Connection;

namespace TabularOps.Core.Refresh;

/// <summary>
/// Refresh strategy exposed to callers. Maps to TOM's RefreshType so the UI
/// layer doesn't need a direct dependency on the TOM assembly.
/// </summary>
public enum RefreshMode
{
    /// <summary>Automatic — AS decides which partitions need work (Process Default in SSMS).</summary>
    Automatic,
    /// <summary>Full data reload + recalculation.</summary>
    Full,
    /// <summary>Reload source data; skip recalculation pass.</summary>
    DataOnly,
    /// <summary>Recalculate calculated columns/measures; no data reload.</summary>
    Calculate,
    /// <summary>Drop all data from the partition (Process Clear in SSMS).</summary>
    ClearValues,
}

/// <summary>
/// Refreshes partitions via TOM RequestRefresh + SaveChanges.
/// Used for SSAS/AAS and Power BI XMLA (read-write capacity).
///
/// Progress events fire per-partition so the UI can update state.
/// SaveChanges is called once per batch for efficiency.
/// </summary>
public sealed class TomRefreshEngine
{
    private readonly ConnectionManager _connectionManager;
    private readonly RefreshHistoryStore _historyStore;

    public TomRefreshEngine(ConnectionManager connectionManager, RefreshHistoryStore historyStore)
    {
        _connectionManager = connectionManager;
        _historyStore = historyStore;
    }

    /// <param name="partitions">Partitions to refresh, as (tableName, partitionName) pairs.</param>
    /// <param name="mode">Refresh strategy; defaults to Automatic (Process Default).</param>
    /// <param name="progress">Receives one event per partition when it starts and when the batch completes.</param>
    public async Task<IReadOnlyList<RefreshRun>> RefreshAsync(
        string tenantId,
        string databaseName,
        IReadOnlyList<(string TableName, string PartitionName)> partitions,
        RefreshMode mode = RefreshMode.Automatic,
        IProgress<RefreshProgressEvent>? progress = null,
        CancellationToken ct = default)
    {
        if (partitions.Count == 0) return [];

        var server = await _connectionManager.GetOrCreateCatalogServerAsync(tenantId, databaseName, ct);

        var db = server.Databases.Cast<Microsoft.AnalysisServices.Database>()
                     .FirstOrDefault(d => d.Name == databaseName)
                 ?? throw new InvalidOperationException(
                     $"Database '{databaseName}' not found on the server.");

        var model = db.Model;

        // Log start and mark each partition as Queued in the progress stream
        var runIds = new Dictionary<(string, string), long>(partitions.Count);
        foreach (var (tbl, part) in partitions)
        {
            var id = await _historyStore.LogStartAsync(tenantId, databaseName, tbl, part, ct);
            runIds[(tbl, part)] = id;
            progress?.Report(new RefreshProgressEvent(tbl, part, RefreshStatus.Running, null));

            // Mark the partition for refresh in TOM (does not send to server yet)
            var tomTable = model.Tables.Find(tbl)
                ?? throw new InvalidOperationException($"Table '{tbl}' not found in model.");
            var tomPartition = tomTable.Partitions.Find(part)
                ?? throw new InvalidOperationException(
                    $"Partition '{part}' not found in table '{tbl}'.");

            tomPartition.RequestRefresh(ToTomRefreshType(mode));
        }

        // Send batch to server — SaveChanges is synchronous and blocks until done
        RefreshStatus batchStatus = RefreshStatus.Completed;
        string? batchError = null;
        try
        {
            await Task.Run(() => model.SaveChanges(), ct);
            // Re-read partition metadata from the server so RefreshedTime reflects the
            // actual completion time. SaveChanges doesn't always sync this back locally.
            await Task.Run(() => db.Refresh(), ct);
        }
        catch (OperationCanceledException)
        {
            batchStatus = RefreshStatus.Cancelled;
        }
        catch (Exception ex)
        {
            batchStatus = RefreshStatus.Failed;
            batchError = ex.Message;
        }

        // Write final status for all partitions
        var runs = new List<RefreshRun>(partitions.Count);
        foreach (var (tbl, part) in partitions)
        {
            var runId = runIds[(tbl, part)];
            await _historyStore.LogCompleteAsync(runId, batchStatus, batchError, ct);
            progress?.Report(new RefreshProgressEvent(tbl, part, batchStatus, batchError));

            runs.Add(new RefreshRun(
                Id: runId,
                TenantId: tenantId,
                DatabaseName: databaseName,
                TableName: tbl,
                PartitionName: part,
                StartedAt: DateTimeOffset.UtcNow,
                CompletedAt: DateTimeOffset.UtcNow,
                Status: batchStatus,
                ErrorMessage: batchError));
        }

        if (batchStatus == RefreshStatus.Failed && batchError is not null)
            throw new InvalidOperationException(batchError);

        return runs;
    }

    /// <summary>
    /// Refreshes the entire model in one TOM call — equivalent to "Process Database" in SSMS.
    /// Faster and simpler than enumerating partitions when all partitions need refreshing.
    /// </summary>
    public async Task RefreshModelAsync(
        string tenantId,
        string databaseName,
        RefreshMode mode = RefreshMode.Automatic,
        CancellationToken ct = default)
    {
        var server = await _connectionManager.GetOrCreateCatalogServerAsync(tenantId, databaseName, ct);

        var db = server.Databases.Cast<Microsoft.AnalysisServices.Database>()
                     .FirstOrDefault(d => d.Name == databaseName)
                 ?? throw new InvalidOperationException(
                     $"Database '{databaseName}' not found on the server.");

        db.Model.RequestRefresh(ToTomRefreshType(mode));
        await Task.Run(() => db.Model.SaveChanges(), ct);
        await Task.Run(() => db.Refresh(), ct);
    }

    private static RefreshType ToTomRefreshType(RefreshMode mode) => mode switch
    {
        RefreshMode.Full        => RefreshType.Full,
        RefreshMode.DataOnly    => RefreshType.DataOnly,
        RefreshMode.Calculate   => RefreshType.Calculate,
        RefreshMode.ClearValues => RefreshType.ClearValues,
        _                       => RefreshType.Automatic,
    };
}

public sealed record RefreshProgressEvent(
    string TableName,
    string PartitionName,
    RefreshStatus Status,
    string? ErrorMessage);
