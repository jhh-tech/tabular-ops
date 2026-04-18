using System.Windows;
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
}
