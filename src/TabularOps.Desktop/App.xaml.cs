using System.IO;
using System.Windows;
using Microsoft.Extensions.Configuration;
using TabularOps.Core.Connection;

namespace TabularOps.Desktop;

public partial class App : Application
{
    public static ConnectionManager ConnectionManager { get; private set; } = null!;
    public static ConnectionStore ConnectionStore { get; private set; } = null!;

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
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        await ConnectionManager.DisposeAsync();
        base.OnExit(e);
    }
}
