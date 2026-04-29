using TabularOps.Core.Refresh;

namespace TabularOps.Core.Tests;

public sealed class BackupStoreTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    private string TempDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tabularops_backup_{Guid.NewGuid():N}.db");
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
        await using var store = new BackupStore(TempDb());

        var id = await store.LogStartAsync("tenant1", "Sales", "sales_20240101.abf");

        Assert.True(id > 0);
    }

    [Fact]
    public async Task LogStartAsync_SequentialCalls_ReturnDistinctIds()
    {
        await using var store = new BackupStore(TempDb());

        var id1 = await store.LogStartAsync("t1", "DB", "a.abf");
        var id2 = await store.LogStartAsync("t1", "DB", "b.abf");

        Assert.NotEqual(id1, id2);
    }

    // ── GetLastBackupAsync — no data ──────────────────────────────────────────

    [Fact]
    public async Task GetLastBackupAsync_ReturnsNull_WhenNoRunsExist()
    {
        await using var store = new BackupStore(TempDb());

        var result = await store.GetLastBackupAsync("tenant1", "Sales");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetLastBackupAsync_ReturnsNull_WhenOnlyFailedRunExists()
    {
        await using var store = new BackupStore(TempDb());
        var id = await store.LogStartAsync("t1", "Sales", "sales.abf");
        await store.LogCompleteAsync(id, succeeded: false, errorMessage: "disk full");

        var result = await store.GetLastBackupAsync("t1", "Sales");

        Assert.Null(result);
    }

    // ── GetLastBackupAsync — successful run ───────────────────────────────────

    [Fact]
    public async Task GetLastBackupAsync_ReturnsRun_AfterSuccessfulBackup()
    {
        await using var store = new BackupStore(TempDb());
        var id = await store.LogStartAsync("t1", "AdventureWorks", "aw_20240101.abf");
        await store.LogCompleteAsync(id, succeeded: true, errorMessage: null);

        var result = await store.GetLastBackupAsync("t1", "AdventureWorks");

        Assert.NotNull(result);
        Assert.Equal("t1",              result.TenantId);
        Assert.Equal("AdventureWorks",  result.DatabaseName);
        Assert.Equal("aw_20240101.abf", result.FileName);
        Assert.True(result.Succeeded);
        Assert.Null(result.ErrorMessage);
        Assert.NotNull(result.CompletedAt);
    }

    [Fact]
    public async Task GetLastBackupAsync_ReturnsLatest_WhenMultipleSucceeded()
    {
        await using var store = new BackupStore(TempDb());

        var id1 = await store.LogStartAsync("t1", "DB", "db_first.abf");
        await store.LogCompleteAsync(id1, succeeded: true, null);

        await Task.Delay(5); // ensure distinct completed_at timestamps

        var id2 = await store.LogStartAsync("t1", "DB", "db_second.abf");
        await store.LogCompleteAsync(id2, succeeded: true, null);

        var result = await store.GetLastBackupAsync("t1", "DB");

        Assert.Equal("db_second.abf", result!.FileName);
    }

    // ── GetLastBackupAsync — tenant/database filtering ────────────────────────

    [Fact]
    public async Task GetLastBackupAsync_FiltersByTenantId()
    {
        await using var store = new BackupStore(TempDb());

        var id1 = await store.LogStartAsync("tenant-a", "DB", "a.abf");
        await store.LogCompleteAsync(id1, succeeded: true, null);

        var id2 = await store.LogStartAsync("tenant-b", "DB", "b.abf");
        await store.LogCompleteAsync(id2, succeeded: true, null);

        Assert.NotNull(await store.GetLastBackupAsync("tenant-a", "DB"));
        Assert.NotNull(await store.GetLastBackupAsync("tenant-b", "DB"));
        // Wrong tenant — should not see each other's backup
        Assert.Equal("a.abf", (await store.GetLastBackupAsync("tenant-a", "DB"))!.FileName);
        Assert.Equal("b.abf", (await store.GetLastBackupAsync("tenant-b", "DB"))!.FileName);
    }

    [Fact]
    public async Task GetLastBackupAsync_FiltersByDatabaseName()
    {
        await using var store = new BackupStore(TempDb());

        var id1 = await store.LogStartAsync("t1", "DB1", "db1.abf");
        await store.LogCompleteAsync(id1, succeeded: true, null);

        var id2 = await store.LogStartAsync("t1", "DB2", "db2.abf");
        await store.LogCompleteAsync(id2, succeeded: true, null);

        Assert.Null(await store.GetLastBackupAsync("t1", "DB3"));
        Assert.Equal("db1.abf", (await store.GetLastBackupAsync("t1", "DB1"))!.FileName);
        Assert.Equal("db2.abf", (await store.GetLastBackupAsync("t1", "DB2"))!.FileName);
    }

    // ── Schema survives reopen ────────────────────────────────────────────────

    [Fact]
    public async Task Data_PersistedAcrossStoreReopens()
    {
        var path = TempDb();

        await using (var store = new BackupStore(path))
        {
            var id = await store.LogStartAsync("t1", "DB", "db.abf");
            await store.LogCompleteAsync(id, succeeded: true, null);
        }

        // Reopen the same file
        await using var store2 = new BackupStore(path);
        var result = await store2.GetLastBackupAsync("t1", "DB");

        Assert.NotNull(result);
        Assert.Equal("db.abf", result.FileName);
    }
}
