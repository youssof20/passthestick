using PassTheStick.App.Services;
using PassTheStick.App.ViewModels;
using PassTheStick.Core;
using PassTheStick.Protocol;
using PassTheStick.Windows;

namespace PassTheStick.App.Tests;

internal sealed class InlineUi : IUiThread
{
    public void Post(Action action) => action();
}

internal sealed class FakeClipboard : IClipboardService
{
    public bool Works { get; set; } = true;

    public string? Text { get; private set; }

    public bool TrySetText(string text)
    {
        Text = Works ? text : null;
        return Works;
    }
}

internal sealed class FakeNotifier : INotifier
{
    public List<string> Shown { get; } = [];

    public void Show(string title, string text) => Shown.Add(text);
}

internal sealed class FakeSession : IHostSession
{
    public event Action<SessionPhase, string?>? PhaseChanged;

    public event Action<string>? RoomCreated;

    public event Action? PlayersChanged;

    public event Action<string>? StickRequested;

    public event Action<ProbeInfo>? ProbeChanged;

    public string? PadDriverProblem { get; set; }

    public string? CheckPadDriver() => PadDriverProblem;

    public void RaiseProbe(ProbeInfo info) => ProbeChanged?.Invoke(info);

    public string? PadProblem { get; set; }

    public IReadOnlyList<PlayerSnapshot> Players { get; set; } = [];

    public bool HostHoldsStick { get; set; } = true;

    public GameStatus Game { get; set; } = new(false, string.Empty, false, false);

    public List<WindowInfo> Windows { get; } = [new WindowInfo(1, "Dolphin", "Dolphin"), new WindowInfo(2, "Notes", "notepad")];

    public Uri? StartedWith { get; private set; }

    public bool Stopped { get; private set; }

    public List<string> Passed { get; } = [];

    public bool PinFails { get; set; }

    public nint Pinned { get; private set; }

    public Task StartAsync(Uri server, CancellationToken cancellationToken)
    {
        StartedWith = server;
        PhaseChanged?.Invoke(SessionPhase.Starting, null);
        RoomCreated?.Invoke("K7M2QX");
        PhaseChanged?.Invoke(SessionPhase.Live, null);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        Stopped = true;
        PhaseChanged?.Invoke(SessionPhase.Idle, null);
        return Task.CompletedTask;
    }

    public bool PassTo(string playerId)
    {
        Passed.Add(playerId);
        return true;
    }

    public bool TakeBack() => !HostHoldsStick;

    public PassOutcome Outcome { get; set; } = PassOutcome.PassedToNext;

    public PassOutcome PassNext() => Outcome;

    public bool TryGetGameRect(out (int Left, int Top, int Width, int Height) rect)
    {
        rect = (0, 0, 100, 100);
        return true;
    }

    public void RaiseRequest(string name) => StickRequested?.Invoke(name);

    public IReadOnlyList<WindowInfo> ListWindows() => Windows;

    public void PinWindow(nint handle)
    {
        if (PinFails)
        {
            throw new InvalidOperationException("That window is gone. Pick the game window again.");
        }

        Pinned = handle;
    }

    public void Raise(SessionPhase phase, string? message) => PhaseChanged?.Invoke(phase, message);

    public void RaisePlayers() => PlayersChanged?.Invoke();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class MainViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pts-app-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSession _session = new();
    private readonly FakeClipboard _clipboard = new();
    private readonly FakeNotifier _notifier = new();
    private readonly SettingsStore _store;

