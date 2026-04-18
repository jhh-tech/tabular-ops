using Microsoft.Data.Sqlite;

namespace TabularOps.Core.Refresh;

/// <summary>
/// Persists refresh run history to a local SQLite database.
/// Thread-safe: all writes use a single serialised connection.
/// </summary>
public sealed class RefreshHistoryStore : IAsyncDisposable
{
    private readonly SqliteConnection _db;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public RefreshHistoryStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _db = new SqliteConnection($"Data Source={dbPath}");
        _db.Open();
        CreateSchema();
    }

    private void CreateSchema()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS refresh_runs (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                tenant_id       TEXT    NOT NULL,
                database_name   TEXT    NOT NULL,
                table_name      TEXT    NOT NULL,
                partition_name  TEXT    NOT NULL,
                started_at      TEXT    NOT NULL,
                completed_at    TEXT,
                status          TEXT    NOT NULL,
                error_message   TEXT,
                source          TEXT    NOT NULL DEFAULT 'App',
                refresh_type    TEXT,
                external_id     TEXT
            );
            """;
        cmd.ExecuteNonQuery();

        // Migrate existing tables that predate the source/refresh_type/external_id columns
        // Must run before creating the index that references these columns
        MigrateAddColumnIfMissing("source",       "TEXT NOT NULL DEFAULT 'App'");
        MigrateAddColumnIfMissing("refresh_type", "TEXT");
        MigrateAddColumnIfMissing("external_id",  "TEXT");

        using var idxCmd = _db.CreateCommand();
        idxCmd.CommandText = """
            CREATE UNIQUE INDEX IF NOT EXISTS ix_refresh_runs_external
                ON refresh_runs(tenant_id, database_name, external_id)
                WHERE external_id IS NOT NULL;
            """;
        idxCmd.ExecuteNonQuery();
    }

    private void MigrateAddColumnIfMissing(string column, string definition)
    {
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"ALTER TABLE refresh_runs ADD COLUMN {column} {definition};";
            cmd.ExecuteNonQuery();
        }
        catch { /* column already exists — SQLite raises an error, ignore it */ }
    }

    /// <summary>Inserts a Running row and returns its auto-generated id.</summary>
    public async Task<long> LogStartAsync(
        string tenantId, string databaseName,
        string tableName, string partitionName,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO refresh_runs
                    (tenant_id, database_name, table_name, partition_name, started_at, status)
                VALUES
                    ($tid, $db, $tbl, $part, $ts, 'Running');
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$tid",  tenantId);
            cmd.Parameters.AddWithValue("$db",   databaseName);
            cmd.Parameters.AddWithValue("$tbl",  tableName);
            cmd.Parameters.AddWithValue("$part", partitionName);
            cmd.Parameters.AddWithValue("$ts",   DateTimeOffset.UtcNow.ToString("O"));
            return (long)(await Task.Run(() => cmd.ExecuteScalar(), ct))!;
        }
        finally { _lock.Release(); }
    }

    /// <summary>Updates an existing row with the final status.</summary>
    public async Task LogCompleteAsync(
        long runId, RefreshStatus status, string? errorMessage = null,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                UPDATE refresh_runs
                SET completed_at  = $ts,
                    status        = $status,
                    error_message = $err
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$ts",     DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$status",  status.ToString());
            cmd.Parameters.AddWithValue("$err",     errorMessage ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$id",      runId);
            await Task.Run(() => cmd.ExecuteNonQuery(), ct);
        }
        finally { _lock.Release(); }
    }

    /// <summary>Returns the most recent refresh runs, newest first.</summary>
    public async Task<IReadOnlyList<RefreshRun>> GetRecentAsync(
        string? tenantId = null, string? databaseName = null,
        int limit = 200, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT id, tenant_id, database_name, table_name, partition_name,
                       started_at, completed_at, status, error_message,
                       COALESCE(source, 'App'), COALESCE(refresh_type, ''), COALESCE(external_id, '')
                FROM   refresh_runs
                WHERE  ($tid IS NULL OR tenant_id = $tid)
                  AND  ($db  IS NULL OR database_name = $db)
                ORDER BY started_at DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$tid",   tenantId ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$db",    databaseName ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$limit", limit);

            return await Task.Run(() =>
            {
                var runs = new List<RefreshRun>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var completedAt = reader.IsDBNull(6) ? (DateTimeOffset?)null
                        : DateTimeOffset.Parse(reader.GetString(6));
                    var status = Enum.Parse<RefreshStatus>(reader.GetString(7));
                    runs.Add(new RefreshRun(
                        Id:            reader.GetInt64(0),
                        TenantId:      reader.GetString(1),
                        DatabaseName:  reader.GetString(2),
                        TableName:     reader.GetString(3),
                        PartitionName: reader.GetString(4),
                        StartedAt:     DateTimeOffset.Parse(reader.GetString(5)),
                        CompletedAt:   completedAt,
                        Status:        status,
                        ErrorMessage:  reader.IsDBNull(8) ? null : reader.GetString(8),
                        Source:        reader.GetString(9),
                        RefreshType:   reader.GetString(10)));
                }
                return (IReadOnlyList<RefreshRun>)runs;
            }, ct);
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Upserts a workspace-level refresh entry (identified by external_id).
    /// A full-model workspace refresh is stored with TableName="*", PartitionName="*".
    /// Existing entries with the same external_id are not duplicated.
    /// </summary>
    public async Task ImportWorkspaceRunAsync(
        string tenantId, string databaseName,
        string externalId, string refreshType,
        DateTimeOffset startedAt, DateTimeOffset? completedAt,
        RefreshStatus status, string? errorMessage,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO refresh_runs
                    (tenant_id, database_name, table_name, partition_name,
                     started_at, completed_at, status, error_message,
                     source, refresh_type, external_id)
                VALUES
                    ($tid, $db, '*', '*',
                     $start, $end, $status, $err,
                     'Workspace', $type, $extid)
                ON CONFLICT(tenant_id, database_name, external_id)
                    WHERE external_id IS NOT NULL
                DO UPDATE SET
                    completed_at  = excluded.completed_at,
                    status        = excluded.status,
                    error_message = excluded.error_message;
                """;
            cmd.Parameters.AddWithValue("$tid",   tenantId);
            cmd.Parameters.AddWithValue("$db",    databaseName);
            cmd.Parameters.AddWithValue("$start", startedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$end",   completedAt.HasValue ? completedAt.Value.ToString("O") : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$status", status.ToString());
            cmd.Parameters.AddWithValue("$err",   errorMessage ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$type",  refreshType);
            cmd.Parameters.AddWithValue("$extid", externalId);
            await Task.Run(() => cmd.ExecuteNonQuery(), ct);
        }
        finally { _lock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        _lock.Dispose();
    }
}
