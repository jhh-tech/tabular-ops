using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.AnalysisServices;
using TabularOps.Core.Connection;

namespace TabularOps.Core.Tracing;

/// <summary>
/// Captures Profiler-style trace events from a tabular model via XMLA
/// and streams them through a <see cref="Channel{T}"/>.
///
/// Uses TOM SessionTrace (ADR-006: classic Trace API) on the catalog-scoped
/// server connection — the same pattern as Server.SessionTrace in AMO.
/// Session traces only capture events from the current session/connection,
/// which is sufficient to observe refresh events on both SSAS and Power BI XMLA.
///
/// Events are dispatched via ITrace.OnEvent using the single TraceEventHandler
/// delegate — TraceEventArgs carries EventClass so a single handler dispatches
/// to all event types.
///
/// Channel drainage is always active regardless of whether the UI is subscribed —
/// events are written to SQLite so nothing is ever lost on tab switches.
/// </summary>
public sealed class TraceCollector : IAsyncDisposable
{
    private readonly ConnectionManager _connectionManager;
    private readonly string _tenantId;
    private readonly string _databaseName;

    private readonly Channel<TraceEvent> _channel;
    private readonly ChannelWriter<TraceEvent> _writer;

    private bool _stopped;
    private CancellationTokenSource? _cts;
    private ITrace? _activeTrace;
    private long _lastId;

    public TraceCollector(
        ConnectionManager connectionManager,
        string tenantId,
        string databaseName)
    {
        _connectionManager = connectionManager;
        _tenantId = tenantId;
        _databaseName = databaseName;

        _channel = Channel.CreateBounded<TraceEvent>(new BoundedChannelOptions(5_000)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _writer = _channel.Writer;
    }

    /// <summary>
    /// Streams trace events as they arrive. Completes when <see cref="StopAsync"/> is called.
    /// </summary>
    public IAsyncEnumerable<TraceEvent> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
        => _channel.Reader.ReadAllAsync(ct);

    /// <summary>
    /// Starts the SessionTrace on the catalog-scoped TOM server.
    /// Uses the cached catalog server from ConnectionManager so model switching
    /// does not require reconnecting.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var server = await _connectionManager.GetOrCreateCatalogServerAsync(
            _tenantId, _databaseName, ct);

        var sessionTrace = server.SessionTrace;
        sessionTrace.OnEvent += OnEvent;
        sessionTrace.Stopped += OnStopped;

        await Task.Run(() => sessionTrace.Start(), ct);
        _activeTrace = sessionTrace;
    }

    private void OnEvent(object sender, TraceEventArgs e)
    {
        if (_stopped) return;
        var evt = MapEvent(e);
        if (evt is not null)
            _writer.TryWrite(evt);
    }

    private void OnStopped(ITrace sender, TraceStoppedEventArgs e)
    {
        _writer.TryComplete();
    }

    /// <summary>Stops the trace and drains remaining events.</summary>
    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;

        _cts?.Cancel();

        if (_activeTrace is not null)
        {
            _activeTrace.OnEvent -= OnEvent;
            if (_activeTrace is SessionTrace st)
                st.Stopped -= OnStopped;
            try { await Task.Run(() => _activeTrace.Stop()); }
            catch { /* best-effort */ }
            _activeTrace = null;
        }

        _channel.Writer.TryComplete();
        await foreach (var _ in ReadAllAsync(CancellationToken.None)) { }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    // -------------------------------------------------------------------------
    // Event mapping
    // -------------------------------------------------------------------------

    private TraceEvent? MapEvent(TraceEventArgs e)
    {
        var id = Interlocked.Increment(ref _lastId);
        var now = DateTimeOffset.UtcNow;

        var ecName = EventClassName(e.EventClass);

        string? partitionName = null;
        string? tableName = null;

        if (e.EventClass >= TraceEventClass.ProgressReportBegin
            && e.EventClass <= TraceEventClass.ProgressReportError
            && !string.IsNullOrEmpty(e.ObjectName))
        {
            partitionName = e.ObjectName;
            var path = e.ObjectPath;
            if (!string.IsNullOrEmpty(path))
            {
                var segments = path.Split(',');
                if (segments.Length >= 2)
                    tableName = segments[^2].Trim();
            }
        }

        return new TraceEvent
        {
            Id            = id,
            Time          = now,
            EventClass    = ecName,
            EventSubclass = e.EventSubclass > 0 ? e.EventSubclass.ToString() : null,
            Text          = e.TextData,
            PartitionName = partitionName,
            TableName     = tableName,
            DurationMs    = e.Duration,
            CpuMs         = e.CpuTime,
            RowCount      = e.IntegerData,
            ErrorCode     = null, // Error code not available on TraceEventArgs in this library version
            DatabaseName  = e.DatabaseName ?? _databaseName,
            SessionId     = e.SessionID,
        };
    }

    private static string EventClassName(TraceEventClass ec) => ec switch
    {
        TraceEventClass.ProgressReportBegin    => "Progress / Begin",
        TraceEventClass.ProgressReportEnd       => "Progress / End",
        TraceEventClass.ProgressReportCurrent    => "Progress / Current",
        TraceEventClass.ProgressReportError     => "Progress / Error",
        TraceEventClass.QueryBegin              => "Query Begin",
        TraceEventClass.QueryEnd                => "Query End",
        TraceEventClass.QuerySubcube           => "Query Subcube",
        TraceEventClass.CommandBegin           => "Command / Begin",
        TraceEventClass.CommandEnd             => "Command / End",
        TraceEventClass.Error                  => "Error",
        TraceEventClass.LockAcquired           => "Lock Acquired",
        TraceEventClass.LockReleased           => "Lock Released",
        TraceEventClass.LockWaiting            => "Lock Waiting",
        TraceEventClass.VertiPaqSEQueryBegin   => "VertiPaq / Query Begin",
        TraceEventClass.VertiPaqSEQueryEnd     => "VertiPaq / Query End",
        TraceEventClass.FileLoadBegin         => "File Load / Begin",
        TraceEventClass.FileLoadEnd            => "File Load / End",
        _ => $"Event_{(int)ec}",
    };
}
