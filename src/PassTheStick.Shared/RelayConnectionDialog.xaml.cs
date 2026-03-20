using System.Windows;

namespace PassTheStick.Shared;

public partial class RelayConnectionDialog : Window
{
    public RelayConnectionViewModel Vm { get; }

    public RelayConnectionDialog()
        : this(new RelayConnectionViewModel())
    {
    }

    public RelayConnectionDialog(RelayConnectionViewModel vm)
    {
        InitializeComponent();
        Vm = vm;
        DataContext = Vm;
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Vm.RetryAsync == null) return;
            await Vm.RetryAsync();
        }
        catch (Exception ex)
        {
            Vm.AddLog("Error: " + ex.Message);
        }
    }

    private async void StartRelay_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Vm.StartRelayAsync == null) return;
            await Vm.StartRelayAsync();
        }
        catch (Exception ex)
        {
            Vm.AddLog("Error: " + ex.Message);
        }
    }

    private void SaveUrl_Click(object sender, RoutedEventArgs e)
    {
        Vm.SaveRelayUrl?.Invoke(Vm.RelayUrl);
        Vm.AddLog("Relay URL saved.");
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Vm.CloseRequested?.Invoke();
        Close();
    }

    private void CopyLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(Vm.LogText ?? string.Empty);
            Vm.AddLog("Logs copied to clipboard.");
        }
        catch
        {
            // ignore clipboard failures
        }
    }
}

