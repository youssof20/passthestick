using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using DataChannelDotnet;
using DataChannelDotnet.Data;
using DataChannelDotnet.Impl;
using Microsoft.Extensions.Logging.Abstractions;
using PassTheStick.Core;
using PassTheStick.Net;
using PassTheStick.Platform;
using PassTheStick.Protocol;

namespace PassTheStick.Net.Tests;

/// <summary>
/// A UDP relay that sits between a real host peer and a real guest peer and drops, delays and reorders packets.
/// Candidates are rewritten so each side believes the relay is the other side. Windows has no built-in way to
/// impair traffic (no netem), so this stands in for a bad mobile connection.
/// </summary>
internal sealed class ImpairedUdpRelay : IDisposable
{
    private readonly UdpClient _towardsHost = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly UdpClient _towardsGuest = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly Random _rng;
    private readonly double _loss;
    private readonly int _baseDelayMs;
    private readonly int _jitterMs;
    private readonly CancellationTokenSource _stop = new();
    private IPEndPoint? _hostReal;
    private IPEndPoint? _guestReal;
    private int _forwarded;
    private int _dropped;

    public ImpairedUdpRelay(double loss, int baseDelayMs, int jitterMs, int seed)
    {
        _loss = loss;
        _baseDelayMs = baseDelayMs;
        _jitterMs = jitterMs;
        _rng = new Random(seed);
        // What the host sends arrives on its own socket and leaves from the guest-facing one towards the real guest, and the reverse.
        _ = Task.Run(() => Pump(_towardsHost, () => _guestReal, _towardsGuest));
        _ = Task.Run(() => Pump(_towardsGuest, () => _hostReal, _towardsHost));
    }

    /// <summary>The port the guest is told is the host.</summary>
    public int PortShownToGuest => ((IPEndPoint)_towardsGuest.Client.LocalEndPoint!).Port;

    /// <summary>The port the host is told is the guest.</summary>
    public int PortShownToHost => ((IPEndPoint)_towardsHost.Client.LocalEndPoint!).Port;

    public int Forwarded => Volatile.Read(ref _forwarded);

    public int Dropped => Volatile.Read(ref _dropped);

    /// <summary>Rewrites a host candidate for the guest, remembering where the host really is. Null when it is not usable.</summary>
    public string? ForGuest(string candidate)
    {
        var rewritten = Rewrite(candidate, PortShownToGuest, out var real);
        if (rewritten is not null)
        {
            _hostReal = new IPEndPoint(IPAddress.Loopback, real);
        }

        return rewritten;
    }

    /// <summary>Rewrites a guest candidate for the host, remembering where the guest really is.</summary>
    public string? ForHost(string candidate)
    {
        var rewritten = Rewrite(candidate, PortShownToHost, out var real);
        if (rewritten is not null)
        {
            _guestReal = new IPEndPoint(IPAddress.Loopback, real);
        }

        return rewritten;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _towardsHost.Dispose();
        _towardsGuest.Dispose();
    }

    private static string? Rewrite(string candidate, int newPort, out int oldPort)
    {
        oldPort = 0;
        var parts = candidate.Split(' ');
        var typ = Array.IndexOf(parts, "typ");
        if (typ < 2 || !parts[typ - 2].Equals("127.0.0.1", StringComparison.Ordinal) || !parts[typ + 1].Equals("host", StringComparison.Ordinal) || !parts[2].Equals("UDP", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        oldPort = int.Parse(parts[typ - 1], System.Globalization.CultureInfo.InvariantCulture);
        parts[typ - 1] = newPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return string.Join(' ', parts);
    }

    /// <summary>Receives on one socket and forwards out of the other, so the far side sees the port it was told about.</summary>
    private async Task Pump(UdpClient receiveOn, Func<IPEndPoint?> target, UdpClient sendFrom)
    {
        while (!_stop.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try
            {
                packet = await receiveOn.ReceiveAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                if (_stop.IsCancellationRequested || ex is ObjectDisposedException)
                {
                    return;
                }

                continue;
            }

            var destination = target();
            if (destination is null)
            {
                continue;
            }

            bool drop;
            int delay;
            lock (_rng)
            {
                drop = _rng.NextDouble() < _loss;
                delay = _baseDelayMs + _rng.Next(_jitterMs + 1);
            }

            if (drop)
            {
                Interlocked.Increment(ref _dropped);
                continue;
            }

            Interlocked.Increment(ref _forwarded);
            var data = packet.Buffer;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay, _stop.Token).ConfigureAwait(false);
                    await sendFrom.SendAsync(data, destination, _stop.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    // The relay is shutting down or the far side closed.
                }
            });
        }
    }
}

