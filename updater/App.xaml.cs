using System.Windows;

namespace Portfolio.OfficeSystem.Updater;

public partial class App : Application
{
    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        var window = new MainWindow(e.Args);
        if (window.IsSilentMode)
        {
            await window.RunUpdateAsync();
            return;
        }

        window.Show();
    }
}
