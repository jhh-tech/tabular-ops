using System.Text.Json;
using TabularOps.Core.Model;

namespace TabularOps.Core.Connection;

/// <summary>
/// Persists the list of configured connections to disk so the sidebar is
/// restored on next launch. Re-authentication is still required to actually
/// use XMLA, but the workspace list and layout are remembered.
/// </summary>
public sealed class ConnectionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;

    public ConnectionStore(string appDataDirectory)
    {
        _filePath = Path.Combine(appDataDirectory, "connections.json");
    }

    public async Task SaveAsync(IEnumerable<TenantContext> contexts, CancellationToken ct = default)
    {
        var entries = contexts.Select(c => new ConnectionEntry
        {
            DisplayName = c.DisplayName,
            ConnectionString = c.ConnectionString,
            EndpointType = c.EndpointType,
            TokenCacheFilePath = c.TokenCacheFilePath,
            CapacityName = c.CapacityName,
            CapacityRegion = c.CapacityRegion,
            CapacitySku = c.CapacitySku,
        }).ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var json = JsonSerializer.Serialize(entries, JsonOptions);
        await File.WriteAllTextAsync(_filePath, json, ct);
    }

    public async Task<IReadOnlyList<TenantContext>> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_filePath)) return [];

        try
        {
            var json = await File.ReadAllTextAsync(_filePath, ct);
            var entries = JsonSerializer.Deserialize<List<ConnectionEntry>>(json) ?? [];

            return entries.Select(e => new TenantContext
            {
                DisplayName = e.DisplayName,
                ConnectionString = e.ConnectionString,
                EndpointType = e.EndpointType,
                TokenCacheFilePath = e.TokenCacheFilePath,
                CapacityName = e.CapacityName,
                CapacityRegion = e.CapacityRegion,
                CapacitySku = e.CapacitySku,
            }).ToList();
        }
        catch (JsonException)
        {
            // Corrupted file — start fresh
            return [];
        }
    }

    private sealed class ConnectionEntry
    {
        public string DisplayName { get; set; } = string.Empty;
        public string ConnectionString { get; set; } = string.Empty;
        public EndpointType EndpointType { get; set; }
        public string? TokenCacheFilePath { get; set; }
        public string? CapacityName { get; set; }
        public string? CapacityRegion { get; set; }
        public string? CapacitySku { get; set; }
    }
}
