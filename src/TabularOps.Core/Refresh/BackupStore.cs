using Microsoft.Data.Sqlite;

namespace TabularOps.Core.Refresh;

/// <summary>
/// Persists model backup history to SQLite.
/// Thread-safe: all writes use a single serialised connection.
/// </summary>
public sealed class BackupStore : IAsyncDisposable
{
    private readonly SqliteConnection _db;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public BackupStore(string dbPath)
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
            CREATE TABLE IF NOT EXISTS backup_runs (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                tenant_id     TEXT    NOT NULL,
                database_name TEXT    NOT NULL,
                file_name     TEXT    NOT NULL,
                started_at    TEXT    NOT NULL,
                completed_at  TEXT,
                succeeded     INTEGER NOT NULL DEFAULT 0,
                error_message TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_backup_runs_model
                ON backup_runs(tenant_id, database_name, started_at DESC);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Inserts a row before the backup starts and returns its id.</summary>
    public async Task<long> LogStartAsync(
        string tenantId, string databaseName, string fileName,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO backup_runs (tenant_id, database_name, file_name, started_at, succeeded)
                VALUES ($tid, $db, $file, $ts, 0);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$tid",  tenantId);
            cmd.Parameters.AddWithValue("$db",   databaseName);
            cmd.Parameters.AddWithValue("$file", fileName);
            cmd.Parameters.AddWithValue("$ts",   DateTimeOffset.UtcNow.ToString("O"));
            return (long)(await Task.Run(() => cmd.ExecuteScalar(), ct))!;
        }
        finally { _lock.Release(); }
    }

    /// <summary>Updates the row with the final outcome.</summary>
    public async Task LogCompleteAsync(
        long runId, bool succeeded, string? errorMessage,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                UPDATE backup_runs
                SET completed_at  = $ts,
                    succeeded     = $ok,
                    error_message = $err
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$ts",  DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$ok",  succeeded ? 1 : 0);
            cmd.Parameters.AddWithValue("$err", errorMessage ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$id",  runId);
            await Task.Run(() => cmd.ExecuteNonQuery(), ct);
        }
        finally { _lock.Release(); }
    }

    /// <summary>Returns the most recent successful backup for this model, or null if none.</summary>
    public async Task<BackupRun?> GetLastBackupAsync(
        string tenantId, string databaseName,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT id, tenant_id, database_name, file_name,
                       started_at, completed_at, succeeded, error_message
                FROM   backup_runs
                WHERE  tenant_id = $tid AND database_name = $db AND succeeded = 1
                ORDER  BY completed_at DESC
                LIMIT  1;
                """;
            cmd.Parameters.AddWithValue("$tid", tenantId);
            cmd.Parameters.AddWithValue("$db",  databaseName);
            return await Task.Run(() =>
            {
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return null;
                return new BackupRun(
                    Id:           r.GetInt64(0),
                    TenantId:     r.GetString(1),
                    DatabaseName: r.GetString(2),
                    FileName:     r.GetString(3),
                    StartedAt:    DateTimeOffset.Parse(r.GetString(4)),
                    CompletedAt:  r.IsDBNull(5) ? null : DateTimeOffset.Parse(r.GetString(5)),
                    Succeeded:    r.GetInt32(6) == 1,
                    ErrorMessage: r.IsDBNull(7) ? null : r.GetString(7));
            }, ct);
        }
        finally { _lock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        _lock.Dispose();
    }
}

public sealed record BackupRun(
    long Id,
    string TenantId,
    string DatabaseName,
    string FileName,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    bool Succeeded,
    string? ErrorMessage);
