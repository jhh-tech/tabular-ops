using TabularOps.Core.Model;

namespace TabularOps.Core.Refresh;

/// <summary>
/// Routes refresh operations to the correct engine based on the model's endpoint type:
///   - Power BI / Fabric → <see cref="PowerBiRefreshEngine"/> (Enhanced Refresh REST API)
///   - SSAS / AAS        → <see cref="TomRefreshEngine"/> (TOM RequestRefresh + SaveChanges)
///
/// Callers only need a single <see cref="RefreshDispatcher"/> reference; they do not
/// need to know which underlying engine is used.
/// </summary>
public sealed class RefreshDispatcher
{
    private readonly TomRefreshEngine      _tom;
    private readonly PowerBiRefreshEngine  _powerBi;

    public RefreshDispatcher(TomRefreshEngine tom, PowerBiRefreshEngine powerBi)
    {
        _tom     = tom;
        _powerBi = powerBi;
    }

    /// <summary>
    /// Refreshes the specified partitions using the engine appropriate for the model's
    /// endpoint type. Cancellation is genuinely honoured on both paths.
    /// </summary>
    public Task<IReadOnlyList<RefreshRun>> RefreshAsync(
        ModelRef model,
        IReadOnlyList<(string TableName, string PartitionName)> partitions,
        RefreshMode mode = RefreshMode.Automatic,
        IProgress<RefreshProgressEvent>? progress = null,
        CancellationToken ct = default)
    {
        return model.EndpointType == EndpointType.PowerBi
            ? _powerBi.RefreshAsync(model, partitions, mode, progress, ct)
            : _tom.RefreshAsync(model.TenantId, model.DatabaseName, partitions, mode, progress, ct);
    }

    /// <summary>
    /// Refreshes the entire model (all tables) using the appropriate engine.
    /// </summary>
    public Task RefreshModelAsync(
        ModelRef model,
        RefreshMode mode = RefreshMode.Automatic,
        CancellationToken ct = default)
    {
        return model.EndpointType == EndpointType.PowerBi
            ? _powerBi.RefreshModelAsync(model, mode, ct)
            : _tom.RefreshModelAsync(model.TenantId, model.DatabaseName, mode, ct);
    }
}
