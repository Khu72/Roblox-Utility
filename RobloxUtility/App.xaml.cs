using System.Windows;
using RobloxUtility.Services;

namespace RobloxUtility;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppLog.Init();
        AppLog.Info("Starting Roblox Utility…");
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Info("Exiting Roblox Utility.");
        base.OnExit(e);
    }
}
