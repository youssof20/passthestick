using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace PassTheStick.Host;

public sealed class RelayProcessManager : IDisposable
{
    private Process? _proc;

    public bool IsRunning => _proc != null && !_proc.HasExited;

    public int StartRelayWithPortFallback(string appDir, int startPort, int endPort)
    {
        Stop();

        var relayDir = Path.Combine(appDir, "relay");
        var nodeExe = Path.Combine(relayDir, "node.exe");
        var serverJs = Path.Combine(relayDir, "server.js");
        if (!File.Exists(nodeExe) || !File.Exists(serverJs))
            throw new FileNotFoundException("Bundled relay runtime not found.");

        for (int port = startPort; port <= endPort; port++)
        {
            if (!IsPortFree(port)) continue;
            var psi = new ProcessStartInfo
            {
                FileName = nodeExe,
                Arguments = $"\"{serverJs}\"",
                WorkingDirectory = relayDir,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.Environment["PORT"] = port.ToString();
            _proc = Process.Start(psi);
            if (_proc == null) throw new InvalidOperationException("Failed to start relay process.");
            return port;
        }

        throw new InvalidOperationException("No free port found for relay (8080-8082).");
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

