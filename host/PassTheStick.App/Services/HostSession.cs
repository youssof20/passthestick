using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PassTheStick.Core;
using PassTheStick.Dsu;
using PassTheStick.Net;
using PassTheStick.Platform;
using PassTheStick.Windows;

namespace PassTheStick.App.Services;

/// <summary>
/// Owns one hosting session from "create room" to "end": the stick state, the input pipeline and its pump
/// thread, the room connection, WebRTC peers, the virtual pad driver and the motion server. Start and stop can
/// repeat; each start builds a fresh set.
/// </summary>
public sealed partial class HostSession : IHostSession
{
    private readonly SettingsStore _store;
    private readonly RoomVault _vault;
    private readonly ILoggerFactory _logs;
    private readonly ILogger _log;
    private readonly WindowTracker _tracker = new();
    private readonly Lock _gate = new();
    private string _gameTitle = string.Empty;
    private TurnMode _mode;
    private int _turnSeconds;
    private Running? _running;

    public HostSession(SettingsStore store, RoomVault vault, ILoggerFactory logs)
    {
        _store = store;
        _vault = vault;
        _logs = logs;
        _log = logs.CreateLogger<HostSession>();
    }

    public event Action<SessionPhase, string?>? PhaseChanged;

    public event Action<string>? RoomCreated;

    public event Action? PlayersChanged;

    public event Action<string>? StickRequested;

    public event Action<ProbeInfo>? ProbeChanged;

    public string? PadProblem { get; private set; }

    public bool HostHoldsStick => _running?.Session.HostHoldsStick ?? true;

    public IReadOnlyList<PlayerSnapshot> Players
    {
        get
        {
            var run = _running;
            if (run is null)
            {
                return [];
            }

            var holder = run.Session.Holder;
            var away = run.Away;
            return [.. run.Session.Players.Select(p => new PlayerSnapshot(p.Id, p.Name, p.Slot, away.Contains(p.Id), p.Id == holder))];
        }
    }

    public GameStatus Game => new(_tracker.IsPinned, _gameTitle, _tracker.IsGameForeground, _tracker.NeedsElevation(), _tracker.LooksExclusiveFullscreen);

    public TurnMode Mode => _running?.Session.Mode ?? _mode;

    public int TurnSeconds
    {
        get => _turnSeconds;
        set
        {
            _turnSeconds = Math.Max(0, value);
            if (_running?.TurnTimer is { } timer)
            {
                timer.Limit = TimeSpan.FromSeconds(_turnSeconds);
            }
        }
    }

    public int? TurnSecondsLeft => _running?.TurnTimer?.Remaining is { } left ? (int)Math.Ceiling(left.TotalSeconds) : null;

    public void SetMode(TurnMode mode)
    {
        _mode = mode;
        _running?.Session.SetMode(mode);
    }

