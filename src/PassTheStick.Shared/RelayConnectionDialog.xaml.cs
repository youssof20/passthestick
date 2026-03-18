using System.Windows;

namespace PassTheStick.Shared;

public partial class RelayConnectionDialog : Window
{
    public string DefaultWs => Constants.DefaultRelayWs;

    public RelayConnectionDialog()
    {
        InitializeComponent();
        DataContext = this;
    }

    public bool ShouldRetry { get; private set; }
    public bool ShouldChangeUrl { get; private set; }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        ShouldRetry = true;
        DialogResult = true;
        Close();
    }

    private void Change_Click(object sender, RoutedEventArgs e)
    {
        ShouldChangeUrl = true;
        DialogResult = true;
        Close();
    }
}

