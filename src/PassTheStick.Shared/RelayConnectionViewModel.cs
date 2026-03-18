using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PassTheStick.Shared;

public sealed class RelayConnectionViewModel : INotifyPropertyChanged
{
    private string _statusText = "Connecting…";
    private bool _isConnected;
    private bool _isBusy;
    private bool _isUrlEditorOpen;
    private string _relayUrl = Constants.RelayWebSocketUrl;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<string> LogLines { get; } = new();

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    public bool IsConnected
    {
        get => _isConnected;
        set { _isConnected = value; OnPropertyChanged(); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanClickButtons)); }
    }

    public bool IsUrlEditorOpen
    {
        get => _isUrlEditorOpen;
        set { _isUrlEditorOpen = value; OnPropertyChanged(); }
    }

    public string RelayUrl
    {
        get => _relayUrl;
        set { _relayUrl = value; OnPropertyChanged(); }
    }

    public bool CanClickButtons => !IsBusy;

    public Func<Task>? StartRelayAsync { get; set; }
    public Func<Task>? RetryAsync { get; set; }
    public Action? CloseRequested { get; set; }
    public Action<string>? SaveRelayUrl { get; set; }

    public void AddLog(string line)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss");
        LogLines.Add($"{ts} - {line}");
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

