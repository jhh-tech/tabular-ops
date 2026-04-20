using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        // Hide Connect until workspaces are loaded; workspace picker step shows it
        BtnConnect.Visibility = StepWorkspacePicker.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        BtnConnect.IsEnabled = false;
    }

    private void ShowSsasPanel()
    {
        PanelPowerBi.Visibility = Visibility.Collapsed;
        PanelSsas.Visibility = Visibility.Visible;
        BtnConnect.Visibility = Visibility.Visible;
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

            var upn = await App.ConnectionManager.GetPowerBiAccountUsernameAsync();
            TxtSignedInAs.Text = upn is not null ? $"({upn})" : string.Empty;

            StepSignIn.Visibility = Visibility.Collapsed;
            StepWorkspacePicker.Visibility = Visibility.Visible;
            BtnConnect.Visibility = Visibility.Visible;
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
                Style = (Style)FindResource("DarkCheckBox"),
                FontFamily = (FontFamily)FindResource("MonoFont"),
                FontSize = 12,
                Padding = new Thickness(8, 5, 10, 5),
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

                // Build name→WorkspaceInfo lookup so capacity details flow through
                var infoByName = _allWorkspaces.ToDictionary(w => w.Name, StringComparer.OrdinalIgnoreCase);

                var contexts = new List<TenantContext>();
                foreach (var name in _checkedNames)
                {
                    var connectionString = "powerbi://api.powerbi.com/v1.0/myorg/" +
                        Uri.EscapeDataString(name);
                    infoByName.TryGetValue(name, out var workspaceInfo);
                    contexts.Add(await App.ConnectionManager
                        .AddPowerBiTenantAsync(connectionString, name, workspaceInfo));
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

    private async void OnDialogSignOut(object sender, RoutedEventArgs e)
    {
        BtnSignOut.IsEnabled = false;
        await App.ConnectionManager.SignOutPowerBiAsync();

        // Reset back to Sign In step
        _allWorkspaces.Clear();
        _checkedNames.Clear();
        WorkspaceList.Children.Clear();
        TxtSearch.Clear();
        TxtManualWorkspace.Clear();
        TxtSignedInAs.Text = string.Empty;
        UpdateConnectButton();

        StepWorkspacePicker.Visibility = Visibility.Collapsed;
        BtnConnect.Visibility = Visibility.Collapsed;
        StepSignIn.Visibility = Visibility.Visible;
        BtnSignIn.IsEnabled = true;
        BtnSignIn.Content = "Sign In";
        BtnSignOut.IsEnabled = true;
    }

    private void OnManualWorkspaceKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddManualWorkspace();
    }

    private void OnAddManualWorkspace(object sender, RoutedEventArgs e) => AddManualWorkspace();

    private void AddManualWorkspace()
    {
        var name = TxtManualWorkspace.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;

        // Add to the master list if not already present
        if (!_allWorkspaces.Any(w => w.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            _allWorkspaces.Add(new WorkspaceInfo(
                Id: name, Name: name,
                CapacityId: null, CapacityName: null, CapacityRegion: null, CapacitySku: null));
        }

        _checkedNames.Add(name);
        TxtManualWorkspace.Clear();

        // Re-apply any active search filter so the new entry shows
        var query = TxtSearch.Text.Trim();
        var filtered = string.IsNullOrEmpty(query)
            ? _allWorkspaces
            : _allWorkspaces.Where(w => w.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        PopulateList(filtered);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowError(string message)
    {
        TxtError.Text = message;
        TxtError.Visibility = Visibility.Visible;
    }
}
