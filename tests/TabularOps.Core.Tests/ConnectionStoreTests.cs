using TabularOps.Core.Connection;
using TabularOps.Core.Model;

namespace TabularOps.Core.Tests;

public sealed class ConnectionStoreTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    private string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tabularops_cs_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var d in _tempDirs)
            try { Directory.Delete(d, recursive: true); } catch { /* best-effort */ }
    }

    private static TenantContext PowerBiContext(string workspace = "MyWorkspace") => new()
    {
        DisplayName      = workspace,
        ConnectionString = $"powerbi://api.powerbi.com/v1.0/myorg/{workspace}",
        EndpointType     = EndpointType.PowerBi,
        TokenCacheFilePath = $@"C:\cache\{workspace}.cache",
    };

    private static TenantContext SsasContext() => new()
    {
        DisplayName      = "Local SSAS",
        ConnectionString = "Data Source=localhost\\TABULAR;Integrated Security=SSPI;",
        EndpointType     = EndpointType.Ssas,
    };

    // ── Load edge cases ───────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_ReturnsEmpty_WhenFileDoesNotExist()
    {
        var store = new ConnectionStore(TempDir());

        var result = await store.LoadAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task LoadAsync_ReturnsEmpty_WhenFileIsCorruptedJson()
    {
        var dir = TempDir();
        await File.WriteAllTextAsync(Path.Combine(dir, "connections.json"), "this is not { valid json");

        var store = new ConnectionStore(dir);
        var result = await store.LoadAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task LoadAsync_ReturnsEmpty_WhenFileContainsNull()
    {
        var dir = TempDir();
        await File.WriteAllTextAsync(Path.Combine(dir, "connections.json"), "null");

        var store = new ConnectionStore(dir);
        var result = await store.LoadAsync();

        Assert.Empty(result);
    }

    // ── Round-trip: Power BI ──────────────────────────────────────────────────

    [Fact]
    public async Task SaveAndLoad_PowerBiContext_PreservesAllCoreFields()
    {
        var store = new ConnectionStore(TempDir());
        var ctx   = PowerBiContext("SalesWorkspace");

        await store.SaveAsync([ctx]);
        var loaded = await store.LoadAsync();

        Assert.Single(loaded);
        Assert.Equal(ctx.DisplayName,        loaded[0].DisplayName);
        Assert.Equal(ctx.ConnectionString,   loaded[0].ConnectionString);
        Assert.Equal(ctx.EndpointType,       loaded[0].EndpointType);
        Assert.Equal(ctx.TokenCacheFilePath, loaded[0].TokenCacheFilePath);
    }

    // ── Round-trip: SSAS ──────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAndLoad_SsasContext_PreservesEndpointType()
    {
        var store = new ConnectionStore(TempDir());

        await store.SaveAsync([SsasContext()]);
        var loaded = await store.LoadAsync();

        Assert.Single(loaded);
        Assert.Equal(EndpointType.Ssas, loaded[0].EndpointType);
        Assert.Null(loaded[0].TokenCacheFilePath); // SSAS uses Windows auth, no token cache
    }

    // ── Capacity info ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAndLoad_PreservesCapacityInfo()
    {
        var store = new ConnectionStore(TempDir());
        var ctx = new TenantContext
        {
            DisplayName      = "Fabric WS",
            ConnectionString = "powerbi://api.powerbi.com/v1.0/myorg/FabricWS",
            EndpointType     = EndpointType.PowerBi,
            CapacityName     = "My Premium Capacity",
            CapacityRegion   = "West Europe",
            CapacitySku      = "F4",
        };

        await store.SaveAsync([ctx]);
        var loaded = await store.LoadAsync();

        Assert.Equal("My Premium Capacity", loaded[0].CapacityName);
        Assert.Equal("West Europe",         loaded[0].CapacityRegion);
        Assert.Equal("F4",                  loaded[0].CapacitySku);
    }

    [Fact]
    public async Task SaveAndLoad_NullCapacityInfo_RemainsNull()
    {
        var store = new ConnectionStore(TempDir());
        await store.SaveAsync([PowerBiContext()]);
        var loaded = await store.LoadAsync();

        Assert.Null(loaded[0].CapacityName);
        Assert.Null(loaded[0].CapacityRegion);
        Assert.Null(loaded[0].CapacitySku);
    }

    // ── Multiple contexts ─────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAndLoad_MultipleContexts_PreservesCount()
    {
        var store = new ConnectionStore(TempDir());

        await store.SaveAsync([PowerBiContext("WS-A"), PowerBiContext("WS-B"), SsasContext()]);
        var loaded = await store.LoadAsync();

        Assert.Equal(3, loaded.Count);
    }

    [Fact]
    public async Task SaveAndLoad_MultipleContexts_PreservesOrder()
    {
        var store = new ConnectionStore(TempDir());
        var contexts = new[]
        {
            PowerBiContext("Alpha"),
            PowerBiContext("Beta"),
            PowerBiContext("Gamma"),
        };

        await store.SaveAsync(contexts);
        var loaded = await store.LoadAsync();

        Assert.Equal("Alpha", loaded[0].DisplayName);
        Assert.Equal("Beta",  loaded[1].DisplayName);
        Assert.Equal("Gamma", loaded[2].DisplayName);
    }

    // ── Overwrite ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAsync_Overwrites_PreviousSave()
    {
        var dir = TempDir();
        var store = new ConnectionStore(dir);

        await store.SaveAsync([PowerBiContext("Old")]);
        await store.SaveAsync([PowerBiContext("New")]);

        var loaded = await store.LoadAsync();
        Assert.Single(loaded);
        Assert.Equal("New", loaded[0].DisplayName);
    }

    [Fact]
    public async Task SaveAsync_EmptyList_ClearsFile()
    {
        var dir = TempDir();
        var store = new ConnectionStore(dir);

        await store.SaveAsync([PowerBiContext()]);
        await store.SaveAsync([]);

        var loaded = await store.LoadAsync();
        Assert.Empty(loaded);
    }
}
