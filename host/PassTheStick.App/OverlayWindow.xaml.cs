using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using PassTheStick.Windows;

namespace PassTheStick.App;

/// <summary>
/// A small pill over the game that says who has the stick. Click-through, never focusable, never in Alt+Tab.
/// Works over borderless windows; exclusive fullscreen games draw over it and it cannot help there.
/// </summary>
public partial class OverlayWindow : Window
{
    private string _text = string.Empty;

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => OverlayStyle.MakePassive(new WindowInteropHelper(this).Handle);
    }

    /// <summary>Updates the label. When the holder changes the pill slides in over the token's handoff duration, or just switches if animations are off.</summary>
    public void SetText(string text)
    {
        if (text == _text)
        {
            return;
        }

        var changed = _text.Length > 0;
        _text = text;
        Label.Text = text;
        if (changed && SystemParameters.ClientAreaAnimation && TryFindResource("PtsMotionHandoff") is Duration duration)
        {
            var slide = new DoubleAnimation(-24, 0, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Slide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, slide);
            Pill.BeginAnimation(OpacityProperty, new DoubleAnimation(0.3, 1, duration));
        }
    }
}