    public MainViewModelTests() => _store = new SettingsStore(Path.Combine(_dir, "settings.json"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private MainViewModel Make(AppSettings? settings = null, string? notice = null) =>
        new(_session, new InlineUi(), _clipboard, _notifier, _store, settings ?? new AppSettings(), notice);

    [Fact]
    public void Starts_idle_with_the_open_windows_listed_and_nothing_selected()
    {
        var vm = Make();
        Assert.True(vm.IsIdle);
        Assert.False(vm.IsLive);
        Assert.Equal(2, vm.Windows.Count);
        Assert.Null(vm.SelectedWindow);
        Assert.False(vm.PinCommand.CanExecute(null));
    }

    [Fact]
    public async Task Starting_shows_the_code_and_an_invite_link_and_saves_the_server_address()
    {
        var vm = Make();
        vm.ServerUrl = "https://pts.example.com/";
        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.IsLive);
        Assert.Equal("K7M2QX", vm.RoomCode);
        Assert.Equal("https://youssof20.github.io/passthestick/join/?server=wss://pts.example.com#K7M2QX", vm.InviteLink);
        Assert.Equal(new Uri("https://pts.example.com/"), _session.StartedWith);
        Assert.Equal("https://pts.example.com", _store.Load().Settings.ServerUrl);
    }

    [Fact]
    public async Task The_invite_link_change_is_announced_when_the_session_goes_live_not_only_when_the_code_arrives()
    {
        var vm = Make();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(changed.LastIndexOf(nameof(MainViewModel.InviteLink)) > changed.IndexOf(nameof(MainViewModel.RoomCode)));
        Assert.Contains(nameof(MainViewModel.HasRoom), changed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://example.com")]
    public async Task A_bad_server_address_is_refused_with_a_message_and_nothing_starts(string url)
    {
        var vm = Make();
        vm.ServerUrl = url;
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Null(_session.StartedWith);
        Assert.Contains("http", vm.Notice);
        Assert.True(vm.IsIdle);
    }

    [Fact]
    public async Task Ending_the_session_clears_the_code_and_players()
    {
        var vm = Make();
        await vm.StartCommand.ExecuteAsync(null);
        _session.Players = [new PlayerSnapshot("a", "Ann", 0, false, false)];
        _session.RaisePlayers();
        Assert.Single(vm.Players);
        await vm.EndCommand.ExecuteAsync(null);
        Assert.True(_session.Stopped);
        Assert.Equal(string.Empty, vm.RoomCode);
        Assert.Equal(string.Empty, vm.InviteLink);
        Assert.True(vm.IsIdle);
    }

    [Fact]
    public async Task Start_is_not_available_while_live_and_end_is_not_available_while_idle()
    {
        var vm = Make();
        Assert.False(vm.EndCommand.CanExecute(null));
        await vm.StartCommand.ExecuteAsync(null);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.True(vm.EndCommand.CanExecute(null));
    }

    [Fact]
    public void A_failed_session_shows_the_reason_and_lets_the_person_try_again()
    {
        var vm = Make();
        _session.Raise(SessionPhase.Failed, "Could not reach the server at example.com. Check the server address and your connection, then try again.");
        Assert.True(vm.IsIdle);
        Assert.Contains("Could not reach", vm.Notice);
        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Players_show_who_holds_the_stick_and_pass_goes_to_the_right_player()
    {
        var vm = Make();
        await vm.StartCommand.ExecuteAsync(null);
        _session.Players = [new PlayerSnapshot("a", "Ann", 0, false, true), new PlayerSnapshot("b", "Bo", 1, false, false), new PlayerSnapshot("c", "Cy", 2, true, false)];
        _session.HostHoldsStick = false;
        _session.RaisePlayers();

        Assert.Equal("Ann has the stick", vm.HolderText);
        Assert.Equal(["Has the stick", "Waiting for a turn", "Reconnecting"], vm.Players.Select(p => p.Status));
        Assert.Equal([false, true, false], vm.Players.Select(p => p.PassCommand.CanExecute(null)));

        vm.Players[1].PassCommand.Execute(null);
        Assert.Equal(["b"], _session.Passed);
    }

    [Fact]
    public void Taking_the_stick_back_when_you_already_hold_it_says_so()
    {
        var vm = Make();
        vm.TakeBackCommand.Execute(null);
        Assert.Equal("You already have the stick.", vm.Notice);
    }

    [Fact]
    public void Copying_the_invite_reports_success_and_failure_in_plain_words()
    {
        var vm = Make();
        _session.Raise(SessionPhase.Live, null);
        vm.RoomCode = "K7M2QX";
        vm.CopyInviteCommand.Execute(null);
        Assert.Equal(vm.InviteLink, _clipboard.Text);
        Assert.Contains("copied", vm.Notice);

        _clipboard.Works = false;
        vm.CopyInviteCommand.Execute(null);
        Assert.Contains("by hand", vm.Notice);
    }

    [Fact]
    public void Pinning_a_window_uses_the_selected_one_and_a_vanished_window_gives_a_clear_message()
    {
        var vm = Make();
        vm.SelectedWindow = vm.Windows[0];
        Assert.True(vm.PinCommand.CanExecute(null));
        vm.PinCommand.Execute(null);
        Assert.Equal((nint)1, _session.Pinned);

        _session.PinFails = true;
        vm.SelectedWindow = vm.Windows[1];
        vm.PinCommand.Execute(null);
        Assert.Contains("Pick the game window again", vm.Notice);
    }

    [Fact]
    public void The_game_line_tracks_focus_and_warns_when_the_game_needs_admin()
    {
        var vm = Make();
        vm.RefreshGame();
        Assert.Contains("No game window", vm.GameText);

        _session.Game = new GameStatus(true, "Dolphin", true, false);
        vm.RefreshGame();
        Assert.Equal("Input is going to this window.", vm.GameText);
        Assert.Equal("Dolphin", vm.GameTitle);
        Assert.False(vm.GameWarningVisible);

        _session.Game = new GameStatus(true, "Dolphin", false, true);
        vm.RefreshGame();
        Assert.Contains("Not in front", vm.GameText);
        Assert.True(vm.GameWarningVisible);
        Assert.Contains("administrator", vm.GameWarning);
    }

    [Fact]
    public async Task A_missing_virtual_pad_driver_is_explained_once_the_session_is_live()
    {
        _session.PadProblem = "ViGEmBus is not installed.";
        var vm = Make();
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal("ViGEmBus is not installed.", vm.Notice);
    }

    [Fact]
    public void A_guest_request_tells_the_host_how_to_approve_it_in_the_window_and_the_tray()
    {
        var vm = Make();
        _session.RaiseRequest("Ann");
        Assert.Equal("Ann wants the stick. Press Ctrl+Shift+Right to give it to them.", vm.Notice);
        Assert.Equal([vm.Notice!], _notifier.Shown);
    }

    [Theory]
    [InlineData(PassOutcome.ApprovedRequest, "Passed the stick to the guest who asked.")]
    [InlineData(PassOutcome.NoOneToPass, "Nobody else is in the session yet. Send them the invite link.")]
    public void Passing_next_explains_what_happened_when_it_is_not_obvious(PassOutcome outcome, string expected)
    {
        var vm = Make();
        _session.Outcome = outcome;
        vm.PassNextCommand.Execute(null);
        Assert.Equal(expected, vm.Notice);
    }

    [Fact]
    public void A_plain_pass_to_the_next_player_adds_no_message()
    {
        var vm = Make();
        vm.PassNextCommand.Execute(null);
        Assert.Null(vm.Notice);
    }

    [Fact]
    public async Task Closing_the_window_hides_to_the_tray_only_while_hosting()
    {
        var vm = Make();
        Assert.False(vm.ShouldHideOnClose);
        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.ShouldHideOnClose);
    }

    [Fact]
    public void Hotkey_text_comes_from_settings_and_falls_back_to_the_defaults_when_it_is_invalid()
    {
        Assert.Equal("Ctrl+Alt+P", Make(new AppSettings { PassHotkey = "alt+ctrl+p" }).PassHotkeyText);
        Assert.Equal("Ctrl+Shift+Left", Make(new AppSettings { TakeBackHotkey = "P" }).TakeBackHotkeyText);
    }

    [Fact]
    public void Hotkey_problems_are_shown_in_the_notice_bar()
    {
        var vm = Make();
        vm.ReportHotkeyProblems(["Ctrl+Shift+Right is already used by another program."]);
        Assert.Contains("already used", vm.Notice);
        vm.ReportHotkeyProblems([]);
        Assert.Contains("already used", vm.Notice);
    }

    [Fact]
    public void A_startup_notice_from_the_settings_loader_is_shown()
    {
        Assert.Equal("Settings were reset.", Make(notice: "Settings were reset.").Notice);
    }
}

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pts-set-" + Guid.NewGuid().ToString("N"));
    private string Path_ => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private static void AssertDefaults(AppSettings s)
    {
        var d = new AppSettings();
        Assert.Equal(d.ServerUrl, s.ServerUrl);
        Assert.Equal(d.IceServers, s.IceServers);
        Assert.Null(s.LastGameProcess);
    }

    [Fact]
    public void First_run_with_no_file_gives_defaults_and_no_notice()
    {
        var result = new SettingsStore(Path_).Load();
        AssertDefaults(result.Settings);
        Assert.Null(result.Notice);
    }

    [Fact]
    public void Settings_round_trip_and_stamp_the_current_version()
    {
        var store = new SettingsStore(Path_);
        store.Save(new AppSettings { ServerUrl = "https://x.example", Version = 0, LastGameProcess = "Dolphin" });
        var loaded = store.Load().Settings;
        Assert.Equal("https://x.example", loaded.ServerUrl);
        Assert.Equal("Dolphin", loaded.LastGameProcess);
        Assert.Equal(AppSettings.CurrentVersion, loaded.Version);
    }

    [Fact]
    public void A_corrupt_file_is_kept_aside_defaults_are_used_and_the_person_is_told()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, "{ this is not json");
        var result = new SettingsStore(Path_).Load();
        AssertDefaults(result.Settings);
        Assert.Contains("could not be read", result.Notice);
        Assert.False(File.Exists(Path_));
        Assert.Single(Directory.GetFiles(_dir, "settings.json.damaged-*"));
    }

