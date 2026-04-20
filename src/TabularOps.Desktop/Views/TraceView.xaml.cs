using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TabularOps.Desktop.ViewModels;

namespace TabularOps.Desktop.Views;

public partial class TraceView : UserControl
{
    public TraceView()
    {
        InitializeComponent();
    }

    private void OnFilterChipClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is string filter && DataContext is TraceViewModel vm)
            vm.ToggleFilterCommand.Execute(filter);
    }
}
