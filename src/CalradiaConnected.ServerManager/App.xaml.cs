using System.Windows;
using CalradiaConnected.Common.Localization;
using CoopServerManager.Services;

namespace CoopServerManager;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Sync language with Windows OS culture before MainWindow & MainViewModel are constructed
        Loc.SyncFromSystemCulture();
        // One-time repair if an older version leaked disable_sound=1 / max_framerate<=30 into engine_config.txt
        ServerRunnerService.RepairLeakedEngineConfigOnce();
        base.OnStartup(e);
    }
}

