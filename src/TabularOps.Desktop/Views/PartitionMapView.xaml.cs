using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TabularOps.Desktop.ViewModels;

namespace TabularOps.Desktop.Views;

public partial class PartitionMapView : UserControl
{
    public PartitionMapView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        PreviewKeyDown += OnPreviewKeyDown;
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

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.OemQuestion)
        {
            if (PartitionFilterBox.IsFocused) return;
            PartitionFilterBox.Focus();
            PartitionFilterBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            if (DataContext is PartitionMapViewModel vm && vm.RefreshSelectedCommand.CanExecute(null))
                vm.RefreshSelectedCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && PartitionFilterBox.IsFocused)
        {
            if (DataContext is PartitionMapViewModel vm)
                vm.PartitionNameFilter = string.Empty;
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }
}
