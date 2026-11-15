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

/// <summary>One option in a segmented choice, such as a turn mode or a turn length.</summary>
public sealed partial class ChoiceViewModel : ObservableObject
{
    public ChoiceViewModel(string label, int value)
    {
        Label = label;
        Value = value;
    }

    public string Label { get; }

    public int Value { get; }

    [ObservableProperty]
    private bool _isSelected;
}

public sealed partial class PlayerViewModel : ObservableObject
{
    private readonly Action<string> _pass;

    public PlayerViewModel(PlayerSnapshot snapshot, Action<string> pass, bool everyone = false)
    {
        Everyone = everyone;
        Id = snapshot.Id;
        Name = snapshot.Name;
        SlotText = $"Player {snapshot.Slot + 1}";
        HasStick = snapshot.HasStick;
        Away = snapshot.Away;
        _pass = pass;
    }

    public string Id { get; }

    public string Name { get; }

    /// <summary>First letter of the name, shown in the avatar circle.</summary>
    public string Initial => Name.Length > 0 ? char.ToUpperInvariant(Name[0]).ToString() : "?";

    public string SlotText { get; }

    public bool HasStick { get; }

    public bool Away { get; }

    public bool Everyone { get; }

    public string Status => Away ? "Reconnecting" : Everyone ? "Playing" : HasStick ? "Has the stick" : "Waiting for a turn";

    /// <summary>Players take turns only in one-at-a-time mode.</summary>
    public bool ShowPass => !Everyone && !HasStick;

    public bool CanPass => !Everyone && !HasStick && !Away;

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
    private readonly UpdateCoordinator? _updates;
    private readonly IDiagnosticsService? _diagnostics;
    private AppSettings _settings;

    public MainViewModel(IHostSession session, IUiThread ui, IClipboardService clipboard, INotifier notifier, SettingsStore store, AppSettings settings, string? startupNotice, UpdateCoordinator? updates = null, IDiagnosticsService? diagnostics = null)
    {
        _updates = updates;
        _diagnostics = diagnostics;
        _session = session;
        _ui = ui;
        _clipboard = clipboard;
        _notifier = notifier;
        _store = store;
        _settings = settings;
        ServerUrl = settings.ServerUrl;
        PassHotkeyText = HotkeyBinding.TryParse(settings.PassHotkey, out var pass) ? pass.ToString() : HotkeyBinding.DefaultPass.ToString();
        TakeBackHotkeyText = HotkeyBinding.TryParse(settings.TakeBackHotkey, out var back) ? back.ToString() : HotkeyBinding.DefaultTakeBack.ToString();
        PassKeys = [.. PassHotkeyText.Split('+')];
        TakeBackKeys = [.. TakeBackHotkeyText.Split('+')];
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

        if (_updates is not null)
        {
            _updates.UpdateReady += version => _ui.Post(() => OnUpdateReady(version));
        }

        _session.PhaseChanged += (phase, message) => _ui.Post(() => OnPhase(phase, message));
        _session.RoomCreated += code => _ui.Post(() => RoomCode = code);
        _session.PlayersChanged += () => _ui.Post(RefreshPlayers);
        _session.StickRequested += name => _ui.Post(() => OnStickRequested(name));
        _session.ProbeChanged += info => _ui.Post(() => Probe.Apply(info));
        _session.TurnSeconds = settings.TurnSeconds;
        SyncChoices();
        RefreshWindows();
    }

    public ProbeViewModel Probe { get; } = new();

    public ObservableCollection<ChoiceViewModel> ModeChoices { get; } = [new("One at a time", (int)TurnMode.OneAtATime), new("Everyone at once", (int)TurnMode.Everyone)];