    [Fact]
    public void Saving_leaves_no_temp_file_behind()
    {
        new SettingsStore(Path_).Save(new AppSettings());
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void An_empty_json_file_counts_as_corrupt()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, "null");
        Assert.NotNull(new SettingsStore(Path_).Load().Notice);
    }
}


public class HotkeyBindingTests
{
    [Theory]
    [InlineData("Ctrl+Shift+Right", "Ctrl+Shift+Right")]
    [InlineData("shift+control+left", "Ctrl+Shift+Left")]
    [InlineData("Alt+F9", "Alt+F9")]
    [InlineData(" Ctrl + Shift + P ", "Ctrl+Shift+P")]
    [InlineData("Win+Ctrl+D5", "Ctrl+Win+D5")]
    public void Valid_combinations_parse_and_print_in_a_standard_order(string input, string expected)
    {
        Assert.True(HotkeyBinding.TryParse(input, out var binding));
        Assert.Equal(expected, binding.ToString());
        Assert.NotEqual(0u, binding.VirtualKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Right")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+Nonsense")]
    [InlineData("Ctrl+None")]
    public void A_bare_key_missing_key_or_unknown_name_is_rejected(string? input) =>
        Assert.False(HotkeyBinding.TryParse(input, out _));

    [Fact]
    public void The_default_pass_and_take_back_keys_do_not_collide()
    {
        Assert.NotEqual(HotkeyBinding.DefaultPass, HotkeyBinding.DefaultTakeBack);
        Assert.Equal(0x27u, HotkeyBinding.DefaultPass.VirtualKey);
        Assert.Equal(0x25u, HotkeyBinding.DefaultTakeBack.VirtualKey);
    }
}

