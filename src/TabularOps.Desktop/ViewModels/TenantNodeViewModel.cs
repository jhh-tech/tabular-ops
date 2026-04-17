using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using TabularOps.Core.Connection;
using TabularOps.Core.Model;

namespace TabularOps.Desktop.ViewModels;

public enum TenantConnectionStatus { Connecting, Connected, Error, ReadOnly }

public partial class TenantNodeViewModel : ObservableObject
{
    public TenantContext Context { get; }

    [ObservableProperty] private TenantConnectionStatus _status = TenantConnectionStatus.Connecting;
    [ObservableProperty] private string? _errorMessage;

    public ObservableCollection<ModelNodeViewModel> Models { get; } = [];

    public string DisplayName => Context.DisplayName;
    public string TenantId => Context.TenantId;

    public TenantNodeViewModel(TenantContext context)
    {
        Context = context;
    }
}

public partial class ModelNodeViewModel : ObservableObject
{
    public ModelRef Model { get; }

    [ObservableProperty] private bool _isActive;

    public string DisplayName => Model.DatabaseName;
    public string WorkspaceName => Model.WorkspaceName;

    public ModelNodeViewModel(ModelRef model)
    {
        Model = model;
    }
}
