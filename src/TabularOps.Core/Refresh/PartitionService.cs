using Microsoft.AnalysisServices.Tabular;
using TabularOps.Core.Connection;
using TabularOps.Core.Dmv;
using TabularOps.Core.Model;

namespace TabularOps.Core.Refresh;

/// <summary>
/// Builds table/partition snapshots from TOM metadata and DMV storage statistics.
///
/// The two-phase design lets callers show partition structure immediately (Phase 1 — TOM only),
/// then fill in row counts and sizes once the DMV query completes (Phase 2 — EnrichWithStorage).
/// </summary>
public static class PartitionService
{
    // -------------------------------------------------------------------------
    // Phase 1 — TOM structure only (fast once the TOM connection is open)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns one <see cref="TableSnapshot"/> per user-visible table, with partition
    /// names and states populated from TOM. Row counts and sizes are null until
    /// <see cref="EnrichWithStorage"/> is called.
    /// </summary>
    public static async Task<IReadOnlyList<TableSnapshot>> GetTableStructureAsync(
        ConnectionManager connectionManager,
        string tenantId,
        string databaseName,
        CancellationToken ct = default)
    {
        var server = await connectionManager.GetOrCreateCatalogServerAsync(tenantId, databaseName, ct);

        var db = server.Databases.Cast<Microsoft.AnalysisServices.Database>()
                     .FirstOrDefault(d => d.Name == databaseName)
                 ?? throw new InvalidOperationException(
                     $"Database '{databaseName}' not found on the server.");

        var model = db.Model;
        var snapshots = new List<TableSnapshot>();

        foreach (Table table in model.Tables)
        {
            if (table.IsHidden || table.CalculationGroup is not null)
                continue;

            var partitions = table.Partitions.Cast<Partition>().Select(p =>
            {
                var lastRefreshed = p.RefreshedTime == DateTime.MinValue
                    ? (DateTimeOffset?)null
                    : new DateTimeOffset(p.RefreshedTime, TimeSpan.Zero);

                return new PartitionRef(
                    TableName: table.Name,
                    PartitionName: p.Name,
                    State: ResolveState(p),
                    LastRefreshed: lastRefreshed,
                    RowCount: null,
                    SizeBytes: null,
                    LastError: string.IsNullOrEmpty(p.ErrorMessage) ? null : p.ErrorMessage);
            }).ToList();

            snapshots.Add(new TableSnapshot(
                TableName: table.Name,
                IsHidden: false,
                PartitionCount: table.Partitions.Count,
                TotalRowCount: 0,
                TotalSizeBytes: 0,
                MaxPartitionSizeBytes: 0,
                Partitions: partitions));
        }

        return snapshots.OrderBy(t => t.TableName).ToList();
    }

    // -------------------------------------------------------------------------
    // Phase 2 — enrich snapshots with DMV storage stats (pure, no network calls)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns new <see cref="TableSnapshot"/> instances with row counts and sizes
    /// populated from <paramref name="storage"/>. Non-matching partitions keep null stats.
    /// </summary>
    public static IReadOnlyList<TableSnapshot> EnrichWithStorage(
        IReadOnlyList<TableSnapshot> snapshots,
        Dictionary<(string Table, string Partition), PartitionStorageInfo> storage)
    {
        return snapshots.Select(snapshot =>
        {
            var enriched = snapshot.Partitions.Select(p =>
            {
                var info = ResolveStorageInfo(p.TableName, p.PartitionName, storage);
                return info is null ? p : p with { RowCount = info.RowCount, SizeBytes = info.SizeBytes };
            }).ToList();

            return snapshot with
            {
                Partitions           = enriched,
                TotalRowCount        = enriched.Sum(p => p.RowCount ?? 0),
                TotalSizeBytes       = enriched.Sum(p => p.SizeBytes ?? 0),
                MaxPartitionSizeBytes = enriched.Max(p => p.SizeBytes ?? 0L),
            };
        }).ToList();
    }

    private static PartitionStorageInfo? ResolveStorageInfo(
        string tableName,
        string partitionName,
        Dictionary<(string, string), PartitionStorageInfo> storage)
    {
        // Exact match (standard SSAS / correct PARTITION_NAME from DMV)
        if (storage.TryGetValue((tableName, partitionName), out var info))
            return info;

        // Power BI single-partition tables: DMV returns partitionName = tableName
        if (storage.TryGetValue((tableName, tableName), out info))
            return info;

        // Last resort: any entry whose table key matches (catches variant encodings)
        return storage
            .FirstOrDefault(kv => string.Equals(
                kv.Key.Item1, tableName, StringComparison.OrdinalIgnoreCase))
            .Value;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static PartitionState ResolveState(Partition partition)
    {
        if (!string.IsNullOrEmpty(partition.ErrorMessage))
            return PartitionState.Failed;

        return partition.State switch
        {
            ObjectState.Ready                  => PartitionState.Ok,
            ObjectState.NoData                 => PartitionState.Stale,
            ObjectState.CalculationNeeded      => PartitionState.Stale,
            ObjectState.ForceCalculationNeeded => PartitionState.Stale,
            ObjectState.Incomplete             => PartitionState.Stale,
            ObjectState.SemanticError          => PartitionState.Failed,
            ObjectState.EvaluationError        => PartitionState.Failed,
            ObjectState.DependencyError        => PartitionState.Failed,
            _                                  => PartitionState.Stale,
        };
    }
}

public sealed record TableSnapshot(
    string TableName,
    bool IsHidden,
    int PartitionCount,
    long TotalRowCount,
    long TotalSizeBytes,
    long MaxPartitionSizeBytes,
    IReadOnlyList<PartitionRef> Partitions);
