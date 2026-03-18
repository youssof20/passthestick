namespace PassTheStick.Shared;

public static class Constants
{
    public const string DefaultCloudRelayWs = "wss://passthestick.onrender.com";
    public const string DefaultLocalRelayWs = "ws://localhost:8080";

    /// <summary>
    /// Relay WebSocket URL.
    /// Priority:
    /// 1) Settings override (in-app)
    /// 2) PTS_RELAY_URL environment variable
    /// 3) Cloud relay default (Render)
    ///
    /// Local relay (ws://localhost:8080) is only used when the user explicitly starts it.
    /// </summary>
    public static string RelayWebSocketUrl
    {
        get
        {
            var settings = SettingsStore.Load();
            var url =
                (settings.RelayUrlOverride ?? string.Empty).Trim();

            if (string.IsNullOrEmpty(url))
                url = Environment.GetEnvironmentVariable("PTS_RELAY_URL")?.Trim() ?? DefaultCloudRelayWs;

            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return "wss://" + url.Substring(8);
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                return "ws://" + url.Substring(7);
            if (url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
                return url;
            // Assume secure by default for bare hosts.
            return "wss://" + url;
        }
    }

    public const string Version = "0.1.0";
    public const int MaxPlayersPerRoom = 8;
}
