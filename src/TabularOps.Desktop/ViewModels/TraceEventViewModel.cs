using TabularOps.Core.Tracing;

namespace TabularOps.Desktop.ViewModels;

public class TraceEventViewModel
{
    private readonly TraceEvent _model;

    public TraceEventViewModel(TraceEvent model)
    {
        _model = model;
    }

    public long Id => _model.Id;
    public DateTimeOffset Time => _model.Time;
    public string Time_Formatted => _model.Time.ToString("HH:mm:ss.fff");

    public string EventClass => _model.EventClass;
    public string? EventSubclass => _model.EventSubclass;

    public string Summary => _model.Summary;

    public string Scope => _model.TableName is not null
        ? _model.PartitionName is not null
            ? $"{_model.TableName} › {_model.PartitionName}"
            : _model.TableName
        : string.Empty;

    public string? Text => _model.Text;

    public string? Duration => _model.DurationMs.HasValue
        ? _model.DurationMs >= 1000
            ? $"{_model.DurationMs / 1000.0:F1}s"
            : $"{_model.DurationMs}ms"
        : null;

    public string? CpuMs => _model.CpuMs.HasValue ? $"{_model.CpuMs}ms" : null;

    public string? RowCount => _model.RowCount.HasValue ? _model.RowCount.Value.ToString("N0") : null;

    public string? ErrorCode => _model.ErrorCode.HasValue ? $"0x{_model.ErrorCode:X}" : null;

    public string? SessionId => _model.SessionId;

    public bool IsError => _model.EventClass.Contains("Error", StringComparison.OrdinalIgnoreCase);

    public bool IsProgress => _model.EventClass.Contains("Progress", StringComparison.OrdinalIgnoreCase);
    public bool IsQuery => _model.EventClass.Contains("Query", StringComparison.OrdinalIgnoreCase);
    public bool IsLock => _model.EventClass.Contains("Lock", StringComparison.OrdinalIgnoreCase);
    public bool IsAudit => _model.EventClass.Contains("Audit", StringComparison.OrdinalIgnoreCase) ||
                           _model.EventClass.Contains("Command", StringComparison.OrdinalIgnoreCase);
}
