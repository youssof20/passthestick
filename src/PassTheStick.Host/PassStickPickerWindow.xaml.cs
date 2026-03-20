using System.Windows;
using System.Windows.Input;
using PassTheStick.Shared;

namespace PassTheStick.Host;

public partial class PassStickPickerWindow : Window
{
    private readonly Action<PlayerInfo>? _onSelect;
    private IReadOnlyList<PlayerInfo> _players = new List<PlayerInfo>();

    public PassStickPickerWindow(Action<PlayerInfo> onSelect)
    {
        _onSelect = onSelect;
        InitializeComponent();
    }

    public void SetPlayers(IReadOnlyList<PlayerInfo> players)
    {
        _players = players;
        GuestsList.ItemsSource = null;
        GuestsList.ItemsSource = _players;
    }

    public void ShowNearCursor()
    {
        GetCursorPos(out POINT p);
        Left = p.X;
        Top = p.Y;
        Show();
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    private void PassButton_Click(object sender, RoutedEventArgs e)
    {
        if (GuestsList.SelectedItem is PlayerInfo p)
        {
            _onSelect?.Invoke(p);
            Close();
        }
    }

    private void GuestsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GuestsList.SelectedItem is PlayerInfo p)
        {
            _onSelect?.Invoke(p);
            Close();
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); }
            catch { /* drag can fail */ }
        }
    }
}
