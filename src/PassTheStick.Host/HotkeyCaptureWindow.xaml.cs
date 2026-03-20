using System.Windows;
using System.Windows.Input;
using PassTheStick.Shared;

namespace PassTheStick.Host;

public partial class HotkeyCaptureWindow : Window
{
    public event Action<uint, uint>? Completed;

    public HotkeyCaptureWindow()
    {
        InitializeComponent();
        PreviewText.Text = "Waiting…";
    }

    private void HotkeyCaptureWindow_Loaded(object sender, RoutedEventArgs e) =>
        Focus();

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }

        // Modifier-only chords — show live preview
        var mods = Win32ModsFromKeyboard();
        var key = e.Key;
        if (key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or
            Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            PreviewText.Text = string.IsNullOrEmpty(KeyNames.FormatRegisterHotKey(mods, 0))
                ? "…"
                : KeyNames.FormatRegisterHotKey(mods, 0) + " + …";
            return;
        }

        if (mods == 0)
        {
            e.Handled = true;
            PreviewText.Text = "Include Ctrl, Shift, Alt, and/or Win.";
            return;
        }

        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0)
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;
        Completed?.Invoke(mods, vk);
        Close();
    }

    private static uint Win32ModsFromKeyboard()
    {
        uint m = 0;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) m |= 0x0002;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) m |= 0x0004;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) m |= 0x0001;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) m |= 0x0008;
        return m;
    }
}