    public ObservableCollection<ChoiceViewModel> TimerChoices { get; } =
    [
        new("No limit", 0), new("30 s", 30), new("1 min", 60), new("2 min", 120), new("5 min", 300),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTurnControls))]
    private bool _isEveryone;

    /// <summary>Passing and timing only mean something when players take turns.</summary>
    public bool ShowTurnControls => !IsEveryone;

    [ObservableProperty]
    private string _turnLeftText = string.Empty;

    [RelayCommand]
    private void ChooseMode(ChoiceViewModel choice)
    {
        _session.SetMode((TurnMode)choice.Value);
        SyncChoices();
    }

    [RelayCommand]
    private void ChooseTimer(ChoiceViewModel choice)
    {
        _session.TurnSeconds = choice.Value;
        _settings = _settings with { TurnSeconds = choice.Value };
        TrySave();
        SyncChoices();
    }

    private void SyncChoices()
    {
        foreach (var c in ModeChoices)
        {
            c.IsSelected = c.Value == (int)_session.Mode;
        }

        foreach (var c in TimerChoices)
        {
            c.IsSelected = c.Value == _session.TurnSeconds;
        }

        IsEveryone = _session.Mode == TurnMode.Everyone;
    }

    /// <summary>Non-null until the first-run wizard is finished or skipped.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWizard))]
    private WizardViewModel? _wizard;

    public bool ShowWizard => Wizard is not null;

    public string PassHotkeyText { get; }

    public string TakeBackHotkeyText { get; }

    /// <summary>The pass hotkey split into key names, for drawing keycaps.</summary>
    public IReadOnlyList<string> PassKeys { get; }

    public IReadOnlyList<string> TakeBackKeys { get; }

    /// <summary>Closing the window while hosting hides it to the tray instead of ending the session.</summary>
    public bool ShouldHideOnClose => IsLive;

    public ObservableCollection<PlayerViewModel> Players { get; } = [];

    /// <summary>The open windows that match the search box.</summary>
    public ObservableCollection<WindowInfo> Windows { get; } = [];

    private List<WindowInfo> _allWindows = [];

    [ObservableProperty]
    private string _searchText = string.Empty;

    partial void OnSearchTextChanged(string value) => ApplyWindowFilter();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPicker))]
    private bool _isPickingGame;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPicker), nameof(NoGame), nameof(StartHint))]
    private bool _hasGame;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartHint))]
    private string _gameTitle = string.Empty;

    public bool NoGame => !HasGame;

    public string StartHint => HasGame
        ? $"Ready. Guests will send input to {GameTitle}."
        : "You can start now and pick the game window later.";

    /// <summary>The window list is always open before a session, and opens on demand once a game is chosen.</summary>
    public bool ShowPicker => IsIdle || IsPickingGame || !HasGame;

    [ObservableProperty]
    private bool _noticeIsProblem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsLive), nameof(CanStart), nameof(PhaseText), nameof(HasRoom), nameof(InviteLink), nameof(ShouldHideOnClose), nameof(ShowPicker))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(EndCommand), nameof(InstallUpdateCommand))]
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

    public bool HasPlayers => Players.Count > 0;

    public bool NoPlayers => Players.Count == 0;

    public string PlayersSummary => Players.Count switch
    {
        0 => "Nobody has joined yet",
        1 => "1 player",
        _ => $"{Players.Count} players",
    };

    public string InviteLink => HasRoom ? BuildInviteLink(_settings.SiteUrl, ServerUrl, RoomCode) : string.Empty;

    /// <summary>
    /// The link friends open: the guest page on the site, the room code in the fragment (so it never reaches the site's
    /// server), and the signaling server in the query only when it is not the one the page was built for.
    /// </summary>
    public static string BuildInviteLink(string siteUrl, string serverUrl, string code)
    {
        var site = siteUrl.TrimEnd('/');
        var server = serverUrl.Trim().TrimEnd('/');
        var query = string.Equals(server, AppSettings.DefaultServerUrl, StringComparison.OrdinalIgnoreCase) || !Uri.TryCreate(server, UriKind.Absolute, out var uri)
            ? string.Empty
            : "?server=" + (uri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws") + "://" + uri.Authority;
        return $"{site}/join/{query}#{code}";
    }

    public string PhaseText => Phase switch
    {
        SessionPhase.Idle => "Not hosting",
        SessionPhase.Starting => "Starting",
        SessionPhase.Live => "Live",
        SessionPhase.Reconnecting => "Reconnecting",
        _ => "Stopped",
    };

    private void Say(string? text)
    {
        Notice = text;
        NoticeIsProblem = false;
    }

    private void Warn(string text)
    {
        Notice = text;
        NoticeIsProblem = true;
    }

    [RelayCommand]
    private void ExportDiagnostics()
    {
        if (_diagnostics is null)
        {
            return;
        }

        try
        {
            var path = _diagnostics.Export();
            Say($"Saved {Path.GetFileName(path)} to your desktop. Tokens, room codes, addresses and your user name are removed. Attach it to a GitHub issue.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn("Could not save the diagnostics file. Check that your desktop folder is writable.");
        }
    }

    [RelayCommand]
    private void DismissNotice() => Say(null);

    [RelayCommand]
    private void ChangeGame() => IsPickingGame = !IsPickingGame;

    [RelayCommand]
    private void CopyCode()
    {
        if (_clipboard.TrySetText(RoomCode))
        {
            Say("Room code copied.");
        }
        else
        {
            Warn("Could not reach the clipboard. Select the code and copy it by hand.");
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (!Uri.TryCreate(ServerUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            Warn("The server address needs to start with http:// or https://.");
            return;
        }

        Say(null);
        _settings = _settings with { ServerUrl = uri.ToString().TrimEnd('/') };
        TrySave();
        await _session.StartAsync(uri, CancellationToken.None);
    }

    [RelayCommand(CanExecute = nameof(IsLive))]
    private async Task EndAsync() => await _session.StopAsync();

    [RelayCommand]
    private void CopyInvite()
    {
        if (_clipboard.TrySetText(InviteLink))
        {
            Say("Invite link copied. Paste it into Discord.");
        }
        else
        {
            Warn("Could not reach the clipboard. Select the link and copy it by hand.");
        }
    }

    [RelayCommand]
    private void PassNext()
    {
        switch (_session.PassNext())
        {
            case PassOutcome.ApprovedRequest:
                Say("Passed the stick to the guest who asked.");
                break;
            case PassOutcome.NotApplicable:
                Say("Everyone is playing at once, so there is no stick to pass. Switch to One at a time to take turns.");
                break;
            case PassOutcome.NoOneToPass:
                Say("Nobody else is in the session yet. Send them the invite link.");
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
            Warn(string.Join(" ", problems));
        }
    }

    /// <summary>The version waiting to be installed, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
    private string? _updateVersion;

    public bool HasUpdate => UpdateVersion is not null;

    [RelayCommand(CanExecute = nameof(CanInstallUpdate))]
    private void InstallUpdate()
    {
        if (_updates?.TryInstallNow() != true)
        {
            Say("The update installs when you close PassTheStick.");
        }
    }

    private bool CanInstallUpdate() => HasUpdate && !IsLive;

    private void OnUpdateReady(string version)
    {
        UpdateVersion = version;
        Say($"Version {version} is ready. It installs when you close PassTheStick, or press Restart to update when you are not hosting.");
    }

    private void OnStickRequested(string name)
    {
        Say($"{name} wants the stick. Press {PassHotkeyText} to give it to them.");
        _notifier.Show("PassTheStick", Notice!);
    }

    [RelayCommand]
    private void TakeBack()
    {
        if (!_session.TakeBack())
        {
            Say("You already have the stick.");
        }
    }

    [RelayCommand]
    private void RefreshWindows()
    {
        var keep = SelectedWindow?.Handle;
        _allWindows = [.. _session.ListWindows()];
        ApplyWindowFilter();
        SelectedWindow = Windows.FirstOrDefault(w => w.Handle == keep);
    }

    private void ApplyWindowFilter()
    {
        var keep = SelectedWindow?.Handle;
        var query = SearchText.Trim();
        Windows.Clear();
        foreach (var w in _allWindows.Where(w => query.Length == 0
            || w.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || w.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase)))
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
            Say(null);
            IsPickingGame = false;
        }
        catch (InvalidOperationException ex)
        {
            Warn(ex.Message);
            RefreshWindows();
        }

        RefreshGame();
    }

    private bool CanPin() => SelectedWindow is not null;

    /// <summary>Called a couple of times a second by the view's timer; keeps the game line and warning current.</summary>
    public void RefreshGame()
    {
        var game = _session.Game;
        HasGame = game.IsPinned;
        TurnLeftText = _session.TurnSecondsLeft is { } left ? $"{left} s left on this turn" : string.Empty;
        GameTitle = game.IsPinned ? game.Title : string.Empty;
        if (!game.IsPinned)
        {
            GameText = "No game window picked yet. Pick one so guests can send input to it.";
            GameWarning = string.Empty;
            GameWarningVisible = false;
            return;
        }

        GameText = game.IsForeground ? "Input is going to this window." : "Not in front. Input is paused until you click it.";
        GameWarningVisible = game.NeedsElevation || game.ExclusiveFullscreen;
        GameWarning = game.NeedsElevation
            ? "This game runs as administrator, so Windows blocks input from PassTheStick. Restart PassTheStick as administrator."
            : game.ExclusiveFullscreen
                ? "This game looks like it is in exclusive fullscreen, so the overlay cannot show over it. Switch the game to borderless or windowed to see who has the stick."
                : string.Empty;
    }

    private void OnPhase(SessionPhase phase, string? message)
    {
        Phase = phase;
        if (message is not null)
        {
            Notice = message;
            NoticeIsProblem = phase is SessionPhase.Failed;
        }

        if (phase is SessionPhase.Idle or SessionPhase.Failed)
        {
            RoomCode = string.Empty;
            RefreshPlayers();
        }

        if (phase is SessionPhase.Live && _session.PadProblem is { } problem)
        {
            Warn(problem);
        }
    }

    private void RefreshPlayers()
    {
        Players.Clear();
        foreach (var p in _session.Players)
        {
            Players.Add(new PlayerViewModel(p, id => _session.PassTo(id), _session.Mode == TurnMode.Everyone));
        }

        OnPropertyChanged(nameof(HasPlayers));
        OnPropertyChanged(nameof(NoPlayers));
        OnPropertyChanged(nameof(PlayersSummary));
        SyncChoices();
        HolderText = _session.Mode == TurnMode.Everyone ? "Everyone is playing" : _session.HostHoldsStick ? "You have the stick" : $"{_session.Players.FirstOrDefault(p => p.HasStick)?.Name ?? "A guest"} has the stick";
    }

    private void TrySave()
    {
        try
        {
            _store.Save(_settings);
        }
        catch (IOException)
        {
            Warn("Could not save your settings. The session still works.");
        }
    }
}
