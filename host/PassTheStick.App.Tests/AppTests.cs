using PassTheStick.App.Services;
using PassTheStick.App.ViewModels;
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

internal sealed class FakeSession : IHostSession
{
    public event Action<SessionPhase, string?>? PhaseChanged;

    public event Action<string>? RoomCreated;

    public event Action? PlayersChanged;

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
        new(_session, new InlineUi(), _clipboard, _store, settings ?? new AppSettings(), notice);

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
        Assert.Equal("https://pts.example.com/join#K7M2QX", vm.InviteLink);
        Assert.Equal(new Uri("https://pts.example.com/"), _session.StartedWith);
        Assert.Equal("https://pts.example.com", _store.Load().Settings.ServerUrl);
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
        Assert.Equal(["Has the stick", "Waiting", "Reconnecting"], vm.Players.Select(p => p.Status));
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
        Assert.Equal("Sending input to Dolphin", vm.GameText);
        Assert.False(vm.GameWarningVisible);

        _session.Game = new GameStatus(true, "Dolphin", false, true);
        vm.RefreshGame();
        Assert.Contains("not in front", vm.GameText);
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