internal sealed class LockedPad : IVirtualPadBackend
{
    private readonly Lock _gate = new();
    private PadState _last;

    public PadState Last
    {
        get
        {
            lock (_gate)
            {
                return _last;
            }
        }
    }

    public void Submit(int slot, in PadState state)
    {
        lock (_gate)
        {
            _last = state;
        }
    }

    public void Neutralize(int slot)
    {
        lock (_gate)
        {
            _last = PadState.Neutral;
        }
    }
}

internal sealed class LockedInjector : IInputInjector
{
    private readonly Lock _gate = new();
    private readonly HashSet<ScanKey> _down = [];

    public int Downs { get; private set; }

    public int Ups { get; private set; }

    public int DownCount
    {
        get
        {
            lock (_gate)
            {
                return _down.Count;
            }
        }
    }

    public void KeyDown(ScanKey key)
    {
        lock (_gate)
        {
            _down.Add(key);
            Downs++;
        }
    }

    public void KeyUp(ScanKey key)
    {
        lock (_gate)
        {
            _down.Remove(key);
            Ups++;
        }
    }
}

internal sealed class BridgeRoom : IRoomLink
{
    // This bridge only carries signals to the guest; it never raises room events.
    public event Action<RoomEvent>? Received
    {
        add { }
        remove { }
    }

    public Action<JsonObject>? ToGuest { get; set; }

