using System;

namespace PassTheStick.Shared;

/// <summary>
/// Lightweight debug logging bus for tracing input injection across components.
/// </summary>
public static class InputDebugLog
{
    public static bool Enabled { get; set; }

    public static event Action<string>? OnInputLog;

    public static void Log(string message)
    {
        if (!Enabled) return;
        OnInputLog?.Invoke(message);
    }
}