public class OverlayPlacementTests
{
    [Fact]
    public void Sits_inside_the_top_left_of_the_game_window_with_a_margin()
    {
        Assert.Equal((116.0, 216.0), OverlayPlacement.Compute((100, 200, 1920, 1080), 300, 60, 16));
    }

    [Fact]
    public void Works_on_a_second_monitor_with_negative_coordinates()
    {
        Assert.Equal((-1904.0, 16.0), OverlayPlacement.Compute((-1920, 0, 1920, 1080), 300, 60, 16));
    }

    [Fact]
    public void Never_leaves_a_game_window_smaller_than_the_overlay()
    {
        var (x, y) = OverlayPlacement.Compute((50, 50, 200, 40), 300, 60, 16);
        Assert.Equal(50.0, x);
        Assert.Equal(50.0, y);
    }
}


public sealed class ProbeViewModelTests
{
    private static ProbeInfo Info(PadButtons b, short lx = 0, bool active = true, params string[] keys) =>
        new("Ann", active, new PadState(b, lx, 0, 0, 0, 0, 0), keys);

    [Fact]
    public void Starts_hidden_with_centered_meters()
    {
        var p = new ProbeViewModel();
        Assert.False(p.IsActive);
        Assert.Equal(0.5, p.LeftX);
        Assert.Equal(14, p.Buttons.Count);
    }

