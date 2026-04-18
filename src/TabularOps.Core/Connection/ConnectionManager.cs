using Microsoft.AnalysisServices;
using Microsoft.AnalysisServices.AdomdClient;
using Microsoft.Identity.Client;
using Microsoft.PowerBI.Api;
using Microsoft.Rest;
using TabularOps.Core.Model;

namespace TabularOps.Core.Connection;

/// <summary>
/// Manages three distinct connections per tenant (ADR-002):
///   1. TOM Server — write operations (RequestRefresh, SaveChanges, schema reads)
///   2. AdomdConnection — background DMV polling (DISCOVER_SESSIONS, DISCOVER_MEMORYUSAGE)
///   3. Trace Server — owned by TraceCollector; obtained via GetTraceConnectionAsync
///
/// A single shared MSAL app handles all Power BI authentication. The token is
/// per-user, not per-workspace, so one login covers all workspaces in a tenant.
///
/// Only the active tenant polls. Others are dormant.
/// Dispose to close all connections and release MSAL state.
/// </summary>
public sealed class ConnectionManager : IAsyncDisposable
{
    private static readonly string[] PowerBiScopes =
        ["https://analysis.windows.net/powerbi/api/.default"];

    private const string PowerBiAuthority =
        "https://login.microsoftonline.com/organizations";

    private readonly string _clientId;
    private readonly string _cacheDirectory;

    // Shared across all Power BI workspaces — one login covers all of them
    private IPublicClientApplication? _powerBiMsalApp;
    private readonly SemaphoreSlim _msalInitLock = new(1, 1);

