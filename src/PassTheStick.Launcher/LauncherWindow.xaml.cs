using System.Windows;

namespace PassTheStick.Launcher;

public partial class LauncherWindow : Window
{
    public bool IsGuest => GuestRadio.IsChecked == true;

    public LauncherWindow()
    {
        InitializeComponent();
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
