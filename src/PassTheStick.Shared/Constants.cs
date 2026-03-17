namespace PassTheStick.Shared;

public static class Constants
{
    /// <summary>
    /// Relay WebSocket URL. Set PTS_RELAY_URL environment variable for production (e.g. wss://xxx.fly.dev);
    /// defaults to ws://localhost:8080 for local dev.
    /// </summary>
    public static string RelayWebSocketUrl
    {
        get
        {
            var url = Environment.GetEnvironmentVariable("PTS_RELAY_URL")?.Trim() ?? "http://localhost:8080";
            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return "wss://" + url.Substring(8);
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                return "ws://" + url.Substring(7);
            if (url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
                return url;
            return "ws://" + url;
        }
    }

    public const string Version = "0.1.0";
    public const int MaxPlayersPerRoom = 8;
}
