using System.Windows;
using System.Windows.Input;

namespace PassTheStick.Host;

public partial class OverlayWindow : Window
{
    public OverlayWindow()
    {
        InitializeComponent();
    }

    public void SetPlayerName(string name)
    {
        PlayerNameText.Text = string.IsNullOrEmpty(name) ? "Host" : name;
    }

    private void OverlayWindow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }
}
