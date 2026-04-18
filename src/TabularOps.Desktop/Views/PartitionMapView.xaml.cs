using System.Windows;
using System.Windows.Controls;
using TabularOps.Desktop.ViewModels;

namespace TabularOps.Desktop.Views;

public partial class PartitionMapView : UserControl
{
    public PartitionMapView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is PartitionMapViewModel vm)
        {
            vm.ConfirmRefresh = (partitions, refreshType) =>
            {
                var dialog = new RefreshConfirmDialog(partitions, refreshType)
                {
                    Owner = Window.GetWindow(this)
                };
                return dialog.ShowDialog() == true;
            };
        }
    }
}
