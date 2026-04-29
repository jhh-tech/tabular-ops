using TabularOps.Core.Connection;
using TabularOps.Core.Model;

namespace TabularOps.Core.Tests;

public class TenantContextTests
{
    // ── Power BI URI workspace extraction ────────────────────────────────────

    [Theory]
    [InlineData("powerbi://api.powerbi.com/v1.0/myorg/MyWorkspace", "MyWorkspace")]
    [InlineData("powerbi://api.powerbi.com/v1.0/myorg/Acme-Corp",   "Acme-Corp")]
    [InlineData("powerbi://api.powerbi.com/v1.0/myorg/sales_bi",    "sales_bi")]
    public void TenantId_PowerBi_ExtractsLastUriSegment(string connectionString, string expected)
    {
        var ctx = new TenantContext
        {
            DisplayName      = "Test",
            ConnectionString = connectionString,
            EndpointType     = EndpointType.PowerBi,
        };

        Assert.Equal(expected, ctx.TenantId);
    }

    [Fact]
    public void TenantId_PowerBi_StripsTrailingSlash()
    {
        var ctx = new TenantContext
        {
            DisplayName      = "Test",
            ConnectionString = "powerbi://api.powerbi.com/v1.0/myorg/MyWorkspace/",
            EndpointType     = EndpointType.PowerBi,
        };

        Assert.Equal("MyWorkspace", ctx.TenantId);
    }

    // ── SSAS / AAS: connection string returned as-is ─────────────────────────

    [Fact]
    public void TenantId_Ssas_ReturnsFullConnectionString()
    {
        const string cs = "Data Source=localhost\\TABULAR;Integrated Security=SSPI;";
        var ctx = new TenantContext
        {
            DisplayName      = "Local SSAS",
            ConnectionString = cs,
            EndpointType     = EndpointType.Ssas,
        };

        Assert.Equal(cs, ctx.TenantId);
    }

    [Fact]
    public void TenantId_Aas_ReturnsFullConnectionString()
    {
        const string cs = "asazure://westeurope.asazure.windows.net/myserver";
        var ctx = new TenantContext
        {
            DisplayName      = "My AAS",
            ConnectionString = cs,
            EndpointType     = EndpointType.Aas,
        };

        Assert.Equal(cs, ctx.TenantId);
    }

    // ── Two different workspaces produce different tenant IDs ─────────────────

    [Fact]
    public void TenantId_PowerBi_DifferentWorkspaces_ProduceDifferentIds()
    {
        var ctx1 = new TenantContext
        {
            DisplayName      = "A",
            ConnectionString = "powerbi://api.powerbi.com/v1.0/myorg/WorkspaceA",
            EndpointType     = EndpointType.PowerBi,
        };
        var ctx2 = new TenantContext
        {
            DisplayName      = "B",
            ConnectionString = "powerbi://api.powerbi.com/v1.0/myorg/WorkspaceB",
            EndpointType     = EndpointType.PowerBi,
        };

        Assert.NotEqual(ctx1.TenantId, ctx2.TenantId);
    }
}
