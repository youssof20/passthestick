using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace PassTheStick.Host;

public partial class OverlayWindow : Window
{
    public event Action? PassRequested;

    private string _activeName = "Host";
    private bool _hostHasStick = true;
    private IReadOnlyList<string> _queue = Array.Empty<string>();

    public OverlayWindow()
    {
        InitializeComponent();
    }

    public void SetTurn(string activeName, bool hostHasStick, IReadOnlyList<string> queuePreview)
    {
        activeName = string.IsNullOrWhiteSpace(activeName) ? "Host" : activeName;
        _activeName = activeName;
        _hostHasStick = hostHasStick;
        _queue = queuePreview ?? Array.Empty<string>();

        AccentBar.Background = hostHasStick
            ? (System.Windows.Media.Brush)FindResource("PtsBrushGreen")
            : (System.Windows.Media.Brush)FindResource("PtsBrushOrange");

        var newTitle = hostHasStick ? "You're playing" : $"{activeName}'s turn";
        var showSubtitle = !hostHasStick;

        AnimateTitleChange(newTitle, showSubtitle);
        BuildQueue();
    }

    private void AnimateTitleChange(string newTitle, bool showPaused)
    {
        // 200ms slide/fade transition (simple and reliable).
        var duration = TimeSpan.FromMilliseconds(200);
        var tt = SlideTransform;

        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(100));
        fadeOut.Completed += (_, _) =>
        {
            TitleText.Text = newTitle;
            if (showPaused)
            {
                SubtitleText.Text = "Your keyboard is paused";
                SubtitleText.Visibility = Visibility.Visible;
            }
            else
            {
                SubtitleText.Text = string.Empty;
                SubtitleText.Visibility = Visibility.Collapsed;
            }
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(100));
            MainView.BeginAnimation(OpacityProperty, fadeIn);
        };

        // Slide a little to make the handoff feel tangible.
        var slide = new DoubleAnimation(0, -12, duration) { AutoReverse = true };
        tt.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, slide);
        MainView.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void BuildQueue()
    {
        QueueList.Children.Clear();
        for (var i = 0; i < _queue.Count; i++)
        {
            QueueList.Children.Add(new TextBlock
            {
                Text = $"{i + 1}. {_queue[i]}",
                Foreground = (System.Windows.Media.Brush)FindResource("PtsBrushTextPrimary"),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 2)
            });
        }
        QueueHeader.Text = _queue.Count == 0 ? "Queue" : "Up next";
    }

    private void OverlayWindow_MouseEnter(object sender, MouseEventArgs e)
    {
        ExpandedPanel.Visibility = Visibility.Visible;
        Height = 190;
        Width = 320;
    }

    private void OverlayWindow_MouseLeave(object sender, MouseEventArgs e)
    {
        ExpandedPanel.Visibility = Visibility.Collapsed;
        Height = 56;
        Width = 280;
    }

    private void OverlayWindow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void PassButton_Click(object sender, RoutedEventArgs e)
    {
        try { PassRequested?.Invoke(); } catch { }
    }
}
