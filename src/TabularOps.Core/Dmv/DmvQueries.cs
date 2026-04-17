using TabularOps.Core.Connection;

namespace TabularOps.Core.Dmv;

/// <summary>
/// DMV queries executed via ADOMD on the poll connection.
/// All methods return plain data — no TOM dependency.
/// </summary>
public static class DmvQueries
{
    /// <summary>
    /// Returns storage statistics per partition from DMV.
    /// Keyed by (TableName, PartitionName).
    ///
    /// Power BI XMLA quirks:
    ///   - DISCOVER_STORAGE_TABLE_PARTITIONS is not supported
    ///   - DISCOVER_STORAGE_TABLES returns column-segment rows (TABLE_ID = H$Table(id)$Column(id)),
    ///     not partition rows — no PARTITION_NAME is available there
    ///   - DISCOVER_STORAGE_TABLE_COLUMN_SEGMENTS has PARTITION_NAME + ELEMENT_COUNT and IS supported
    ///
    /// Strategy: try COLUMN_SEGMENTS first; fall back to STORAGE_TABLES (table-level only).
    /// </summary>
    public static async Task<Dictionary<(string Table, string Partition), PartitionStorageInfo>>
        GetPartitionStorageAsync(
            ConnectionManager connectionManager,
            string tenantId,
            string databaseName,
            CancellationToken ct = default)
    {
        // Primary: DISCOVER_STORAGE_TABLE_COLUMN_SEGMENTS has PARTITION_NAME + row counts
        try
        {
            const string q = "SELECT * FROM $SYSTEM.DISCOVER_STORAGE_TABLE_COLUMN_SEGMENTS";
            var rows = await connectionManager.ExecuteDmvAsync(tenantId, q, ct, catalogName: databaseName);
            if (rows.Count > 0)
                return BuildFromColumnSegments(rows);
        }
        catch { /* DMV not supported on this endpoint — fall through */ }

        // Fallback: DISCOVER_STORAGE_TABLES — column segments but no partition name.
        // Aggregate row count per table and store under a (tableName, tableName) key so
        // PartitionService's table-name fallback can still surface a total.
        try
        {
            const string q = "SELECT * FROM $SYSTEM.DISCOVER_STORAGE_TABLES";
            var rows = await connectionManager.ExecuteDmvAsync(tenantId, q, ct, catalogName: databaseName);
            return BuildFromStorageTables(rows);
        }
        catch { return new Dictionary<(string, string), PartitionStorageInfo>(); }
    }

    // -------------------------------------------------------------------------
    // DISCOVER_STORAGE_TABLE_COLUMN_SEGMENTS  (preferred path)
    // Columns: DIMENSION_NAME, PARTITION_NAME, ELEMENT_COUNT, CURRENT_SIZE, …
    // -------------------------------------------------------------------------

    private static Dictionary<(string, string), PartitionStorageInfo> BuildFromColumnSegments(
        List<Dictionary<string, object?>> rows)
    {
        var result = new Dictionary<(string, string), PartitionStorageInfo>();

        foreach (var row in rows)
        {
            var tableName = TryGetString(row, "DIMENSION_NAME") ?? string.Empty;
            if (string.IsNullOrEmpty(tableName)) continue;

            // POS_TO_ID rows are internal position-mapping structures — always RECORDS_COUNT=0,
            // and their USED_SIZE would inflate the apparent data size. Skip them entirely.
            var columnId = TryGetString(row, "COLUMN_ID") ?? string.Empty;
            if (string.Equals(columnId, "POS_TO_ID", StringComparison.OrdinalIgnoreCase))
                continue;

            // PARTITION_NAME is present on Power BI XMLA; fall back to table name for safety
            var partitionName = TryGetString(row, "PARTITION_NAME") ?? tableName;

            // RECORDS_COUNT is the row count for this column segment (= partition row count).
            // All columns in the same partition share the same count — take max across columns.
            var rowCount = TryGetLong(row, "RECORDS_COUNT") ?? 0L;
            var usedSize = TryGetLong(row, "USED_SIZE") ?? TryGetLong(row, "CURRENT_SIZE") ?? 0L;

            var key = (tableName, partitionName);
            if (result.TryGetValue(key, out var existing))
                result[key] = existing with
                {
                    RowCount  = Math.Max(existing.RowCount, rowCount),
                    SizeBytes = existing.SizeBytes + usedSize,
                };
            else
                result[key] = new PartitionStorageInfo(tableName, partitionName, rowCount, usedSize);
        }

        return result;
    }

    // -------------------------------------------------------------------------
    // DISCOVER_STORAGE_TABLES  (fallback — no partition granularity)
    // Columns: DIMENSION_NAME, TABLE_ID (H$Table(id)$Column(id)), ROWS_COUNT, …
    // -------------------------------------------------------------------------

    private static Dictionary<(string, string), PartitionStorageInfo> BuildFromStorageTables(
        List<Dictionary<string, object?>> rows)
    {
        // Aggregate by table name only — store under (tableName, tableName) so that
        // PartitionService's fallback lookup (table.Name, table.Name) can find it.
        var byTable = new Dictionary<string, (long MaxRows, long TotalSize)>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var tableName = TryGetString(row, "DIMENSION_NAME") ?? string.Empty;
            if (string.IsNullOrEmpty(tableName)) continue;

            var rowCount = TryGetLong(row, "ROWS_COUNT") ?? 0L;
            var usedSize  = TryGetLong(row, "USED_SIZE") ?? 0L;

            byTable.TryGetValue(tableName, out var acc);
            byTable[tableName] = (Math.Max(acc.MaxRows, rowCount), acc.TotalSize + usedSize);
        }

        return byTable.ToDictionary(
            kv => (kv.Key, kv.Key),   // (tableName, tableName) key
            kv => new PartitionStorageInfo(kv.Key, kv.Key, kv.Value.MaxRows, kv.Value.TotalSize));
    }

    private static string? TryGetString(Dictionary<string, object?> row, string column)
    {
        if (!row.TryGetValue(column, out var val) || val is null) return null;
        var s = val.ToString();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static long? TryGetLong(Dictionary<string, object?> row, string column)
    {
        if (!row.TryGetValue(column, out var val) || val is null) return null;
        try { return Convert.ToInt64(val); }
        catch { return null; }
    }
}

public sealed record PartitionStorageInfo(
    string TableName,
    string PartitionName,
    long RowCount,
    long SizeBytes);
