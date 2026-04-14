using System.Windows;

namespace GameLibrary.Net8;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        UiLocalization.ApplyLanguage(AppSettings.UiLanguage);
        base.OnStartup(e);
    }
}
