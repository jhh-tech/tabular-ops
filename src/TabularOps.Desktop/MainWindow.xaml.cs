using System.Windows;
using System.Windows.Input;
using TabularOps.Desktop.ViewModels;

namespace TabularOps.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainViewModel(App.ConnectionManager, App.ConnectionStore);
        DataContext = vm;
        Loaded += async (_, _) => await vm.RestoreConnectionsAsync();
        UpdateThemeButton();
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        ThemeManager.Toggle();
        UpdateThemeButton();
    }

    private void UpdateThemeButton() =>
        ThemeToggleButton.Content = ThemeManager.IsDark ? "☀" : "☾";

    private void OnWorkspaceHeaderClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is TenantNodeViewModel vm)
            vm.IsExpanded = !vm.IsExpanded;
    }
}