    public async Task StartAsync(Uri server, CancellationToken cancellationToken)
    {
        if (_running is not null)
        {
            return;
        }

        var settings = _store.Load().Settings;
        PhaseChanged?.Invoke(SessionPhase.Starting, null);
        CreatedRoom room;
        try
        {
            room = await ResumeOrCreateAsync(server, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            LogCreateFailed(_log, ex, server);
            PhaseChanged?.Invoke(SessionPhase.Failed, $"Could not reach the server at {server.Host}. Check the server address and your connection, then try again.");
            return;
        }

        string? problem = null;
        var pad = settings.UseVirtualPads ? ViGemPadBackend.TryCreate(out problem) : null;
        PadProblem = problem;
        IVirtualPadBackend padBackend = pad is null ? new NoPadBackend() : pad;

        var session = new StickSession("host");
        var pipeline = new InputPipeline(session, new SendInputInjector(_logs.CreateLogger<SendInputInjector>()), padBackend);
        var wsBase = new UriBuilder(server) { Scheme = server.Scheme == Uri.UriSchemeHttps ? "wss" : "ws" }.Uri;
        var client = new RoomClient(new WebSocketFactory(), wsBase, room.Code, room.HostToken);
        var hub = new PeerHub(client, settings.IceServers, _logs.CreateLogger<PeerHub>());
        var controller = new HostSessionController(session, pipeline, client, hub, _logs.CreateLogger<HostSessionController>());
        var dsu = TryStartDsu();
        var run = new Running(session, pipeline, padBackend, client, hub, controller, dsu, new TimerResolution());
        run.Pass = new PassCoordinator(session, id => run.Away.Contains(id));
        run.TurnTimer = new TurnTimer(session, run.Pass) { Limit = TimeSpan.FromSeconds(_turnSeconds) };
        session.SetMode(_mode);
        _running = run;

        controller.MotionReceived += (id, motion) =>
        {
            if (run.Session.Players.FirstOrDefault(p => p.Id == id) is { } player)
            {
                run.Dsu?.Update(player.Slot, motion);
            }
        };
        if (pad is not null)
        {
            pad.Rumble += (slot, strong, weak) =>
            {
                if (run.Session.Players.FirstOrDefault(p => p.Slot == slot) is { } player)
                {
                    run.Hub.SendControl(player.Id, new JsonObject { ["t"] = "rumble", ["strong"] = strong, ["weak"] = weak, ["ms"] = 200 });
                }
            };
        }

        controller.ProbeChanged += p => ProbeChanged?.Invoke(new ProbeInfo(NameOf(run, p.GuestId), true, p.Pad, p.Keys));
        controller.TestModeChanged += (id, on) => ProbeChanged?.Invoke(new ProbeInfo(NameOf(run, id), on, PassTheStick.Protocol.PadState.Neutral, []));
        session.HolderChanged += _ => PlayersChanged?.Invoke();
        session.StickRequested += player => StickRequested?.Invoke(player.Name);
        client.Received += e => OnRoomEvent(run, e);

        run.Pump = Task.Run(() => PumpAsync(run, run.Stop.Token), CancellationToken.None);
        run.Room = Task.Run(() => client.RunAsync(run.Stop.Token), CancellationToken.None);
        RoomCreated?.Invoke(room.Code);
    }

    /// <summary>
    /// A room saved by an earlier run is taken over if the server still has it (the app crashed or was updated within the
    /// server's grace window); otherwise a new room is created. Guests of a resumed room reconnect on their own.
    /// </summary>
    private async Task<CreatedRoom> ResumeOrCreateAsync(Uri server, CancellationToken cancellationToken)
    {
        var wsBase = new UriBuilder(server) { Scheme = server.Scheme == Uri.UriSchemeHttps ? "wss" : "ws" }.Uri;
        var serverKey = server.ToString().TrimEnd('/');
        CreatedRoom room;
        if (_vault.Load() is { } saved
            && string.Equals(saved.Server, serverKey, StringComparison.OrdinalIgnoreCase)
            && await RoomClient.ProbeAsync(new WebSocketFactory(), wsBase, saved.Code, saved.HostToken, TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false))
        {
            room = new CreatedRoom(saved.Code, saved.HostToken);
            PhaseChanged?.Invoke(SessionPhase.Starting, "Picked up your previous session. Friends reconnect on their own.");
        }
        else
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            room = await RoomClient.CreateRoomAsync(http, server, cancellationToken).ConfigureAwait(false);
        }

        _vault.Save(new SavedRoom(serverKey, room.Code, room.HostToken));
        return room;
    }

    public async Task StopAsync()
    {
        Running? run;
        lock (_gate)
        {
            run = _running;
            _running = null;
        }

        if (run is null)
        {
            return;
        }

        _vault.Clear(); // ending the session on purpose is not a crash: do not offer to resume it
        await run.Stop.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(run.Room ?? Task.CompletedTask, run.Pump ?? Task.CompletedTask).ConfigureAwait(false);
        run.Pipeline.ReleaseAll(); // nothing may stay held, whatever state the session was in
        run.TurnTimer?.Dispose();
        run.Pass?.Dispose();
        run.Controller.Dispose();
        run.Hub.Dispose();
        run.Pipeline.Dispose();
        run.Dsu?.Dispose();
        (run.Pad as IDisposable)?.Dispose();
        run.Timer.Dispose();
        run.Stop.Dispose();
        PlayersChanged?.Invoke();
        PhaseChanged?.Invoke(SessionPhase.Idle, null);
    }

