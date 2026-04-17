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

        var groups = await pbiClient.Groups.GetGroupsAsync(cancellationToken: ct);
        return groups.Value
            // Only workspaces on dedicated capacity support XMLA read-write
            .Where(g => g.IsOnDedicatedCapacity == true)
            .OrderBy(g => g.Name)
            .Select(g => new Model.WorkspaceInfo(g.Id.ToString(), g.Name))
            .ToList();
    }

    /// <summary>
    /// Registers a Power BI workspace as a tenant. Reuses the token acquired
    /// during GetWorkspacesAsync — no second browser popup.
    /// </summary>
    public async Task<TenantContext> AddPowerBiTenantAsync(
        string connectionString,
        string displayName,
        CancellationToken ct = default)
    {
        var app = await GetOrCreatePowerBiAppAsync(ct);

        var context = new TenantContext
        {
            DisplayName = displayName,
            ConnectionString = connectionString,
            EndpointType = EndpointType.PowerBi,
            TokenCacheFilePath = Path.Combine(_cacheDirectory, "powerbi", "msal.cache"),
        };

        await RegisterTenantAsync(context, app, ct);
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

    private static async Task<AuthenticationResult> AcquireTokenAsync(
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
            state.TomServer.Connect(cs);

        if (state.PollConnection is null || state.PollConnection.State != System.Data.ConnectionState.Open)
        {
            state.PollConnection?.Dispose();
            state.PollConnection = new AdomdConnection(cs);
            state.PollConnection.Open();
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
