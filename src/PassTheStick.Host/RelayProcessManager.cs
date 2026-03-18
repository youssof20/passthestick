using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;

namespace PassTheStick.Host;

public sealed class RelayProcessManager : IDisposable
{
    private Process? _proc;

    public bool IsRunning => _proc != null && !_proc.HasExited;

    public async Task<int> StartRelayWithPortFallbackAsync(
        string appDir,
        int startPort,
        int endPort,
        Action<string>? log,
        CancellationToken ct)
    {
        var relayDir = Path.Combine(appDir, "relay");
        var serverJs = Path.Combine(relayDir, "server.js");
        var bundledNode = Path.Combine(relayDir, "node.exe");

        log?.Invoke($"Relay folder: {relayDir}");
        log?.Invoke($"server.js exists: {File.Exists(serverJs)}");
        log?.Invoke($"bundled node.exe exists: {File.Exists(bundledNode)}");

        if (!File.Exists(serverJs))
            throw new FileNotFoundException("Bundled relay server.js not found.");

        var nodeExe = File.Exists(bundledNode) ? bundledNode : FindInPath("node.exe");
        if (string.IsNullOrWhiteSpace(nodeExe) || !File.Exists(nodeExe))
            throw new FileNotFoundException("Node.js was not found. Install Node.js or reinstall PassTheStick with the bundled relay.");

        // If a relay is already listening, don't start a new one.
        for (int port = startPort; port <= endPort; port++)
        {
            if (IsTcpListening(port))
            {
                log?.Invoke($"Port {port} already in use — assuming relay is running, will connect directly.");
                return port;
            }
        }

        Stop();

        for (int port = startPort; port <= endPort; port++)
        {
            if (!IsPortFree(port)) continue;
            var psi = new ProcessStartInfo
            {
                FileName = nodeExe,
                Arguments = $"\"{serverJs}\"",
                WorkingDirectory = relayDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.Environment["PORT"] = port.ToString();
            log?.Invoke($"Starting relay: \"{psi.FileName}\" {psi.Arguments} (PORT={port})");
            _proc = Process.Start(psi);
            if (_proc == null) throw new InvalidOperationException("Failed to start relay process.");

            _proc.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log?.Invoke(e.Data); };
            _proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log?.Invoke("ERR: " + e.Data); };
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();

            var ready = await WaitForTcpListenAsync(port, TimeSpan.FromSeconds(10), log, ct);
            if (!ready)
                throw new InvalidOperationException("Relay did not start listening in time.");

            return port;
        }

        throw new InvalidOperationException("No free port found for relay (8080-8082).");
    }

    public int StartRelayWithPortFallback(string appDir, int startPort, int endPort)
    {
        // Legacy sync entrypoint used in a few places.
        return StartRelayWithPortFallbackAsync(appDir, startPort, endPort, null, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.Loopback, port);
            l.Start();
            l.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTcpListening(int port) =>
        IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Any(ep => ep.Port == port);

    private static async Task<bool> WaitForTcpListenAsync(int port, TimeSpan timeout, Action<string>? log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        int tick = 0;
        while (sw.Elapsed < timeout && !ct.IsCancellationRequested)
        {
            tick++;
            if (IsTcpListening(port))
            {
                log?.Invoke($"Relay is listening on port {port}.");
                return true;
            }
            if (tick % 2 == 0)
                log?.Invoke($"Waiting for relay… ({tick}/20)");
            await Task.Delay(500, ct);
        }
        return false;
    }

    private static string? FindInPath(string exe)
    {
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? Array.Empty<string>();
        foreach (var dir in paths)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                var full = Path.Combine(dir.Trim(), exe);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }

    public void Stop()
    {
        try
        {
            if (_proc != null && !_proc.HasExited)
                _proc.Kill(entireProcessTree: true);
        }
        catch { }
        finally
        {
            _proc?.Dispose();
            _proc = null;
        }
    }

    public void Dispose() => Stop();
}