    private readonly Dictionary<string, TenantState> _tenants = new();
    // Cached catalog-scoped TOM servers — keyed by (tenantId, databaseName).
    // Reused across loads so Reload doesn't re-open a new connection.
    private readonly Dictionary<(string, string), Server> _catalogServers = new();
    private string? _activeTenantId;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ConnectionManager(string cacheDirectory, string clientId)
    {
        _cacheDirectory = cacheDirectory;
        _clientId = clientId;
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    public IReadOnlyList<TenantContext> Tenants =>
        _tenants.Values.Select(s => s.Context).ToList();

    /// <summary>
    /// Creates a PowerBIClient authenticated with the Power BI MSAL token.
    /// Caller owns the lifetime and must dispose it.
    /// </summary>
    public async Task<PowerBIClient> CreatePowerBiClientAsync(CancellationToken ct = default)
    {
        var app = await GetOrCreatePowerBiAppAsync(ct);
        var token = await AcquireTokenAsync(app, ct);
        return new PowerBIClient(new TokenCredentials(token.AccessToken, "Bearer"));
    }

    /// <summary>
    /// Authenticates interactively (once) and returns all Power BI workspaces
    /// the user has access to. Subsequent calls return silently from token cache.
    /// </summary>
    public async Task<IReadOnlyList<Model.WorkspaceInfo>> GetWorkspacesAsync(
        CancellationToken ct = default)
    {
        var app = await GetOrCreatePowerBiAppAsync(ct);
        var token = await AcquireTokenAsync(app, ct);

        var credentials = new TokenCredentials(token.AccessToken, "Bearer");
        using var pbiClient = new PowerBIClient(credentials);

        // Fetch capacity details — keyed by capacity ID.
        // Returns only capacities where the user is admin; others are left null.
        var capacityMap = new Dictionary<Guid, Microsoft.PowerBI.Api.Models.Capacity>();
        try
        {
            var caps = await pbiClient.Capacities.GetCapacitiesAsync(cancellationToken: ct);
            foreach (var c in caps.Value)
                capacityMap[c.Id] = c;
        }
        catch { /* not a capacity admin, or API unavailable — capacity info stays null */ }

        var groups = await pbiClient.Groups.GetGroupsAsync(cancellationToken: ct);
        return groups.Value
            // Only workspaces on dedicated capacity support XMLA read-write
            .Where(g => g.IsOnDedicatedCapacity == true)
            .OrderBy(g => g.Name)
            .Select(g =>
            {
                Microsoft.PowerBI.Api.Models.Capacity? cap = null;
                if (g.CapacityId.HasValue)
                    capacityMap.TryGetValue(g.CapacityId.Value, out cap);

                return new Model.WorkspaceInfo(
                    Id:            g.Id.ToString(),
                    Name:          g.Name,
                    CapacityId:    g.CapacityId?.ToString(),
                    CapacityName:  cap?.DisplayName,
                    CapacityRegion: cap?.Region,
                    CapacitySku:   cap?.Sku);
            })
            .ToList();
    }

    /// <summary>
    /// Registers a Power BI workspace as a tenant. Reuses the token acquired
    /// during GetWorkspacesAsync — no second browser popup.
    /// </summary>
    public async Task<TenantContext> AddPowerBiTenantAsync(
        string connectionString,
        string displayName,
        Model.WorkspaceInfo? workspaceInfo = null,
        CancellationToken ct = default)
    {
        var app = await GetOrCreatePowerBiAppAsync(ct);

        var context = new TenantContext
        {
            DisplayName = displayName,
            ConnectionString = connectionString,
            EndpointType = EndpointType.PowerBi,
            TokenCacheFilePath = Path.Combine(_cacheDirectory, "powerbi", "msal.cache"),
            CapacityName   = workspaceInfo?.CapacityName,
            CapacityRegion = workspaceInfo?.CapacityRegion,
            CapacitySku    = workspaceInfo?.CapacitySku,
        };

        await RegisterTenantAsync(context, app, ct);

        // Populate UPN from the cached MSAL account so the sidebar can show which
        // Entra account this workspace belongs to.
        var accounts = await app.GetAccountsAsync();
        context.UserPrincipalName = accounts.FirstOrDefault()?.Username;

        return context;
    }

    /// <summary>
    /// Registers an SSAS/AAS tenant using Windows auth or an embedded connection string.
    /// </summary>
    public async Task<TenantContext> AddSsasTenantAsync(
        string connectionString,
        string displayName,
        CancellationToken ct = default)
    {
        var context = new TenantContext
        {
            DisplayName = displayName,
            ConnectionString = connectionString,
            EndpointType = connectionString.StartsWith("asazure://", StringComparison.OrdinalIgnoreCase)
                ? EndpointType.Aas
                : EndpointType.Ssas,
            TokenCacheFilePath = null,
        };

        await RegisterTenantAsync(context, msalApp: null, ct);
        return context;
    }

    /// <summary>
    /// Sets the active tenant. Starts DMV polling for this tenant; suspends it
    /// for the previously active one.
    /// </summary>
    public async Task SetActiveTenantAsync(string tenantId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_activeTenantId == tenantId) return;

            if (_activeTenantId is not null && _tenants.TryGetValue(_activeTenantId, out var old))
                old.StopPolling();

            _activeTenantId = tenantId;

            if (_tenants.TryGetValue(tenantId, out var next))
                next.StartPolling();
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Returns the TOM Server for write operations. Never share with polling or trace.
    /// </summary>
    public async Task<Server> GetTomServerAsync(string tenantId, CancellationToken ct = default)
    {
        var state = GetState(tenantId);
        await EnsureConnectedAsync(state, ct);
        return state.TomServer!;
    }

    /// <summary>
    /// Returns a TOM Server scoped to a specific database via <c>Initial Catalog</c>,
    /// creating and caching it on first call and reusing it on subsequent calls.
    /// Required for Power BI XMLA — model-level access fails without a catalog.
    /// ConnectionManager owns the lifetime; do NOT dispose the returned Server.
    /// </summary>
    public async Task<Server> GetOrCreateCatalogServerAsync(
        string tenantId, string databaseName, CancellationToken ct = default)
    {
        var key = (tenantId, databaseName);

        await _lock.WaitAsync(ct);
        try
        {
            if (_catalogServers.TryGetValue(key, out var cached) && cached.Connected)
                return cached;

            var state = GetState(tenantId);
            var cs = await BuildConnectionStringAsync(state, ct);
            cs = AppendCatalog(cs, databaseName);

            var server = new Server();
            await Task.Run(() => server.Connect(cs), ct);

            // Dispose stale server before replacing
            if (_catalogServers.TryGetValue(key, out var old))
            {
                old.Disconnect();
                old.Dispose();
            }
            _catalogServers[key] = server;
            return server;
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Executes a DMV or DAX query and returns the results as a list of rows.
    /// When <paramref name="catalogName"/> is provided a dedicated connection with
    /// <c>Initial Catalog</c> is opened (required for DISCOVER_* DMVs on Power BI
    /// XMLA endpoints that don't accept queries without a current catalog).
    /// </summary>
    public async Task<List<Dictionary<string, object?>>> ExecuteDmvAsync(
        string tenantId,
        string query,
        CancellationToken ct = default,
        string? catalogName = null)
    {
        var state = GetState(tenantId);
        await EnsureConnectedAsync(state, ct);

        // Power BI XMLA requires Initial Catalog for DMV queries — open a short-lived
        // dedicated connection with the catalog set rather than mutating the shared one.
        AdomdConnection connection;
        bool ownConnection = catalogName is not null;

        if (ownConnection)
        {
            var cs = await BuildConnectionStringAsync(state, ct);
            cs = AppendCatalog(cs, catalogName!);
            connection = new AdomdConnection(cs);
            await Task.Run(() => connection.Open(), ct);
        }
        else
        {
            connection = state.PollConnection!;
        }

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = query;

            using var reader = cmd.ExecuteReader();
            var results = new List<Dictionary<string, object?>>();

            while (reader.Read())
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                results.Add(row);
            }

            return results;
        }
        finally
        {
            if (ownConnection) connection.Dispose();
        }
    }

    private static string AppendCatalog(string connectionString, string catalogName) =>
        connectionString.TrimEnd(';') + $";Initial Catalog={catalogName}";

    /// <summary>
    /// Opens a fresh dedicated ADOMD connection for trace collection.
    /// Caller (TraceCollector) owns the lifetime and must dispose.
    /// </summary>
    public async Task<AdomdConnection> GetTraceConnectionAsync(
        string tenantId,
        CancellationToken ct = default)
    {
        var state = GetState(tenantId);
        var cs = await BuildConnectionStringAsync(state, ct);
        var conn = new AdomdConnection(cs);
        conn.Open();
        return conn;
    }

    // -------------------------------------------------------------------------
    // Internal
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the shared Power BI MSAL app, creating and registering the token
    /// cache on first call.
    /// </summary>
    private async Task<IPublicClientApplication> GetOrCreatePowerBiAppAsync(CancellationToken ct)
    {
        if (_powerBiMsalApp is not null) return _powerBiMsalApp;

        await _msalInitLock.WaitAsync(ct);
        try
        {
            if (_powerBiMsalApp is not null) return _powerBiMsalApp;

            var cacheDir = Path.Combine(_cacheDirectory, "powerbi");
            var app = PublicClientApplicationBuilder
                .Create(_clientId)
                .WithAuthority(PowerBiAuthority)
                .WithDefaultRedirectUri()
                .Build();

            await MsalTokenCache.RegisterAsync(app, cacheDir);
            _powerBiMsalApp = app;
        }
        finally { _msalInitLock.Release(); }

        return _powerBiMsalApp;
    }

    /// <summary>
    /// Optional override for interactive token acquisition. Set by the Desktop project
    /// so that auth dialogs open on the UI thread with a proper window handle (required
    /// for Windows broker / WAM on Windows 10/11). When null, falls back to calling
    /// AcquireTokenInteractive directly (works for initial login from the UI thread).
    /// </summary>
    public Func<IPublicClientApplication, string[], Task<AuthenticationResult>>? InteractiveAuthProvider { get; set; }

    private async Task<AuthenticationResult> AcquireTokenAsync(
        IPublicClientApplication app,
        CancellationToken ct)
    {
        try
        {
            var accounts = await app.GetAccountsAsync();
            return await app
                .AcquireTokenSilent(PowerBiScopes, accounts.FirstOrDefault())
                .ExecuteAsync(ct);
        }
        catch (MsalUiRequiredException)
        {
            // Interactive auth needs a window handle in WPF. If the Desktop project
            // registered an InteractiveAuthProvider, use it (runs on UI dispatcher).
            if (InteractiveAuthProvider is not null)
                return await InteractiveAuthProvider(app, PowerBiScopes);

            return await app
                .AcquireTokenInteractive(PowerBiScopes)
                .WithPrompt(Prompt.SelectAccount)
                .ExecuteAsync(ct);
        }
    }

    private async Task RegisterTenantAsync(
        TenantContext context,
        IPublicClientApplication? msalApp,
        CancellationToken ct)
    {
        var state = new TenantState(context, msalApp);

        await _lock.WaitAsync(ct);
        try { _tenants[context.TenantId] = state; }
        finally { _lock.Release(); }

        await EnsureConnectedAsync(state, ct);
    }

    private async Task EnsureConnectedAsync(TenantState state, CancellationToken ct)
    {
        if (state.TomServer?.Connected == true) return;

        var cs = await BuildConnectionStringAsync(state, ct);

        state.TomServer ??= new Server();
        if (!state.TomServer.Connected)
            await Task.Run(() => state.TomServer.Connect(cs), ct);

        if (state.PollConnection is null || state.PollConnection.State != System.Data.ConnectionState.Open)
        {
            state.PollConnection?.Dispose();
            state.PollConnection = new AdomdConnection(cs);
            await Task.Run(() => state.PollConnection.Open(), ct);
        }

        state.ConnectionError = null;
    }

    private async Task<string> BuildConnectionStringAsync(TenantState state, CancellationToken ct)
    {
        if (state.MsalApp is null)
            return state.Context.ConnectionString;

        var token = await AcquireTokenAsync(state.MsalApp, ct);
        return $"Data Source={state.Context.ConnectionString};Password={token.AccessToken}";
    }

    /// <summary>
    /// Disconnects and removes a tenant. Safe to call even if the tenant is active.
    /// </summary>
    public async Task RemoveTenantAsync(string tenantId)
    {
        await _lock.WaitAsync();
        try
        {
            if (!_tenants.TryGetValue(tenantId, out var state)) return;
            _tenants.Remove(tenantId);
            if (_activeTenantId == tenantId) _activeTenantId = null;
            await state.DisposeAsync();

            // Dispose any catalog-scoped servers for this tenant
            var catalogKeys = _catalogServers.Keys
                .Where(k => k.Item1 == tenantId).ToList();
            foreach (var key in catalogKeys)
            {
                _catalogServers[key].Disconnect();
                _catalogServers[key].Dispose();
                _catalogServers.Remove(key);
            }
        }
        finally { _lock.Release(); }
    }

    private TenantState GetState(string tenantId)
    {
        if (!_tenants.TryGetValue(tenantId, out var state))
            throw new InvalidOperationException($"Tenant '{tenantId}' is not registered.");
        return state;
    }

    private static string SanitizeName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    public async ValueTask DisposeAsync()
    {
        foreach (var state in _tenants.Values)
            await state.DisposeAsync();
        _tenants.Clear();

        foreach (var server in _catalogServers.Values)
        {
            server.Disconnect();
            server.Dispose();
        }
        _catalogServers.Clear();

        _lock.Dispose();
        _msalInitLock.Dispose();
    }

    // -------------------------------------------------------------------------
    // Per-tenant state bag
    // -------------------------------------------------------------------------

    private sealed class TenantState(
        TenantContext context,
        IPublicClientApplication? msalApp) : IAsyncDisposable
    {
        public TenantContext Context { get; } = context;
        public IPublicClientApplication? MsalApp { get; } = msalApp;

        public Server? TomServer { get; set; }
        public AdomdConnection? PollConnection { get; set; }
        public string? ConnectionError { get; set; }

        private CancellationTokenSource? _pollCts;

        public void StartPolling()
        {
            if (_pollCts is not null) return;
            _pollCts = new CancellationTokenSource();
            _ = PollLoopAsync(_pollCts.Token);
        }

        public void StopPolling()
        {
            _pollCts?.Cancel();
            _pollCts = null;
        }

        private async Task PollLoopAsync(CancellationToken ct)
        {
            // Placeholder — DMV polling implemented in Milestone 2
            while (!ct.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            StopPolling();
            TomServer?.Disconnect();
            TomServer?.Dispose();
            PollConnection?.Close();
            PollConnection?.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
