using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TabularOps.Core.Connection;
using TabularOps.Core.Model;

namespace TabularOps.Core.Refresh;

/// <summary>
/// Refreshes Power BI datasets via the Enhanced Refresh REST API.
///
/// Advantages over TOM SaveChanges:
///   - Genuinely async — the server processes independently; we just poll.
///   - Cancel works: DELETE /refreshes/{requestId} stops the job server-side.
///   - Retry and maxParallelism configuration.
///   - Works on Fabric/Premium workspaces with read-write XMLA.
///
/// Docs: https://learn.microsoft.com/en-us/power-bi/connect-data/asynchronous-refresh
/// </summary>
public sealed class PowerBiRefreshEngine
{
    private const string BaseUrl = "https://api.powerbi.com/v1.0/myorg";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(6);
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ConnectionManager _connectionManager;
    private readonly RefreshHistoryStore _historyStore;

    public PowerBiRefreshEngine(ConnectionManager connectionManager, RefreshHistoryStore historyStore)
    {
        _connectionManager = connectionManager;
        _historyStore      = historyStore;
    }

    // ── Partition-level refresh ───────────────────────────────────────────────

    public async Task<IReadOnlyList<RefreshRun>> RefreshAsync(
        ModelRef model,
        IReadOnlyList<(string TableName, string PartitionName)> partitions,
        RefreshMode mode = RefreshMode.Full,
        IProgress<RefreshProgressEvent>? progress = null,
        CancellationToken ct = default)
    {
        var (groupId, datasetId) = ResolveIds(model);

        var runIds = new Dictionary<(string, string), long>(partitions.Count);
        foreach (var (tbl, part) in partitions)
        {
            var id = await _historyStore.LogStartAsync(model.TenantId, model.DatabaseName, tbl, part, ct);
            runIds[(tbl, part)] = id;
            progress?.Report(new RefreshProgressEvent(tbl, part, RefreshStatus.Running, null));
        }

        var objects = partitions
            .Select(p => new RefreshObject(p.TableName, p.PartitionName))
            .ToList();

        var (status, error) = await ExecuteAndPollAsync(groupId, datasetId, mode, objects, ct);

        return await WriteHistoryAsync(model, partitions, runIds, status, error, progress);
    }

    // ── Whole-model refresh ───────────────────────────────────────────────────

    public async Task RefreshModelAsync(
        ModelRef model,
        RefreshMode mode = RefreshMode.Full,
        CancellationToken ct = default)
    {
        var (groupId, datasetId) = ResolveIds(model);
        var (status, error) = await ExecuteAndPollAsync(groupId, datasetId, mode, objects: null, ct);

        if (status == RefreshStatus.Failed && error is not null)
            throw new InvalidOperationException(error);
        if (status == RefreshStatus.Cancelled)
            throw new OperationCanceledException("Refresh was cancelled.");
    }

    // ── Core: POST → poll → optional DELETE ──────────────────────────────────

    private async Task<(RefreshStatus Status, string? Error)> ExecuteAndPollAsync(
        string groupId,
        string datasetId,
        RefreshMode mode,
        List<RefreshObject>? objects,
        CancellationToken ct)
    {
        var token     = await _connectionManager.GetPowerBiAccessTokenAsync(ct);
        using var http = BuildClient(token);

        // POST the refresh request
        var body = new EnhancedRefreshRequest(
            Type:           MapMode(mode),
            CommitMode:     "transactional",
            MaxParallelism: 10,
            RetryCount:     0,
            Objects:        objects);

        var postUrl  = $"{BaseUrl}/groups/{groupId}/datasets/{datasetId}/refreshes";
        var response = await http.PostAsJsonAsync(postUrl, body, JsonOpts, ct);
        response.EnsureSuccessStatusCode();

        // Extract requestId from the Location header
        var location  = response.Headers.Location?.ToString()
            ?? throw new InvalidOperationException("Power BI Enhanced Refresh response had no Location header.");
        var requestId = location.Split('/').Last();

        // Poll until the server reports a terminal state
        var pollUrl = $"{BaseUrl}/groups/{groupId}/datasets/{datasetId}/refreshes/{requestId}";

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, ct);

