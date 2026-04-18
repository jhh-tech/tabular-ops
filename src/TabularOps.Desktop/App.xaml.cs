using System.IO;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using TabularOps.Core.Connection;
using TabularOps.Core.Refresh;

namespace TabularOps.Desktop;

public partial class App : Application
{
    public static ConnectionManager ConnectionManager { get; private set; } = null!;
    public static ConnectionStore ConnectionStore { get; private set; } = null!;
    public static RefreshHistoryStore RefreshHistory { get; private set; } = null!;
    public static TomRefreshEngine RefreshEngine { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        var clientId = config["EntraClientId"]
            ?? throw new InvalidOperationException(
                "EntraClientId is missing from appsettings.json. " +
                "See README.md for setup instructions.");

        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TabularOps");

        ConnectionManager = new ConnectionManager(Path.Combine(appDataDir, "Cache"), clientId);
        ConnectionStore = new ConnectionStore(appDataDir);
        RefreshHistory = new RefreshHistoryStore(Path.Combine(appDataDir, "refresh-history.db"));
        RefreshEngine = new TomRefreshEngine(ConnectionManager, RefreshHistory);

        // Ensure interactive token acquisition always runs on the UI thread with the
        // main window handle — required for Windows broker (WAM) on Windows 10/11.
        ConnectionManager.InteractiveAuthProvider = (app, scopes) =>
            Dispatcher.InvokeAsync(async () =>
            {
                var hwnd = MainWindow is not null
                    ? new WindowInteropHelper(MainWindow).Handle
                    : IntPtr.Zero;

                return await app
                    .AcquireTokenInteractive(scopes)
                    .WithParentActivityOrWindow(hwnd)
                    .WithPrompt(Prompt.SelectAccount)
                    .ExecuteAsync();
            }).Task.Unwrap();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        await ConnectionManager.DisposeAsync();
        await RefreshHistory.DisposeAsync();
        base.OnExit(e);
    }
}
