using System.Windows;
using CalradiaConnected.Common.Localization;

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
        base.OnStartup(e);
    }
}

