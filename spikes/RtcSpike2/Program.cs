using System.Diagnostics;
using DataChannelDotnet; using DataChannelDotnet.Data; using DataChannelDotnet.Impl; using DataChannelDotnet.Events;

var host = new RtcPeerConnection(new RtcPeerConfiguration { IceServers = [] });
var guest = new RtcPeerConnection(new RtcPeerConfiguration { IceServers = [] });
host.OnCandidateSafe += (_, c) => guest.AddRemoteCandidate(c);
guest.OnCandidateSafe += (_, c) => host.AddRemoteCandidate(c);
host.OnLocalDescriptionSafe += (_, d) => guest.SetRemoteDescription(d);
guest.OnLocalDescriptionSafe += (_, d) => host.SetRemoteDescription(d);

var gch = new TaskCompletionSource<IRtcDataChannel>();
guest.OnDataChannel += (_, ch) => { gch.TrySetResult(ch); };
var sw0 = Stopwatch.StartNew();
var state = host.CreateDataChannel(new RtcCreateDataChannelArgs { Label = "state", Unordered = true, Unreliable = true, MaxRetransmits = 0 });
var open = new TaskCompletionSource();
state.OnOpen += _ => open.TrySetResult();
await open.Task.WaitAsync(TimeSpan.FromSeconds(15));
var g = await gch.Task.WaitAsync(TimeSpan.FromSeconds(5));
Console.WriteLine($"connect+open: {sw0.ElapsedMilliseconds} ms");

g.OnBinaryReceivedSafe += (ch, e) => ch.Send(e.Data);
var rtts = new List<double>(); var pending = new Dictionary<int, long>(); int got = 0;
state.OnBinaryReceivedSafe += (ch, e) =>
{
    int s = BitConverter.ToInt32(e.Data);
    lock (pending) if (pending.Remove(s, out var t)) { rtts.Add((Stopwatch.GetTimestamp() - t) * 1000.0 / Stopwatch.Frequency); got++; }
};
var buf = new byte[16]; int total = 1200;
for (int i = 0; i < total; i++)
{
    BitConverter.TryWriteBytes(buf, i);
    lock (pending) pending[i] = Stopwatch.GetTimestamp();
    state.Send(buf);
    await Task.Delay(1000 / 60);
}
await Task.Delay(500);
rtts.Sort();
double P(double p) => rtts[(int)Math.Min(rtts.Count - 1, p * rtts.Count)];
Console.WriteLine($"sent {total} recv {got} loss {(1 - got / (double)total):P2}");
Console.WriteLine($"rtt ms: p50 {P(.5):F2} p95 {P(.95):F2} p99 {P(.99):F2} max {rtts[^1]:F2} mean {rtts.Average():F2}");

