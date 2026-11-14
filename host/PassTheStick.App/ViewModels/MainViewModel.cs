using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PassTheStick.App.Services;
using PassTheStick.Core;
using PassTheStick.Windows;

namespace PassTheStick.App.ViewModels;

/// <summary>Runs work on the UI thread. The WPF implementation wraps the Dispatcher; tests run it inline.</summary>
public interface IUiThread
{
    void Post(Action action);
}

/// <summary>Shows a short message outside the window, as a tray balloon.</summary>
public interface INotifier
{
    void Show(string title, string text);
}

public interface IClipboardService
{
    bool TrySetText(string text);
}

public sealed partial class PlayerViewModel : ObservableObject
{
    private readonly Action<string> _pass;

    public PlayerViewModel(PlayerSnapshot snapshot, Action<string> pass)
    {
        Id = snapshot.Id;
        Name = snapshot.Name;
        HasStick = snapshot.HasStick;
        Away = snapshot.Away;
        _pass = pass;
    }

    public string Id { get; }

    public string Name { get; }

    public bool HasStick { get; }

    public bool Away { get; }

    public string Status => HasStick ? "Has the stick" : Away ? "Reconnecting" : "Waiting";

    public bool CanPass => !HasStick && !Away;

    [RelayCommand(CanExecute = nameof(CanPass))]
    private void Pass() => _pass(Id);
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IHostSession _session;
    private readonly IUiThread _ui;
    private readonly IClipboardService _clipboard;
    private readonly INotifier _notifier;
    private readonly SettingsStore _store;
    private AppSettings _settings;

    public MainViewModel(IHostSession session, IUiThread ui, IClipboardService clipboard, INotifier notifier, SettingsStore store, AppSettings settings, string? startupNotice)
    {
        _session = session;
        _ui = ui;
        _clipboard = clipboard;
        _notifier = notifier;
        _store = store;
        _settings = settings;
        ServerUrl = settings.ServerUrl;
        PassHotkeyText = HotkeyBinding.TryParse(settings.PassHotkey, out var pass) ? pass.ToString() : HotkeyBinding.DefaultPass.ToString();
        TakeBackHotkeyText = HotkeyBinding.TryParse(settings.TakeBackHotkey, out var back) ? back.ToString() : HotkeyBinding.DefaultTakeBack.ToString();
        Notice = startupNotice;
        if (!settings.OnboardingCompleted)
        {
            Wizard = new WizardViewModel(session, store, settings, handle =>
            {
                _session.PinWindow(handle);
                RefreshGame();
            });
            Wizard.Completed += chosen =>
            {
                _settings = chosen;
                Wizard = null;
                RefreshWindows();
                RefreshGame();
            };
        }

        _session.PhaseChanged += (phase, message) => _ui.Post(() => OnPhase(phase, message));
        _session.RoomCreated += code => _ui.Post(() => RoomCode = code);
        _session.PlayersChanged += () => _ui.Post(RefreshPlayers);
        _session.StickRequested += name => _ui.Post(() => OnStickRequested(name));
        _session.ProbeChanged += info => _ui.Post(() => Probe.Apply(info));
        RefreshWindows();
    }

    public ProbeViewModel Probe { get; } = new();

    /// <summary>Non-null until the first-run wizard is finished or skipped.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWizard))]
    private WizardViewModel? _wizard;

    public bool ShowWizard => Wizard is not null;

    public string PassHotkeyText { get; }

    public string TakeBackHotkeyText { get; }

    /// <summary>Closing the window while hosting hides it to the tray instead of ending the session.</summary>
    public bool ShouldHideOnClose => IsLive;

    public ObservableCollection<PlayerViewModel> Players { get; } = [];