    [Fact]
    public void Lights_the_pressed_buttons_names_the_guest_and_lists_keys_in_order()
    {
        var p = new ProbeViewModel();
        p.Apply(Info(PadButtons.A | PadButtons.DpadLeft, 0, true, "Space", "KeyW"));
        Assert.True(p.IsActive);
        Assert.Equal("Testing Ann's controls", p.Title);
        Assert.Equal(["A", "Left"], p.Buttons.Where(b => b.IsDown).Select(b => b.Name));
        Assert.Equal("Keys down: KeyW, Space", p.KeysText);
    }

    [Fact]
    public void Axes_map_to_zero_through_one_with_centre_at_half()
    {
        var p = new ProbeViewModel();
        p.Apply(Info(PadButtons.None, short.MinValue));
        Assert.Equal(0.0, p.LeftX, 4);
        p.Apply(Info(PadButtons.None, short.MaxValue));
        Assert.Equal(1.0, p.LeftX, 4);
        p.Apply(Info(PadButtons.None, 0));
        Assert.Equal(0.5, p.LeftX, 2);
    }

    [Fact]
    public void Leaving_test_mode_clears_everything()
    {
        var p = new ProbeViewModel();
        p.Apply(Info(PadButtons.A, 20000, true, "KeyW"));
        p.Apply(Info(PadButtons.None, 0, false));
        Assert.False(p.IsActive);
        Assert.All(p.Buttons, b => Assert.False(b.IsDown));
        Assert.Equal("No keys down", p.KeysText);
        Assert.Equal(string.Empty, p.Title);
    }
}

