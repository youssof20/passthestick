using System.Windows;

namespace PassTheStick.Guest;

public partial class App : Application
{
    public App()
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
    }
}
