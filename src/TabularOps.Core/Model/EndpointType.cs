namespace TabularOps.Core.Model;

public enum EndpointType
{
    PowerBi,   // powerbi:// XMLA — routes to Enhanced Refresh REST API
    Ssas,      // Server name or full SSAS connection string
    Aas,       // asazure:// Azure Analysis Services
}
