using TabularOps.Core.Dmv;
using TabularOps.Core.Model;
using TabularOps.Core.Refresh;

namespace TabularOps.Core.Tests;

/// <summary>
/// Tests for PartitionService.EnrichWithStorage — the pure Phase 2 function
/// that merges TOM partition structure with DMV storage statistics.
/// No live server connection is required.
/// </summary>
public class PartitionServiceTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static PartitionRef Part(string table, string partition) =>
        new(table, partition, PartitionState.Ok, null, null, null, null);

    private static TableSnapshot Snapshot(string table, params string[] partitions)
    {
        var refs = partitions.Select(p => Part(table, p)).ToList<PartitionRef>();
        return new TableSnapshot(table, false, refs.Count, 0, 0, 0, refs);
    }

    private static Dictionary<(string, string), PartitionStorageInfo> Storage(
        params (string table, string partition, long rows, long bytes)[] items) =>
        items.ToDictionary(
            i => (i.table, i.partition),
            i => new PartitionStorageInfo(i.table, i.partition, i.rows, i.bytes));

    // ── Exact match ───────────────────────────────────────────────────────────

    [Fact]
    public void EnrichWithStorage_ExactMatch_PopulatesRowCountAndSize()
    {
        var snapshots = new[] { Snapshot("Sales", "Sales_2024") };
        var storage   = Storage(("Sales", "Sales_2024", 1_000_000, 50_000_000));

        var result = PartitionService.EnrichWithStorage(snapshots, storage);

        Assert.Equal(1_000_000L,  result[0].Partitions[0].RowCount);
        Assert.Equal(50_000_000L, result[0].Partitions[0].SizeBytes);
    }

    // ── Power BI single-partition fallback (partition name == table name) ─────

    [Fact]
    public void EnrichWithStorage_SinglePartitionFallback_WhenPartitionKeyEqualsTableName()
    {
        // Power BI XMLA exposes single-partition tables with partitionName = tableName in DMV
        var snapshots = new[] { Snapshot("Date", "DatePartition") };
        var storage   = Storage(("Date", "Date", 365, 8_000));

        var result = PartitionService.EnrichWithStorage(snapshots, storage);

        Assert.Equal(365L, result[0].Partitions[0].RowCount);
        Assert.Equal(8_000L, result[0].Partitions[0].SizeBytes);
    }

    // ── Case-insensitive table name fallback ──────────────────────────────────

    [Fact]
    public void EnrichWithStorage_CaseInsensitiveFallback_MatchesTableNameIgnoringCase()
    {
        // Some endpoints return lowercase table names in DMV
        var snapshots = new[] { Snapshot("Product", "Product_Main") };
        var storage   = Storage(("product", "product", 500, 20_000));

        var result = PartitionService.EnrichWithStorage(snapshots, storage);

        Assert.Equal(500L, result[0].Partitions[0].RowCount);
    }

    // ── No match ─────────────────────────────────────────────────────────────

    [Fact]
    public void EnrichWithStorage_NoMatch_KeepsNullStats()
    {
        var snapshots = new[] { Snapshot("Customer", "Customer_All") };
        var storage   = Storage(("OtherTable", "OtherPartition", 1, 1));

        var result = PartitionService.EnrichWithStorage(snapshots, storage);

        Assert.Null(result[0].Partitions[0].RowCount);
        Assert.Null(result[0].Partitions[0].SizeBytes);
    }

    [Fact]
    public void EnrichWithStorage_EmptyStorage_KeepsAllStatsNull()
    {
        var snapshots = new[] { Snapshot("Fact", "P1", "P2") };
        var storage   = new Dictionary<(string, string), PartitionStorageInfo>();

        var result = PartitionService.EnrichWithStorage(snapshots, storage);

        Assert.All(result[0].Partitions, p =>
        {
            Assert.Null(p.RowCount);
            Assert.Null(p.SizeBytes);
        });
    }

    // ── Aggregation ───────────────────────────────────────────────────────────

    [Fact]
    public void EnrichWithStorage_AggregatesTotalRowCount_AcrossPartitions()
    {
        var snapshots = new[] { Snapshot("Fact", "P1", "P2", "P3") };
        var storage   = Storage(
            ("Fact", "P1", 100, 1_000),
            ("Fact", "P2", 200, 2_000),
            ("Fact", "P3", 300, 3_000));

        var result = PartitionService.EnrichWithStorage(snapshots, storage);

        Assert.Equal(600L,   result[0].TotalRowCount);
        Assert.Equal(6_000L, result[0].TotalSizeBytes);
    }

    [Fact]
    public void EnrichWithStorage_MaxPartitionSize_IsLargestPartition()
    {
        var snapshots = new[] { Snapshot("Orders", "Small", "Large") };
        var storage   = Storage(
            ("Orders", "Small", 10,  1_000),
            ("Orders", "Large", 999, 9_999_999));

        var result = PartitionService.EnrichWithStorage(snapshots, storage);

        Assert.Equal(9_999_999L, result[0].MaxPartitionSizeBytes);
    }

    [Fact]
    public void EnrichWithStorage_MultipleSnapshots_EachEnrichedIndependently()
    {
        var snapshots = new[]
        {
            Snapshot("Sales",    "Sales_2024"),
            Snapshot("Inventory","Inventory_All"),
        };
        var storage = Storage(
            ("Sales",    "Sales_2024",    500, 5_000),
            ("Inventory","Inventory_All", 200, 2_000));

        var result = PartitionService.EnrichWithStorage(snapshots, storage);

        Assert.Equal(500L, result[0].Partitions[0].RowCount);
        Assert.Equal(200L, result[1].Partitions[0].RowCount);
    }

    // ── Immutability ─────────────────────────────────────────────────────────

    [Fact]
    public void EnrichWithStorage_DoesNotMutateOriginalSnapshots()
    {
        var snapshots = new[] { Snapshot("Sales", "P1") };
        var storage   = Storage(("Sales", "P1", 42, 1_000));

        var result = PartitionService.EnrichWithStorage(snapshots, storage);

        // Original partition ref is unchanged — with-copy should produce a new instance
        Assert.Null(snapshots[0].Partitions[0].RowCount);
        Assert.Equal(42L, result[0].Partitions[0].RowCount);
    }

    [Fact]
    public void EnrichWithStorage_PreservesPartitionState()
    {
        var failed = new PartitionRef("Fact", "P1", PartitionState.Failed, null, null, null, "Source error");
        var snap   = new TableSnapshot("Fact", false, 1, 0, 0, 0, [failed]);
        var storage = Storage(("Fact", "P1", 100, 5_000));

        var result = PartitionService.EnrichWithStorage([snap], storage);

        Assert.Equal(PartitionState.Failed, result[0].Partitions[0].State);
        Assert.Equal("Source error", result[0].Partitions[0].LastError);
    }
}
