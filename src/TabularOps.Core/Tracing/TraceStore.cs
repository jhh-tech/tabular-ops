using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace TabularOps.Core.Tracing;

/// <summary>
/// Persists trace events to SQLite for the rolling 30-day archive.
/// Batches inserts (500 ms or 50 events) to avoid writer saturation.
///
/// Thread-safe: all writes are serialised by the write lock.
/// </summary>
public sealed class TraceStore : IAsyncDisposable
{
    private readonly SqliteConnection _db;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private readonly Channel<TraceEvent> _ingest;
    private readonly Task _processLoop;
    private readonly CancellationTokenSource _cts;

    public TraceStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _db = new SqliteConnection($"Data Source={dbPath}");
        _db.Open();
        CreateSchema();

        _cts = new CancellationTokenSource();
        _ingest = Channel.CreateBounded<TraceEvent>(new BoundedChannelOptions(5_000)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        _processLoop = ProcessLoopAsync(_cts.Token);
    }

    /// <summary>
    /// Enqueues an event for async batch insert. Returns immediately.
    /// </summary>
    public bool TryEnqueue(TraceEvent evt) => _ingest.Writer.TryWrite(evt);

    private async Task ProcessLoopAsync(CancellationToken ct)
    {
        var buffer = new List<TraceEvent>(50);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                while (buffer.Count < 50)
                {
                    var evt = await _ingest.Reader.ReadAsync(ct);
                    buffer.Add(evt);
                }
            }
            catch (OperationCanceledException) { break; }

            if (buffer.Count > 0)
            {
                await FlushAsync(buffer, ct);
                buffer.Clear();
            }
        }

        // Drain remainder on shutdown
        while (_ingest.Reader.TryRead(out var evt))
            buffer.Add(evt);
        if (buffer.Count > 0)
            await FlushAsync(buffer, CancellationToken.None);
    }

    private async Task FlushAsync(List<TraceEvent> buffer, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var batch = _db.CreateCommand();
            batch.CommandText = """
                INSERT INTO trace_events
                    (event_id, time, event_class, event_subclass, text,
                     partition_name, table_name, duration_ms, cpu_ms,
                     row_count, error_code, database_name, session_id)
                VALUES
                    ($id, $time, $cls, $sub, $txt,
                     $part, $tbl, $dur, $cpu,
                     $rows, $err, $db, $sid);
                """;

            foreach (var evt in buffer)
            {
                batch.Parameters.Clear();
                batch.Parameters.AddWithValue("$id",   evt.Id);
                batch.Parameters.AddWithValue("$time", evt.Time.ToString("O"));
                batch.Parameters.AddWithValue("$cls",  evt.EventClass);
                batch.Parameters.AddWithValue("$sub",  evt.EventSubclass ?? (object)DBNull.Value);
                batch.Parameters.AddWithValue("$txt",  evt.Text ?? (object)DBNull.Value);
                batch.Parameters.AddWithValue("$part", evt.PartitionName ?? (object)DBNull.Value);
                batch.Parameters.AddWithValue("$tbl",  evt.TableName ?? (object)DBNull.Value);
                batch.Parameters.AddWithValue("$dur",  evt.DurationMs ?? (object)DBNull.Value);
                batch.Parameters.AddWithValue("$cpu",  evt.CpuMs ?? (object)DBNull.Value);
                batch.Parameters.AddWithValue("$rows", evt.RowCount ?? (object)DBNull.Value);
                batch.Parameters.AddWithValue("$err",  evt.ErrorCode ?? (object)DBNull.Value);
                batch.Parameters.AddWithValue("$db",   evt.DatabaseName ?? (object)DBNull.Value);
                batch.Parameters.AddWithValue("$sid",  evt.SessionId ?? (object)DBNull.Value);
                await Task.Run(() => batch.ExecuteNonQuery(), ct);
            }
        }
        finally { _lock.Release(); }
    }

    private void CreateSchema()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS trace_events (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id      INTEGER,
                time          TEXT    NOT NULL,
                event_class   TEXT    NOT NULL,
                event_subclass TEXT,
                text          TEXT,
                partition_name TEXT,
                table_name    TEXT,
                duration_ms   INTEGER,
                cpu_ms        INTEGER,
                row_count     INTEGER,
                error_code    INTEGER,
                database_name TEXT,
                session_id    TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_trace_time
                ON trace_events(time DESC);
            """;
        cmd.ExecuteNonQuery();

        // Prune events older than 30 days
        using var prune = _db.CreateCommand();
        prune.CommandText = """
            DELETE FROM trace_events
            WHERE time < datetime('now', '-30 days');
            """;
        prune.ExecuteNonQuery();
    }

    /// <summary>Returns the most recent events, newest first.</summary>
    public async Task<IReadOnlyList<TraceEvent>> GetRecentAsync(
        string? databaseName = null,
        int limit = 500,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT event_id, time, event_class, event_subclass, text,
                       partition_name, table_name, duration_ms, cpu_ms,
                       row_count, error_code, database_name, session_id
                FROM   trace_events
                WHERE  ($db IS NULL OR database_name = $db)
                ORDER BY time DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$db",    databaseName ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$limit", limit);

            return await Task.Run(() =>
            {
                var evts = new List<TraceEvent>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    evts.Add(new TraceEvent
                    {
                        Id            = reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
                        Time          = DateTimeOffset.Parse(reader.GetString(1)),
                        EventClass    = reader.GetString(2),
                        EventSubclass = reader.IsDBNull(3) ? null : reader.GetString(3),
                        Text          = reader.IsDBNull(4) ? null : reader.GetString(4),
                        PartitionName = reader.IsDBNull(5) ? null : reader.GetString(5),
                        TableName     = reader.IsDBNull(6) ? null : reader.GetString(6),
                        DurationMs    = reader.IsDBNull(7) ? null : reader.GetInt64(7),
                        CpuMs         = reader.IsDBNull(8) ? null : reader.GetInt64(8),
                        RowCount      = reader.IsDBNull(9) ? null : reader.GetInt64(9),
                        ErrorCode     = reader.IsDBNull(10) ? null : reader.GetInt32(10),
                        DatabaseName  = reader.IsDBNull(11) ? null : reader.GetString(11),
                        SessionId     = reader.IsDBNull(12) ? null : reader.GetString(12),
                    });
                }
                return (IReadOnlyList<TraceEvent>)evts;
            }, ct);
        }
        finally { _lock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _ingest.Writer.TryComplete();
        await _processLoop;
        _db.Dispose();
        _lock.Dispose();
    }
}
