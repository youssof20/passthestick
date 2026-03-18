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

    public void SetBanner(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            BannerText.Text = string.Empty;
            BannerText.Visibility = Visibility.Collapsed;
        }
        else
        {
            BannerText.Text = message;
            BannerText.Visibility = Visibility.Visible;
        }
    }

    private void OverlayWindow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }
}