                var statusResp = await http.GetAsync(pollUrl, ct);
                statusResp.EnsureSuccessStatusCode();

                var dto = await statusResp.Content.ReadFromJsonAsync<RefreshStatusDto>(JsonOpts, ct);
                switch (dto?.Status)
                {
                    case "Completed":
                        return (RefreshStatus.Completed, null);
                    case "Failed":
                        var err = dto.ServiceExceptionJson ?? "Refresh failed with an unknown error.";
                        return (RefreshStatus.Failed, err);
                    case "Cancelled":
                        return (RefreshStatus.Cancelled, null);
                    // "Unknown" / "Disabled" / null → still running, keep polling
                }
            }
            return (RefreshStatus.Cancelled, null);
        }
        catch (OperationCanceledException)
        {
            // Best-effort cancel on the server: use a fresh token (original may be timed-out)
            _ = TryCancelOnServerAsync(groupId, datasetId, requestId);
            return (RefreshStatus.Cancelled, null);
        }
        catch (Exception ex)
        {
            return (RefreshStatus.Failed, ex.Message);
        }
    }

    private async Task TryCancelOnServerAsync(string groupId, string datasetId, string requestId)
    {
        try
        {
            var token = await _connectionManager.GetPowerBiAccessTokenAsync(default);
            using var http = BuildClient(token);
            await http.DeleteAsync(
                $"{BaseUrl}/groups/{groupId}/datasets/{datasetId}/refreshes/{requestId}");
        }
        catch { /* best-effort — ignore failures */ }
    }

    // ── History ───────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<RefreshRun>> WriteHistoryAsync(
        ModelRef model,
        IReadOnlyList<(string TableName, string PartitionName)> partitions,
        Dictionary<(string, string), long> runIds,
        RefreshStatus status,
        string? error,
        IProgress<RefreshProgressEvent>? progress)
    {
        var runs = new List<RefreshRun>(partitions.Count);
        var now  = DateTimeOffset.UtcNow;

        foreach (var (tbl, part) in partitions)
        {
            var runId = runIds[(tbl, part)];
            await _historyStore.LogCompleteAsync(runId, status, error, default);
            progress?.Report(new RefreshProgressEvent(tbl, part, status, error));
            runs.Add(new RefreshRun(runId, model.TenantId, model.DatabaseName, tbl, part,
                now, now, status, error));
        }

        if (status == RefreshStatus.Failed && error is not null)
            throw new InvalidOperationException(error);

        return runs;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (string GroupId, string DatasetId) ResolveIds(ModelRef model)
    {
        var groupId = model.WorkspaceGuid
            ?? throw new InvalidOperationException(
                "WorkspaceGuid is required for Power BI Enhanced Refresh. " +
                "Re-add the workspace connection to populate it.");

        // TOM db.ID for Power BI may include curly braces: {xxxxxxxx-...}
        var datasetId = model.DatabaseId.Trim('{', '}');
        return (groupId, datasetId);
    }

    private static HttpClient BuildClient(string bearerToken)
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
        return http;
    }

    private static string MapMode(RefreshMode mode) => mode switch
    {
        RefreshMode.Full        => "full",
        RefreshMode.DataOnly    => "dataOnly",
        RefreshMode.Calculate   => "calculate",
        RefreshMode.ClearValues => "clearValues",
        _                       => "full",  // Automatic → full for Power BI (no equivalent)
    };

    // ── DTOs ─────────────────────────────────────────────────────────────────

    private sealed record EnhancedRefreshRequest(
        string Type,
        string CommitMode,
        int MaxParallelism,
        int RetryCount,
        [property: JsonPropertyName("objects")]
        List<RefreshObject>? Objects);

    private sealed record RefreshObject(
        [property: JsonPropertyName("table")]
        string Table,
        [property: JsonPropertyName("partition")]
        string Partition);

    private sealed class RefreshStatusDto
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        /// <summary>
        /// Serialised exception JSON from the Power BI service when the refresh fails.
        /// Contains the human-readable error message nested inside.
        /// </summary>
        [JsonPropertyName("serviceExceptionJson")]
        public string? ServiceExceptionJson { get; set; }
    }
}
