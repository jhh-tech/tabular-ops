using Microsoft.Data.Sqlite;
using TabularOps.Core.Model;
using TabularOps.Core.Refresh;

namespace TabularOps.Core.Dmv;

/// <summary>
/// Persists the last-known partition snapshot for each model to SQLite so that
/// PartitionMapView can display cached data immediately on model switch while
/// live data loads in the background.
/// </summary>
public sealed class PartitionCacheStore : IAsyncDisposable
{
    private readonly string _connectionString;
    private bool _initialized;

    public PartitionCacheStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = $"Data Source={dbPath}";
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS partition_cache (
                tenant_id       TEXT NOT NULL,
                database_name   TEXT NOT NULL,
                table_name      TEXT NOT NULL,
                partition_name  TEXT NOT NULL,
                state           INTEGER NOT NULL,
                last_refreshed  TEXT,
                row_count       INTEGER,
                size_bytes      INTEGER,
                last_error      TEXT,
                PRIMARY KEY (tenant_id, database_name, table_name, partition_name)
            );
            """;
        await cmd.ExecuteNonQueryAsync();
        _initialized = true;
    }

    /// <summary>
    /// Replaces the cached partitions for a model with the provided snapshots.
    /// </summary>
    public async Task SaveAsync(
        string tenantId,
        string databaseName,
        IReadOnlyList<TableSnapshot> snapshots,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var del = conn.CreateCommand())
        {
            del.Transaction = (SqliteTransaction)tx;
            del.CommandText = "DELETE FROM partition_cache WHERE tenant_id=@t AND database_name=@d";
            del.Parameters.AddWithValue("@t", tenantId);
            del.Parameters.AddWithValue("@d", databaseName);
            await del.ExecuteNonQueryAsync(ct);
        }

        await using var ins = conn.CreateCommand();
        ins.Transaction = (SqliteTransaction)tx;
        ins.CommandText = """
            INSERT INTO partition_cache
                (tenant_id, database_name, table_name, partition_name,
                 state, last_refreshed, row_count, size_bytes, last_error)
            VALUES (@t, @d, @tn, @pn, @st, @lr, @rc, @sb, @le)
            """;

        var pT  = ins.Parameters.Add("@t",  SqliteType.Text);
        var pD  = ins.Parameters.Add("@d",  SqliteType.Text);
        var pTn = ins.Parameters.Add("@tn", SqliteType.Text);
        var pPn = ins.Parameters.Add("@pn", SqliteType.Text);
        var pSt = ins.Parameters.Add("@st", SqliteType.Integer);
        var pLr = ins.Parameters.Add("@lr", SqliteType.Text);
        var pRc = ins.Parameters.Add("@rc", SqliteType.Integer);
        var pSb = ins.Parameters.Add("@sb", SqliteType.Integer);
        var pLe = ins.Parameters.Add("@le", SqliteType.Text);

        pT.Value = tenantId;
        pD.Value = databaseName;

        foreach (var snapshot in snapshots)
        {
            foreach (var p in snapshot.Partitions)
            {
                pTn.Value = p.TableName;
                pPn.Value = p.PartitionName;
                pSt.Value = (int)p.State;
                pLr.Value = p.LastRefreshed.HasValue
                    ? (object)p.LastRefreshed.Value.ToString("O")
                    : DBNull.Value;
                pRc.Value = p.RowCount.HasValue ? (object)p.RowCount.Value : DBNull.Value;
                pSb.Value = p.SizeBytes.HasValue ? (object)p.SizeBytes.Value : DBNull.Value;
                pLe.Value = p.LastError is not null ? (object)p.LastError : DBNull.Value;
                await ins.ExecuteNonQueryAsync(ct);
            }
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Returns the cached partition snapshots for a model, or an empty list if none exist.
    /// </summary>
    public async Task<IReadOnlyList<TableSnapshot>> LoadAsync(
        string tenantId,
        string databaseName,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT table_name, partition_name, state, last_refreshed,
                   row_count, size_bytes, last_error
            FROM partition_cache
            WHERE tenant_id=@t AND database_name=@d
            ORDER BY table_name, partition_name
            """;
        cmd.Parameters.AddWithValue("@t", tenantId);
        cmd.Parameters.AddWithValue("@d", databaseName);

        var byTable = new Dictionary<string, List<PartitionRef>>(StringComparer.OrdinalIgnoreCase);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var tableName     = reader.GetString(0);
            var partitionName = reader.GetString(1);
            var state         = (PartitionState)reader.GetInt32(2);
            DateTimeOffset? lastRefreshed = reader.IsDBNull(3)
                ? null
                : DateTimeOffset.Parse(reader.GetString(3));
            long? rowCount  = reader.IsDBNull(4) ? null : reader.GetInt64(4);
            long? sizeBytes = reader.IsDBNull(5) ? null : reader.GetInt64(5);
            string? lastError = reader.IsDBNull(6) ? null : reader.GetString(6);

            var partition = new PartitionRef(
                tableName, partitionName, state, lastRefreshed, rowCount, sizeBytes, lastError);

            if (!byTable.TryGetValue(tableName, out var list))
                byTable[tableName] = list = [];
            list.Add(partition);
        }

        return byTable
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv =>
            {
                var partitions = kv.Value;
                return new TableSnapshot(
                    TableName:             kv.Key,
                    IsHidden:              false,
                    PartitionCount:        partitions.Count,
                    TotalRowCount:         partitions.Sum(p => p.RowCount ?? 0),
                    TotalSizeBytes:        partitions.Sum(p => p.SizeBytes ?? 0),
                    MaxPartitionSizeBytes: partitions.Max(p => p.SizeBytes ?? 0L),
                    Partitions:            partitions);
            })
            .ToList();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
