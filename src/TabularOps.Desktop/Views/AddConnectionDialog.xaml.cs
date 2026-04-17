using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TabularOps.Core.Connection;
using TabularOps.Core.Model;

namespace TabularOps.Desktop.Views;

public partial class AddConnectionDialog : Window
{
    public IReadOnlyList<TenantContext>? ResultContexts { get; private set; }

    private List<WorkspaceInfo> _allWorkspaces = [];

    // Source of truth for checked state — independent of what's currently visible
    private readonly HashSet<string> _checkedNames = [];

    public AddConnectionDialog()
    {
        InitializeComponent();
        RadioPowerBi.Checked += (_, _) => ShowPowerBiPanel();
        RadioSsas.Checked += (_, _) => ShowSsasPanel();
        ShowPowerBiPanel();
    }

    private void ShowPowerBiPanel()
    {
        PanelPowerBi.Visibility = Visibility.Visible;
        PanelSsas.Visibility = Visibility.Collapsed;
        BtnConnect.IsEnabled = false;
    }

    private void ShowSsasPanel()
    {
        PanelPowerBi.Visibility = Visibility.Collapsed;
        PanelSsas.Visibility = Visibility.Visible;
        BtnConnect.IsEnabled = true;
    }

    private async void OnSignIn(object sender, RoutedEventArgs e)
    {
        TxtError.Visibility = Visibility.Collapsed;
        BtnSignIn.IsEnabled = false;
        BtnSignIn.Content = "Signing in...";

        try
        {
            var workspaces = await App.ConnectionManager.GetWorkspacesAsync();
            _allWorkspaces = [.. workspaces];
            PopulateList(_allWorkspaces);

            StepSignIn.Visibility = Visibility.Collapsed;
            StepWorkspacePicker.Visibility = Visibility.Visible;
            TxtSearch.Focus();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            BtnSignIn.IsEnabled = true;
            BtnSignIn.Content = "Sign In";
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        var query = TxtSearch.Text.Trim();
        var filtered = string.IsNullOrEmpty(query)
            ? _allWorkspaces
            : _allWorkspaces
                .Where(w => w.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

        PopulateList(filtered);
    }

    private void PopulateList(IEnumerable<WorkspaceInfo> workspaces)
    {
        WorkspaceList.Children.Clear();

        foreach (var w in workspaces)
        {
            var cb = new CheckBox
            {
                Content = w.Name,
                IsChecked = _checkedNames.Contains(w.Name),
                FontFamily = (FontFamily)FindResource("MonoFont"),
                FontSize = 12,
                Foreground = (Brush)FindResource("Text0"),
                Background = Brushes.Transparent,
                Padding = new Thickness(6, 5, 10, 5),
                Margin = new Thickness(4, 0, 0, 0),
            };
            cb.Checked += OnCheckChanged;
            cb.Unchecked += OnCheckChanged;
            WorkspaceList.Children.Add(cb);
        }

        UpdateConnectButton();
    }

    private void OnCheckChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.Content is string name)
        {
            if (cb.IsChecked == true)
                _checkedNames.Add(name);
            else
                _checkedNames.Remove(name);
        }

        UpdateConnectButton();
    }

    private void UpdateConnectButton()
    {
        var count = _checkedNames.Count;

        if (count == 0)
        {
            TxtSelectionCount.Visibility = Visibility.Collapsed;
            BtnConnect.Content = "Connect";
            BtnConnect.IsEnabled = false;
        }
        else
        {
            TxtSelectionCount.Text = $"{count} selected";
            TxtSelectionCount.Visibility = Visibility.Visible;
            BtnConnect.Content = count == 1 ? "Connect" : $"Connect ({count})";
            BtnConnect.IsEnabled = true;
        }
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        TxtError.Visibility = Visibility.Collapsed;
        IsEnabled = false;

        try
        {
            if (RadioPowerBi.IsChecked == true)
            {
                if (_checkedNames.Count == 0) { ShowError("Please select at least one workspace."); return; }

                var contexts = new List<TenantContext>();
                foreach (var name in _checkedNames)
                {
                    var connectionString = "powerbi://api.powerbi.com/v1.0/myorg/" +
                        Uri.EscapeDataString(name);
                    contexts.Add(await App.ConnectionManager
                        .AddPowerBiTenantAsync(connectionString, name));
                }

                ResultContexts = contexts;
            }
            else
            {
                var displayName = TxtDisplayName.Text.Trim();
                var connectionString = TxtConnectionString.Text.Trim();

                if (string.IsNullOrEmpty(displayName)) { ShowError("Display name is required."); return; }
                if (string.IsNullOrEmpty(connectionString)) { ShowError("Connection string is required."); return; }

                ResultContexts = [await App.ConnectionManager
                    .AddSsasTenantAsync(connectionString, displayName)];
            }

            DialogResult = true;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowError(string message)
    {
        TxtError.Text = message;
        TxtError.Visibility = Visibility.Visible;
    }
}
