using System.Windows;

namespace PassTheStick.Shared;

public partial class RelayConnectionDialog : Window
{
    public string DefaultWs => Constants.DefaultRelayWs;
    public bool CanStartRelay => StartRelayRequested != null;

    public Action? StartRelayRequested { get; init; }

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

    private void StartRelay_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StartRelayRequested?.Invoke();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                "Couldn't start the local relay server.\n\n" + ex.Message,
                "PassTheStick",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }
}

