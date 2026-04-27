using TabularOps.Core.Refresh;

namespace TabularOps.Core.Tests;

public sealed class RefreshHistoryStoreTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    private string TempDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tabularops_history_{Guid.NewGuid():N}.db");
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            try { File.Delete(f); } catch { /* best-effort */ }
    }

    // ── LogStartAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task LogStartAsync_ReturnsPositiveId()
    {
        await using var store = new RefreshHistoryStore(TempDb());

        var id = await store.LogStartAsync("t1", "DB", "Sales", "Sales_2024");

        Assert.True(id > 0);
    }

    [Fact]
    public async Task LogStartAsync_SequentialCalls_ReturnDistinctIds()
    {
        await using var store = new RefreshHistoryStore(TempDb());

        var id1 = await store.LogStartAsync("t1", "DB", "T", "P1");
        var id2 = await store.LogStartAsync("t1", "DB", "T", "P2");

        Assert.NotEqual(id1, id2);
    }

    // ── GetRecentAsync — empty ────────────────────────────────────────────────

    [Fact]
    public async Task GetRecentAsync_ReturnsEmpty_WhenNoRunsExist()
    {
        await using var store = new RefreshHistoryStore(TempDb());

        var runs = await store.GetRecentAsync();

        Assert.Empty(runs);
    }

    // ── LogCompleteAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task LogCompleteAsync_Completed_SetsStatusAndCompletedAt()
    {
        await using var store = new RefreshHistoryStore(TempDb());
        var id = await store.LogStartAsync("t1", "DB", "Sales", "P_2024");
        await store.LogCompleteAsync(id, RefreshStatus.Completed);

        var runs = await store.GetRecentAsync("t1", "DB");

        Assert.Single(runs);
        Assert.Equal(RefreshStatus.Completed, runs[0].Status);
        Assert.NotNull(runs[0].CompletedAt);
    }

    [Fact]
    public async Task LogCompleteAsync_Failed_StoresErrorMessage()
    {
        await using var store = new RefreshHistoryStore(TempDb());
        var id = await store.LogStartAsync("t1", "DB", "Sales", "P_2024");
        await store.LogCompleteAsync(id, RefreshStatus.Failed, "Source query timed out");

        var runs = await store.GetRecentAsync("t1", "DB");

        Assert.Equal(RefreshStatus.Failed, runs[0].Status);
        Assert.Equal("Source query timed out", runs[0].ErrorMessage);
    }

    [Fact]
    public async Task LogCompleteAsync_Cancelled_SetsStatusCorrectly()
    {
        await using var store = new RefreshHistoryStore(TempDb());
        var id = await store.LogStartAsync("t1", "DB", "T", "P");
        await store.LogCompleteAsync(id, RefreshStatus.Cancelled, "User cancelled");

        var runs = await store.GetRecentAsync("t1", "DB");

        Assert.Equal(RefreshStatus.Cancelled, runs[0].Status);
    }

    // ── GetRecentAsync — filtering ────────────────────────────────────────────

    [Fact]
    public async Task GetRecentAsync_FiltersByTenantId()
    {
        await using var store = new RefreshHistoryStore(TempDb());
        await store.LogStartAsync("tenant-a", "DB", "T", "P");
        await store.LogStartAsync("tenant-b", "DB", "T", "P");

        var runs = await store.GetRecentAsync(tenantId: "tenant-a");

        Assert.Single(runs);
        Assert.Equal("tenant-a", runs[0].TenantId);
    }

    [Fact]
    public async Task GetRecentAsync_FiltersByDatabaseName()
    {
        await using var store = new RefreshHistoryStore(TempDb());
        await store.LogStartAsync("t1", "DB-Alpha", "T", "P");
        await store.LogStartAsync("t1", "DB-Beta",  "T", "P");

        var runs = await store.GetRecentAsync(databaseName: "DB-Alpha");

        Assert.Single(runs);
        Assert.Equal("DB-Alpha", runs[0].DatabaseName);
    }

    [Fact]
    public async Task GetRecentAsync_FiltersByBothTenantAndDatabase()
    {
        await using var store = new RefreshHistoryStore(TempDb());
        await store.LogStartAsync("t1", "DB1", "T", "P");
        await store.LogStartAsync("t1", "DB2", "T", "P");
        await store.LogStartAsync("t2", "DB1", "T", "P");

        var runs = await store.GetRecentAsync("t1", "DB1");

        Assert.Single(runs);
        Assert.Equal("t1",  runs[0].TenantId);
        Assert.Equal("DB1", runs[0].DatabaseName);
    }

    [Fact]
    public async Task GetRecentAsync_RespectsLimitParameter()
    {
        await using var store = new RefreshHistoryStore(TempDb());
        for (var i = 0; i < 10; i++)
            await store.LogStartAsync("t1", "DB", "T", $"P_{i}");

        var runs = await store.GetRecentAsync(limit: 3);

        Assert.Equal(3, runs.Count);
    }

    [Fact]
    public async Task GetRecentAsync_ReturnsMostRecentFirst()
    {
        await using var store = new RefreshHistoryStore(TempDb());
        await store.LogStartAsync("t1", "DB", "T", "First");
        await Task.Delay(5);
        await store.LogStartAsync("t1", "DB", "T", "Second");
        await Task.Delay(5);
        await store.LogStartAsync("t1", "DB", "T", "Third");

        var runs = await store.GetRecentAsync("t1", "DB");

        Assert.Equal("Third",  runs[0].PartitionName);
        Assert.Equal("Second", runs[1].PartitionName);
        Assert.Equal("First",  runs[2].PartitionName);
    }

    // ── ImportWorkspaceRunAsync ───────────────────────────────────────────────

    [Fact]
    public async Task ImportWorkspaceRunAsync_InsertsNewRow()
    {
        await using var store = new RefreshHistoryStore(TempDb());
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);

        await store.ImportWorkspaceRunAsync(
            "t1", "DB", "ext-id-001", "Full",
            start, DateTimeOffset.UtcNow, RefreshStatus.Completed, null);

        var runs = await store.GetRecentAsync("t1", "DB");

        Assert.Single(runs);
        Assert.Equal("Workspace", runs[0].Source);
        Assert.Equal("Full",      runs[0].RefreshType);
        Assert.Equal("*",         runs[0].TableName);
        Assert.Equal("*",         runs[0].PartitionName);
    }

    [Fact]
    public async Task ImportWorkspaceRunAsync_Upserts_WhenExternalIdAlreadyExists()
    {
        await using var store = new RefreshHistoryStore(TempDb());
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);

        // Insert as Running first
        await store.ImportWorkspaceRunAsync(
            "t1", "DB", "ext-abc", "Full",
            start, null, RefreshStatus.Running, null);

        // Re-import same external_id with completion
        await store.ImportWorkspaceRunAsync(
            "t1", "DB", "ext-abc", "Full",
            start, DateTimeOffset.UtcNow, RefreshStatus.Completed, null);

        var runs = await store.GetRecentAsync("t1", "DB");
        Assert.Single(runs); // no duplicate
        Assert.Equal(RefreshStatus.Completed, runs[0].Status);
    }

    [Fact]
    public async Task ImportWorkspaceRunAsync_CoexistsWith_AppRuns()
    {
        await using var store = new RefreshHistoryStore(TempDb());

        // App-triggered run
        var appId = await store.LogStartAsync("t1", "DB", "Sales", "P1");
        await store.LogCompleteAsync(appId, RefreshStatus.Completed);

        // Workspace run
        await store.ImportWorkspaceRunAsync(
            "t1", "DB", "ws-ext-001", "Full",
            DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow,
            RefreshStatus.Completed, null);

        var runs = await store.GetRecentAsync("t1", "DB");
        Assert.Equal(2, runs.Count);
        Assert.Contains(runs, r => r.Source == "App");
        Assert.Contains(runs, r => r.Source == "Workspace");
    }

    // ── Persistence across reopens ────────────────────────────────────────────

    [Fact]
    public async Task Data_PersistedAcrossStoreReopens()
    {
        var path = TempDb();

        await using (var store = new RefreshHistoryStore(path))
        {
            var id = await store.LogStartAsync("t1", "DB", "Sales", "P1");
            await store.LogCompleteAsync(id, RefreshStatus.Completed);
        }

        await using var store2 = new RefreshHistoryStore(path);
        var runs = await store2.GetRecentAsync("t1", "DB");

        Assert.Single(runs);
        Assert.Equal(RefreshStatus.Completed, runs[0].Status);
    }
}