    public bool PassTo(string playerId) => _running?.Session.PassTo(playerId) ?? false;

    public bool TakeBack() => _running?.Session.TakeBack() ?? false;

    public PassOutcome PassNext() => _running?.Pass?.Pass() ?? PassOutcome.NoOneToPass;

    public bool TryGetGameRect(out (int Left, int Top, int Width, int Height) rect) =>
        _tracker.TryGetRect(out rect) && !_tracker.IsMinimized;

    public string? CheckPadDriver()
    {
        using var probe = ViGemPadBackend.TryCreate(out var problem);
        return problem;
    }

    public IReadOnlyList<WindowInfo> ListWindows() => WindowCatalog.List();

    public void PinWindow(nint handle)
    {
        _tracker.Pin(handle);
        _gameTitle = WindowCatalog.List().FirstOrDefault(w => w.Handle == handle)?.Title ?? "Game window";
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private void OnRoomEvent(Running run, RoomEvent e)
    {
        switch (e)
        {
            case PresenceEvent presence:
                run.Away = [.. presence.Players.Where(p => p.Away).Select(p => p.Id)];
                PlayersChanged?.Invoke();
                break;
            case ConnectionEvent { State: RoomConnectionState.Open }:
                PhaseChanged?.Invoke(SessionPhase.Live, null);
                break;
            case ConnectionEvent { State: RoomConnectionState.Reconnecting }:
                PhaseChanged?.Invoke(SessionPhase.Reconnecting, "Lost the server. Reconnecting. Your guests keep their place for a minute.");
                break;
            case RoomErrorEvent { Fatal: true } error:
                PhaseChanged?.Invoke(SessionPhase.Failed, ErrorText(error.Code));
                break;
            case EndedEvent:
                _vault.Clear();
                PhaseChanged?.Invoke(SessionPhase.Failed, "The server closed this session. Start a new one to get a new code.");
                break;
            default:
                break;
        }
    }

    private static string NameOf(Running run, string id) => run.Session.Players.FirstOrDefault(p => p.Id == id)?.Name ?? "A guest";

    private static string ErrorText(string code) => code switch
    {
        "bad_version" => "This app and the server speak different versions. Update PassTheStick, then start again.",
        "bad_token" or "room_not_found" => "The server no longer knows this session. Start a new one to get a new code.",
        _ => "The server refused the connection. Start a new session and try again.",
    };

    private async Task PumpAsync(Running run, CancellationToken token)
    {
        var pump = new InputPump(_tracker, run.Pipeline);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                pump.Step();
                run.Controller.FlushEchoes();
                run.Controller.Maintain();
                run.TurnTimer?.Tick();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private DsuServer? TryStartDsu()
    {
        try
        {
            var dsu = new DsuServer();
            dsu.Start();
            return dsu;
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            LogDsuUnavailable(_log, ex);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not create a room on {Server}")]
    private static partial void LogCreateFailed(ILogger logger, Exception ex, Uri server);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The motion server port is in use; phone motion will not reach emulators")]
    private static partial void LogDsuUnavailable(ILogger logger, Exception ex);

    private sealed class Running(
        StickSession session,
        InputPipeline pipeline,
        IVirtualPadBackend pad,
        RoomClient client,
        PeerHub hub,
        HostSessionController controller,
        DsuServer? dsu,
        TimerResolution timer)
    {
        public StickSession Session { get; } = session;

        public InputPipeline Pipeline { get; } = pipeline;

        public IVirtualPadBackend Pad { get; } = pad;

        public RoomClient Client { get; } = client;

        public PeerHub Hub { get; } = hub;

        public HostSessionController Controller { get; } = controller;

        public DsuServer? Dsu { get; } = dsu;

        public TimerResolution Timer { get; } = timer;

        public CancellationTokenSource Stop { get; } = new();

        public HashSet<string> Away { get; set; } = [];

        public PassCoordinator? Pass { get; set; }

        public TurnTimer? TurnTimer { get; set; }

        public Task? Pump { get; set; }

        public Task? Room { get; set; }
    }
}
