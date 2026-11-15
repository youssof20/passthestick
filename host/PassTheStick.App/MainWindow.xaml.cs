using System.Windows;
using System.Windows.Interop;
using PassTheStick.Windows;

namespace PassTheStick.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => TitleBar.UseDark(new WindowInteropHelper(this).Handle, !App.IsLightTheme);
    }
}
