using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PassTheStick.Windows;
using PassTheStick.App.ViewModels;

namespace PassTheStick.App.Services;

/// <summary>Keeps the overlay over the game: shown only while the game is in front during a session, and following the window as it moves.</summary>
public sealed class OverlayController : IDisposable
{
    private const double Margin = 16;

    private readonly IHostSession _session;
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _timer;
    private OverlayWindow? _window;

    public OverlayController(IHostSession session, MainViewModel vm)
    {
        _session = session;
        _vm = vm;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => Update();
        _timer.Start();
    }

    public void Dispose()
    {
        _timer.Stop();
        _window?.Close();
    }

    private void Update()
    {
        var game = _session.Game;
        if (!_vm.IsLive || !game.IsPinned || !game.IsForeground || !_session.TryGetGameRect(out var rect))
        {
            _window?.Hide();
            return;
        }

        _window ??= new OverlayWindow();
        _window.SetText(_vm.HolderText);
        if (!_window.IsVisible)
        {
            _window.Show();
        }

        // The game rectangle is in screen pixels, so work in pixels: convert the overlay's size and margin by the window's DPI.
        _window.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(_window);
        var (x, y) = OverlayPlacement.Compute(rect, _window.ActualWidth * dpi.DpiScaleX, _window.ActualHeight * dpi.DpiScaleY, Margin * dpi.DpiScaleX);
        OverlayStyle.MoveTo(new WindowInteropHelper(_window).Handle, (int)Math.Round(x), (int)Math.Round(y));
    }
}