public sealed class WizardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pts-wiz-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSession _session = new();
    private readonly SettingsStore _store;
    private readonly List<nint> _pinned = [];

    public WizardTests() => _store = new SettingsStore(Path.Combine(_dir, "settings.json"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private WizardViewModel Make() => new(_session, _store, new AppSettings(), _pinned.Add);

    [Fact]
    public void Walks_through_four_screens_and_back_is_only_available_after_the_first()
    {
        var w = Make();
        Assert.True(w.IsWelcome);
        Assert.False(w.BackCommand.CanExecute(null));
        w.NextCommand.Execute(null);
        Assert.True(w.IsGame);
        Assert.True(w.BackCommand.CanExecute(null));
        w.NextCommand.Execute(null);
        Assert.True(w.IsControls);
        w.NextCommand.Execute(null);
        Assert.True(w.IsDone);
        Assert.Equal("Done", w.NextText);
        w.BackCommand.Execute(null);
        Assert.True(w.IsControls);
    }

    [Fact]
    public void Finishing_with_the_defaults_saves_keyboard_only_and_marks_setup_complete()
    {
        var w = Make();
        AppSettings? chosen = null;
        w.Completed += c => chosen = c;
        for (var i = 0; i < 4; i++)
        {
            w.NextCommand.Execute(null);
        }

        Assert.True(chosen!.OnboardingCompleted);
        Assert.False(chosen.UseVirtualPads);
        var saved = _store.Load().Settings;
        Assert.True(saved.OnboardingCompleted);
        Assert.False(saved.UseVirtualPads);
    }

    [Fact]
    public void Skipping_saves_the_same_so_the_wizard_does_not_come_back()
    {
        var w = Make();
        w.SkipCommand.Execute(null);
        Assert.True(_store.Load().Settings.OnboardingCompleted);
    }

    [Fact]
    public void Picking_a_window_pins_it_and_remembers_the_title()
    {
        var w = Make();
        Assert.False(w.PickCommand.CanExecute(null));
        w.SelectedWindow = w.Windows[0];
        w.PickCommand.Execute(null);
        Assert.Equal([(nint)1], _pinned);
        Assert.Equal("Dolphin", w.PickedTitle);
    }

    [Fact]
    public void A_window_that_closed_gives_a_plain_message_and_refreshes_the_list()
    {
        var failing = new WizardViewModel(_session, _store, new AppSettings(), _ => throw new InvalidOperationException("That window is gone. Pick the game window again."));
        failing.SelectedWindow = failing.Windows[0];
        failing.PickCommand.Execute(null);
        Assert.Contains("Pick the game window again", failing.Error);
        Assert.Null(failing.PickedTitle);
    }

    [Fact]
    public void Keyboard_only_needs_no_driver_and_never_asks_the_session_about_one()
    {
        _session.PadDriverProblem = "ViGEmBus is not installed.";
        var w = Make();
        Assert.Equal("Keyboard only. Nothing to install.", w.DriverMessage);
        Assert.False(w.ShowDriverHelp);
    }

    [Fact]
    public void Controllers_without_the_driver_explain_what_to_get_where_and_that_a_restart_is_needed()
    {
        _session.PadDriverProblem = "ViGEmBus is not installed.";
        var w = Make();
        w.WantsControllers = true;
        Assert.True(w.ShowDriverHelp);
        Assert.Contains(WizardViewModel.DriverUrl, w.DriverMessage);
        Assert.Contains("restart your PC", w.DriverMessage);
        Assert.Contains("finish setup now", w.DriverMessage);
    }

    [Fact]
    public void Check_again_notices_a_driver_that_was_installed_while_the_wizard_was_open()
    {
        _session.PadDriverProblem = "ViGEmBus is not installed.";
        var w = Make();
        w.WantsControllers = true;
        _session.PadDriverProblem = null;
        w.CheckDriverCommand.Execute(null);
        Assert.False(w.ShowDriverHelp);
        Assert.Contains("installed", w.DriverMessage);
    }

    [Fact]
    public void Controllers_chosen_are_saved_even_when_the_driver_is_missing_so_it_can_be_added_later()
    {
        _session.PadDriverProblem = "ViGEmBus is not installed.";
        var w = Make();
        w.WantsControllers = true;
        w.SkipCommand.Execute(null);
        Assert.True(_store.Load().Settings.UseVirtualPads);
    }

    [Fact]
    public void The_main_window_shows_the_wizard_until_it_finishes_and_never_for_a_returning_user()
    {
        var first = new MainViewModel(_session, new InlineUi(), new FakeClipboard(), new FakeNotifier(), _store, new AppSettings(), null);
        Assert.True(first.ShowWizard);
        first.Wizard!.SkipCommand.Execute(null);
        Assert.False(first.ShowWizard);

        var returning = new MainViewModel(_session, new InlineUi(), new FakeClipboard(), new FakeNotifier(), _store, new AppSettings { OnboardingCompleted = true }, null);
        Assert.False(returning.ShowWizard);
    }

    [Fact]
    public void Probe_events_from_the_session_reach_the_main_view_model()
    {
        var vm = new MainViewModel(_session, new InlineUi(), new FakeClipboard(), new FakeNotifier(), _store, new AppSettings { OnboardingCompleted = true }, null);
        _session.RaiseProbe(new ProbeInfo("Bo", true, new PadState(PadButtons.B, 0, 0, 0, 0, 0, 0), []));
        Assert.True(vm.Probe.IsActive);
        Assert.Contains("Bo", vm.Probe.Title);
    }
}

public sealed class LayoutStateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pts-lay-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSession _session = new();
    private readonly FakeClipboard _clipboard = new();

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private MainViewModel Make() =>
        new(_session, new InlineUi(), _clipboard, new FakeNotifier(), new SettingsStore(Path.Combine(_dir, "s.json")), new AppSettings { OnboardingCompleted = true }, null);

    [Fact]
    public void Searching_filters_the_window_list_by_title_or_program_and_keeps_the_selection_when_it_still_matches()
    {
        var vm = Make();
        vm.SelectedWindow = vm.Windows.Single(w => w.Title == "Dolphin");
        vm.SearchText = "dol";
        Assert.Equal(["Dolphin"], vm.Windows.Select(w => w.Title));
        Assert.NotNull(vm.SelectedWindow);

        vm.SearchText = "NOTEPAD";
        Assert.Equal(["Notes"], vm.Windows.Select(w => w.Title));
        Assert.Null(vm.SelectedWindow);

        vm.SearchText = string.Empty;
        Assert.Equal(2, vm.Windows.Count);
    }

    [Fact]
    public async Task The_window_picker_is_open_until_a_game_is_chosen_then_opens_only_on_demand_during_a_session()
    {
        var vm = Make();
        Assert.True(vm.ShowPicker);

        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.ShowPicker); // live but no game yet

        _session.Game = new GameStatus(true, "Dolphin", true, false);
        vm.RefreshGame();
        Assert.False(vm.ShowPicker);
        Assert.True(vm.HasGame);

        vm.ChangeGameCommand.Execute(null);
        Assert.True(vm.ShowPicker);

        vm.SelectedWindow = vm.Windows[0];
        vm.PinCommand.Execute(null);
        Assert.False(vm.ShowPicker);
    }

    [Fact]
    public void The_start_hint_tells_the_person_what_will_happen()
    {
        var vm = Make();
        Assert.Contains("pick the game window later", vm.StartHint);
        _session.Game = new GameStatus(true, "Dolphin", true, false);
        vm.RefreshGame();
        Assert.Equal("Ready. Guests will send input to Dolphin.", vm.StartHint);
    }

    [Fact]
    public async Task Notices_are_marked_as_problems_only_when_something_went_wrong()
    {
        var vm = Make();
        _clipboard.Works = false;
        vm.RoomCode = "K7M2QX";
        await vm.StartCommand.ExecuteAsync(null);
        vm.CopyInviteCommand.Execute(null);
        Assert.True(vm.NoticeIsProblem);

        _clipboard.Works = true;
        vm.CopyInviteCommand.Execute(null);
        Assert.False(vm.NoticeIsProblem);

        _session.Raise(SessionPhase.Failed, "Could not reach the server.");
        Assert.True(vm.NoticeIsProblem);

        vm.DismissNoticeCommand.Execute(null);
        Assert.False(vm.HasNotice);
        Assert.False(vm.NoticeIsProblem);
    }

    [Fact]
    public async Task Players_summary_and_empty_state_follow_who_has_joined()
    {
        var vm = Make();
        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.NoPlayers);
        Assert.Equal("Nobody has joined yet", vm.PlayersSummary);

        _session.Players = [new PlayerSnapshot("a", "maya", 0, false, false)];
        _session.RaisePlayers();
        Assert.Equal("1 player", vm.PlayersSummary);
        Assert.True(vm.HasPlayers);
        Assert.Equal("M", vm.Players[0].Initial);
        Assert.Equal("Player 1", vm.Players[0].SlotText);

        _session.Players = [.. _session.Players, new PlayerSnapshot("b", "Jonas", 1, false, false)];
        _session.RaisePlayers();
        Assert.Equal("2 players", vm.PlayersSummary);
    }

    [Fact]
    public void Hotkeys_are_split_into_keycaps()
    {
        var vm = Make();
        Assert.Equal(["Ctrl", "Shift", "Right"], vm.PassKeys);
        Assert.Equal(["Ctrl", "Shift", "Left"], vm.TakeBackKeys);
    }

    [Fact]
    public void Copying_the_code_alone_works_and_reports_clipboard_trouble()
    {
        var vm = Make();
        vm.RoomCode = "K7M2QX";
        vm.CopyCodeCommand.Execute(null);
        Assert.Equal("K7M2QX", _clipboard.Text);
        Assert.Equal("Room code copied.", vm.Notice);
        _clipboard.Works = false;
        vm.CopyCodeCommand.Execute(null);
        Assert.True(vm.NoticeIsProblem);
    }
}


public class InviteLinkTests
{
    [Fact]
    public void The_default_server_needs_no_query_because_the_page_is_built_for_it()
    {
        Assert.Equal("https://site.example/join/#K7M2QX", MainViewModel.BuildInviteLink("https://site.example/", AppSettings.DefaultServerUrl, "K7M2QX"));
    }

    [Theory]
    [InlineData("https://rooms.example.com", "wss://rooms.example.com")]
    [InlineData("http://192.168.1.20:8787/", "ws://192.168.1.20:8787")]
    public void A_custom_server_travels_in_the_link_so_the_page_talks_to_the_right_one(string server, string expected)
    {
        Assert.Equal($"https://site.example/join/?server={expected}#ABCDEF", MainViewModel.BuildInviteLink("https://site.example", server, "ABCDEF"));
    }

    [Fact]
    public void An_unparseable_server_leaves_the_query_off_and_the_code_stays_in_the_fragment()
    {
        var link = MainViewModel.BuildInviteLink("https://site.example", "not a url", "ABCDEF");
        Assert.Equal("https://site.example/join/#ABCDEF", link);
        Assert.DoesNotContain("?", link.Split('#')[1], StringComparison.Ordinal);
    }
}