    public Task SendHolderAsync(string? guestId, uint epoch, bool everyone, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendSignalAsync(string guestId, JsonObject payload, CancellationToken cancellationToken)
    {
        ToGuest?.Invoke(payload);
        return Task.CompletedTask;
    }

    public Task KickAsync(string guestId, CancellationToken cancellationToken) => Task.CompletedTask;

}

public sealed class LossyLinkTests
{
    private static async Task Eventually(Func<bool> condition, string what, int seconds = 20)
    {
        for (var i = 0; i < seconds * 100; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Timed out waiting for: " + what);
    }

    /// <summary>
    /// A holder presses and releases buttons and keys for several seconds over a link that loses 10% of packets, twice the 5% the brief asks for, so a lost change shows, adds
    /// 15 to 45 ms of delay and reorders. It follows the web guest's policy: send on change, and resend the current
    /// pad state every 100 ms. The host must never be wrong about the pad for long, and no key may be lost or stuck.
    /// </summary>
    [Fact]
    [Trait("Category", "Network")]
    public async Task Five_percent_loss_and_jitter_never_leaves_a_button_stuck_or_a_key_lost()
    {
        using var relay = new ImpairedUdpRelay(loss: 0.10, baseDelayMs: 15, jitterMs: 30, seed: 7);

        // The host side: the real peer hub, the real controller and pipeline.
        var session = new StickSession("host");
        session.AddPlayer("g1", "Guest");
        var pad = new LockedPad();
        var injector = new LockedInjector();
        using var pipeline = new InputPipeline(session, injector, pad);
        var bridge = new BridgeRoom();
        using var hub = new PeerHub(bridge, [], NullLogger.Instance, "127.0.0.1");
        using var controller = new HostSessionController(session, pipeline, bridge, hub, NullLogger.Instance);
        var hostOpen = false;
        var trace = new System.Collections.Concurrent.ConcurrentQueue<string>();
        hub.StatusChanged += (_, status) =>
        {
            trace.Enqueue("host status " + status);
            hostOpen |= status == PeerStatus.Open;
        };

        // The guest side: a bare peer standing in for the browser.
        using var guest = new RtcPeerConnection(new RtcPeerConfiguration { IceServers = [], BindAddress = "127.0.0.1" });
        IRtcDataChannel? state = null;
        IRtcDataChannel? control = null;
        guest.OnDataChannel += (_, ch) =>
        {
            if (ch.Label == "state")
            {
                state = ch;
            }
            else
            {
                control = ch;
            }
        };
        guest.OnLocalDescriptionSafe += (_, d) => hub.HandleSignal("g1", new JsonObject { ["sdp"] = new JsonObject { ["type"] = d.Type.ToString().ToLowerInvariant(), ["sdp"] = d.Sdp } });
        guest.OnCandidateSafe += (_, c) =>
        {
            trace.Enqueue("guest candidate: " + c.Content);
            if (relay.ForHost(c.Content) is { } line)
            {
                hub.HandleSignal("g1", new JsonObject { ["candidate"] = new JsonObject { ["candidate"] = line, ["sdpMid"] = c.Mid } });
            }
        };
        bridge.ToGuest = payload =>
        {
            trace.Enqueue("to guest: " + payload.ToJsonString()[..Math.Min(160, payload.ToJsonString().Length)]);
            if (payload["sdp"] is JsonObject sdp && (string?)sdp["type"] == "offer")
            {
                guest.SetRemoteDescription(new RtcDescription { Sdp = (string)sdp["sdp"]!, Type = RtcDescriptionType.Offer });
            }
            else if (payload["candidate"] is JsonObject c && relay.ForGuest((string)c["candidate"]!) is { } line)
            {
                guest.AddRemoteCandidate(new RtcCandidate { Content = line, Mid = (string?)c["sdpMid"] ?? "0" });
            }
        };

        hub.EnsurePeer("g1");
        try
        {
            await Eventually(() => hostOpen && state is { IsOpen: true } && control is { IsOpen: true }, "a connection through the lossy relay", 12);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(ex.Message + Environment.NewLine + string.Join(Environment.NewLine, trace) + Environment.NewLine + $"relay forwarded {relay.Forwarded} dropped {relay.Dropped}");
        }

        Assert.True(session.PassTo("g1"));
        var epoch = session.Epoch;

        // Script: every 700 ms the guest presses or releases a pad button and a key. The intended state is the truth.
        PadState intended = PadState.Neutral;
        var seq = 0;
        var lastSent = Stopwatch.StartNew();
        var mismatchSince = (Stopwatch?)null;
        var longestMismatch = TimeSpan.Zero;
        var mismatchTotal = TimeSpan.Zero;
        var sampled = TimeSpan.Zero;
        var keyEvents = 0;
        var run = Stopwatch.StartNew();
        var nextChange = TimeSpan.FromMilliseconds(700);
        var pressed = false;
        var last = run.Elapsed;

        while (run.Elapsed < TimeSpan.FromSeconds(14))
        {
            var now = run.Elapsed;
            if (now >= nextChange)
            {
                pressed = !pressed;
                intended = pressed ? new PadState(PadButtons.A | PadButtons.DpadLeft, 12000, 0, 0, 0, 200, 0) : PadState.Neutral;
                state!.Send(StateFrameCodec.EncodePad(unchecked((ushort)++seq), intended));
                lastSent.Restart();
                control!.Send($"{{\"t\":\"key\",\"code\":\"{(pressed ? "KeyW" : "KeyW")}\",\"down\":{(pressed ? "true" : "false")},\"epoch\":{epoch}}}");
                keyEvents++;
                nextChange += TimeSpan.FromMilliseconds(700);
            }
            else if (lastSent.ElapsedMilliseconds >= 100)
            {
                state!.Send(StateFrameCodec.EncodePad(unchecked((ushort)++seq), intended)); // resend the current state
                lastSent.Restart();
            }

            var matches = pad.Last == intended;
            var dt = now - last;
            last = now;
            sampled += dt;
            if (!matches)
            {
                mismatchTotal += dt;
                mismatchSince ??= Stopwatch.StartNew();
                if (mismatchSince.Elapsed > longestMismatch)
                {
                    longestMismatch = mismatchSince.Elapsed;
                }
            }
            else
            {
                mismatchSince = null;
            }

            await Task.Delay(4);
        }

        // End on a release and give the link a moment: everything must settle to nothing held.
        if (pressed)
        {
            intended = PadState.Neutral;
            state!.Send(StateFrameCodec.EncodePad(unchecked((ushort)++seq), intended));
            control!.Send($"{{\"t\":\"key\",\"code\":\"KeyW\",\"down\":false,\"epoch\":{epoch}}}");
            keyEvents++;
        }

        for (var i = 0; i < 30; i++)
        {
            state!.Send(StateFrameCodec.EncodePad(unchecked((ushort)++seq), intended));
            await Task.Delay(100);
        }

        await Eventually(() => pad.Last == PadState.Neutral && injector.DownCount == 0, "everything released");

        Assert.InRange(relay.Dropped * 1.0 / (relay.Dropped + relay.Forwarded), 0.05, 0.20);
        Assert.True(relay.Dropped >= 5, $"the relay should have dropped packets (dropped {relay.Dropped}, forwarded {relay.Forwarded})");
        Assert.True(longestMismatch < TimeSpan.FromMilliseconds(500), $"the host was wrong about the pad for {longestMismatch.TotalMilliseconds:F0} ms in a row");
        Assert.True(mismatchTotal.TotalMilliseconds / sampled.TotalMilliseconds < 0.35, $"the host was wrong {mismatchTotal.TotalMilliseconds / sampled.TotalMilliseconds:P0} of the time");
        Assert.True(injector.Downs >= keyEvents / 2 - 1, $"key presses went missing: {injector.Downs} downs for {keyEvents / 2} presses");
        Assert.Equal(injector.Downs, injector.Ups); // reliable channel: every press was released, in order
    }
}
