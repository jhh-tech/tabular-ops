using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using TabularOps.Core.Connection;
using TabularOps.Core.Tracing;

namespace TabularOps.Desktop.ViewModels;

public partial class TraceViewModel : ObservableObject
{
    private readonly ConnectionManager _connectionManager;
    private readonly TraceStore _traceStore;

    private string? _tenantId;
    private string? _databaseName;
    private TraceCollector? _collector;
    private CancellationTokenSource? _readCts;

    public ObservableCollection<TraceEventViewModel> Events { get; } = [];
    public ICollectionView EventsView { get; private set; }

    [ObservableProperty] private TraceEventViewModel? _selectedEvent;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _statusMessage = "Ready";

    [ObservableProperty] private bool _filterProgress = true;
    [ObservableProperty] private bool _filterQuery = true;
    [ObservableProperty] private bool _filterErrors = true;
    [ObservableProperty] private bool _filterLock;
    [ObservableProperty] private bool _filterAudit;

    public TraceViewModel(ConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TabularOps", "trace.db");
        _traceStore = new TraceStore(dbPath);

        EventsView = CollectionViewSource.GetDefaultView(Events);
        EventsView.Filter = FilterEvent;

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName?.StartsWith("Filter") == true)
                EventsView.Refresh();
        };

        Events.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
            {
                if (EventsView.IsEmpty)
                    EventsView.MoveCurrentToFirst();
            }
        };
    }

    public void SetContext(string? tenantId, string? databaseName)
    {
        _tenantId = tenantId;
        _databaseName = databaseName;
    }

    public async Task StartTraceAsync()
    {
        if (IsRunning || _tenantId is null) return;

        try
        {
            HasError = false;
            StatusMessage = "Starting trace...";
            IsRunning = true;

            _collector = new TraceCollector(_connectionManager, _tenantId, _databaseName ?? "");
            await _collector.StartAsync();

            _readCts = new CancellationTokenSource();
            _ = ReadTraceEventsAsync(_readCts.Token);

            StatusMessage = "Trace running";
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = $"Error: {ex.Message}";
            IsRunning = false;
            _collector = null;
        }
    }

    public async Task StopTraceAsync()
    {
        if (!IsRunning) return;

        try
        {
            _readCts?.Cancel();
            if (_collector is not null)
                await _collector.StopAsync();

            StatusMessage = "Trace stopped";
            IsRunning = false;
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = $"Error stopping: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task StartTrace() => await StartTraceAsync();

    [RelayCommand]
    public async Task StopTrace() => await StopTraceAsync();

    [RelayCommand]
    public void ClearEvents()
    {
        Events.Clear();
        SelectedEvent = null;
        StatusMessage = "Events cleared";
    }

    [RelayCommand]
    public void ToggleFilter(string? filterName)
    {
        if (filterName is null) return;

        switch (filterName)
        {
            case "Progress":
                FilterProgress = !FilterProgress;
                break;
            case "Query":
                FilterQuery = !FilterQuery;
                break;
            case "Errors":
                FilterErrors = !FilterErrors;
                break;
            case "Lock":
                FilterLock = !FilterLock;
                break;
            case "Audit":
                FilterAudit = !FilterAudit;
                break;
        }
    }

    private async Task ReadTraceEventsAsync(CancellationToken ct)
    {
        if (_collector is null) return;

        try
        {
            await foreach (var evt in _collector.ReadAllAsync(ct))
            {
                if (ct.IsCancellationRequested) break;

                _traceStore.TryEnqueue(evt);

                var vm = new TraceEventViewModel(evt);
                Events.Add(vm);

                if (Events.Count > 10000)
                    Events.RemoveAt(0);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on stop
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = $"Read error: {ex.Message}";
        }
    }

    private bool FilterEvent(object obj)
    {
        if (obj is not TraceEventViewModel vm) return true;

        if (vm.IsError && !FilterErrors) return false;
        if (vm.IsProgress && !FilterProgress) return false;
        if (vm.IsQuery && !FilterQuery) return false;
        if (vm.IsLock && !FilterLock) return false;
        if (vm.IsAudit && !FilterAudit) return false;

        return true;
    }

    ~TraceViewModel()
    {
        _readCts?.Cancel();
        _collector?.DisposeAsync().GetAwaiter().GetResult();
        _traceStore?.DisposeAsync().GetAwaiter().GetResult();
    }
}
