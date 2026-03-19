using System.Windows;
using System.Windows.Input;

namespace PassTheStick.Launcher;

public partial class LauncherWindow : Window
{
    public bool IsGuest { get; private set; }

    public LauncherWindow()
    {
        InitializeComponent();
        SelectHost();
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void HostCard_Click(object sender, MouseButtonEventArgs e) => SelectHost();
    private void GuestCard_Click(object sender, MouseButtonEventArgs e) => SelectGuest();

    private void SelectHost()
    {
        IsGuest = false;
        ModeHintText.Text = "Selected: Host";
        HostCard.BorderBrush = (System.Windows.Media.Brush)FindResource("PtsBrushGreen");
        HostCard.BorderThickness = new Thickness(2);
        GuestCard.BorderBrush = (System.Windows.Media.Brush)FindResource("PtsBrushBorder");
        GuestCard.BorderThickness = new Thickness(1);
    }

    private void SelectGuest()
    {
        IsGuest = true;
        ModeHintText.Text = "Selected: Guest";
        GuestCard.BorderBrush = (System.Windows.Media.Brush)FindResource("PtsBrushGreen");
        GuestCard.BorderThickness = new Thickness(2);
        HostCard.BorderBrush = (System.Windows.Media.Brush)FindResource("PtsBrushBorder");
        HostCard.BorderThickness = new Thickness(1);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }
}