    public ObservableCollection<WindowInfo> Windows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsLive), nameof(CanStart), nameof(PhaseText), nameof(HasRoom), nameof(InviteLink), nameof(ShouldHideOnClose))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(EndCommand))]
    private SessionPhase _phase = SessionPhase.Idle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InviteLink), nameof(HasRoom))]
    private string _roomCode = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string? _notice;

    [ObservableProperty]
    private string _serverUrl;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PinCommand))]
    private WindowInfo? _selectedWindow;

    [ObservableProperty]
    private string _holderText = "You have the stick";

    [ObservableProperty]
    private string _gameText = "No game window picked yet.";

    [ObservableProperty]
    private bool _gameWarningVisible;

    [ObservableProperty]
    private string _gameWarning = string.Empty;

    public bool IsIdle => Phase is SessionPhase.Idle or SessionPhase.Failed;

    public bool IsLive => Phase is SessionPhase.Live or SessionPhase.Reconnecting;

    public bool CanStart => IsIdle;

    public bool HasRoom => RoomCode.Length > 0 && IsLive;

    public bool HasNotice => !string.IsNullOrEmpty(Notice);

    public string InviteLink => HasRoom ? $"{InviteBase}/join#{RoomCode}" : string.Empty;

    /// <summary>The page that hosts the web guest. Defaults to the server address; the release build points it at the site.</summary>
    public string InviteBase => ServerUrl.TrimEnd('/');

    public string PhaseText => Phase switch
    {
        SessionPhase.Idle => "Not hosting",
        SessionPhase.Starting => "Starting",
        SessionPhase.Live => "Live",
        SessionPhase.Reconnecting => "Reconnecting",
        _ => "Stopped",
    };

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (!Uri.TryCreate(ServerUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            Notice = "The server address needs to start with http:// or https://.";
            return;
        }

        Notice = null;
        _settings = _settings with { ServerUrl = uri.ToString().TrimEnd('/') };
        TrySave();
        await _session.StartAsync(uri, CancellationToken.None);
    }

    [RelayCommand(CanExecute = nameof(IsLive))]
    private async Task EndAsync() => await _session.StopAsync();

    [RelayCommand]
    private void CopyInvite()
    {
        Notice = _clipboard.TrySetText(InviteLink)
            ? "Invite link copied. Paste it into Discord."
            : "Could not reach the clipboard. Select the link and copy it by hand.";
    }

    [RelayCommand]
    private void PassNext()
    {
        switch (_session.PassNext())
        {
            case PassOutcome.ApprovedRequest:
                Notice = "Passed the stick to the guest who asked.";
                break;
            case PassOutcome.NoOneToPass:
                Notice = "Nobody else is in the session yet. Send them the invite link.";
                break;
            default:
                break;
        }
    }

    /// <summary>Called once at startup with one line per hotkey that could not be registered.</summary>
    public void ReportHotkeyProblems(IReadOnlyList<string> problems)
    {
        if (problems.Count > 0)
        {
            Notice = string.Join(" ", problems);
        }
    }

    private void OnStickRequested(string name)
    {
        Notice = $"{name} wants the stick. Press {PassHotkeyText} to give it to them.";
        _notifier.Show("PassTheStick", Notice);
    }

    [RelayCommand]
    private void TakeBack()
    {
        if (!_session.TakeBack())
        {
            Notice = "You already have the stick.";
        }
    }

    [RelayCommand]
    private void RefreshWindows()
    {
        var keep = SelectedWindow?.Handle;
        Windows.Clear();
        foreach (var w in _session.ListWindows())
        {
            Windows.Add(w);
        }

        SelectedWindow = Windows.FirstOrDefault(w => w.Handle == keep);
    }

    [RelayCommand(CanExecute = nameof(CanPin))]
    private void Pin()
    {
        try
        {
            _session.PinWindow(SelectedWindow!.Handle);
            Notice = null;
        }
        catch (InvalidOperationException ex)
        {
            Notice = ex.Message;
            RefreshWindows();
        }

        RefreshGame();
    }

    private bool CanPin() => SelectedWindow is not null;

    /// <summary>Called a couple of times a second by the view's timer; keeps the game line and warning current.</summary>
    public void RefreshGame()
    {
        var game = _session.Game;
        if (!game.IsPinned)
        {
            GameText = "No game window picked yet. Pick one so guests can send input to it.";
            GameWarning = string.Empty;
            GameWarningVisible = false;
            return;
        }

        GameText = game.IsForeground ? $"Sending input to {game.Title}" : $"{game.Title} is not in front. Input is paused until you click it.";
        GameWarningVisible = game.NeedsElevation;
        GameWarning = game.NeedsElevation
            ? "This game runs as administrator, so Windows blocks input from PassTheStick. Restart PassTheStick as administrator."
            : string.Empty;
    }

    private void OnPhase(SessionPhase phase, string? message)
    {
        Phase = phase;
        if (message is not null)
        {
            Notice = message;
        }

        if (phase is SessionPhase.Idle or SessionPhase.Failed)
        {
            RoomCode = string.Empty;
            RefreshPlayers();
        }

        if (phase is SessionPhase.Live && _session.PadProblem is { } problem)
        {
            Notice = problem;
        }
    }

    private void RefreshPlayers()
    {
        Players.Clear();
        foreach (var p in _session.Players)
        {
            Players.Add(new PlayerViewModel(p, id => _session.PassTo(id)));
        }

        HolderText = _session.HostHoldsStick ? "You have the stick" : $"{_session.Players.FirstOrDefault(p => p.HasStick)?.Name ?? "A guest"} has the stick";
    }

    private void TrySave()
    {
        try
        {
            _store.Save(_settings);
        }
        catch (IOException)
        {
            Notice = "Could not save your settings. The session still works.";
        }
    }
}
