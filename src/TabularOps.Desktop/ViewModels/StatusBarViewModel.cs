using CommunityToolkit.Mvvm.ComponentModel;
using TabularOps.Core.Connection;

namespace TabularOps.Desktop.ViewModels;

public partial class StatusBarViewModel : ObservableObject
{
    [ObservableProperty] private string _connectionState = "Not connected";
    [ObservableProperty] private string _endpointLabel = string.Empty;
    [ObservableProperty] private bool _isReadOnly;
    [ObservableProperty] private int _activeSessions;
    [ObservableProperty] private string _memoryUsage = string.Empty;
    [ObservableProperty] private int _refreshQueueDepth;

    public void UpdateFromContext(TenantContext? context)
    {
        if (context is null)
        {
            ConnectionState = "Not connected";
            EndpointLabel = string.Empty;
            IsReadOnly = false;
            ActiveSessions = 0;
            MemoryUsage = string.Empty;
            return;
        }

        EndpointLabel = context.EndpointType.ToString();
        IsReadOnly = context.IsReadOnly;
        ConnectionState = context.IsReadOnly ? "Read-only" : "Connected";
    }
}
