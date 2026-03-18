using System.IO;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace PassTheStick.Host;

public partial class App : System.Windows.Application
{
    private void App_OnStartup(object sender, StartupEventArgs e)
    {
        SessionEnding += (_, args) =>
        {
            try
            {
                Current.MainWindow?.Close();
            }
            finally
            {
                args.Cancel = false;
                Shutdown();
            }
        };

        if (!ElevationHelper.IsRunningAsAdmin())
        {
            var result = System.Windows.MessageBox.Show(
                "Some games require admin rights to receive input. Restart as administrator?",
                "PassTheStick",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
                ElevationHelper.RestartAsAdmin();
        }
        var main = new MainWindow();
        main.Show();
        main.Closed += (_, _) => Shutdown();
    }
}
