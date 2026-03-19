using System;

namespace PassTheStick.Shared;

/// <summary>
/// Lightweight debug logging bus for tracing input injection across components.
/// </summary>
public static class InputDebugLog
{
    public enum LogLevel
    {
        Verbose = 0,
        Info = 1,
        Warning = 2,
        Error = 3
    }

    /// <summary>
    /// When false, Verbose logs are suppressed. Info/Warning/Error still emit.
    /// Typically tied to the host debug panel expander state.
    /// </summary>
    public static bool Enabled { get; set; }

    /// <summary>
    /// Minimum level to emit. Defaults to Info.
    /// </summary>
    public static LogLevel MinLevel { get; set; } = LogLevel.Info;

    public static event Action<string>? OnInputLog;

    private static readonly object Gate = new();
    private static LogLevel? _lastLevel;
    private static string? _lastMessage;
    private static int _repeatCount;

    public static void Log(string message)
    {
        Log(LogLevel.Info, message);
    }

    public static void Log(LogLevel level, string message)
    {
        if (level < MinLevel) return;
        if (level == LogLevel.Verbose && !Enabled) return;

        lock (Gate)
        {
            // Dedup identical consecutive messages.
            if (_lastMessage != null && _lastLevel != null && _lastLevel.Value == level && string.Equals(_lastMessage, message, StringComparison.Ordinal))
            {
                _repeatCount++;
                return;
            }

            FlushLocked();

            _lastLevel = level;
            _lastMessage = message;
            _repeatCount = 1;

            // Emit first occurrence immediately.
            OnInputLog?.Invoke(Format(level, message));
        }
    }

    public static void Flush()
    {
        lock (Gate)
        {
            FlushLocked();
        }
    }

    private static void FlushLocked()
    {
        if (_lastMessage == null || _lastLevel == null) return;
        if (_repeatCount >= 3)
            OnInputLog?.Invoke(Format(_lastLevel.Value, $"{_lastMessage} (×{_repeatCount})"));

        _lastMessage = null;
        _lastLevel = null;
        _repeatCount = 0;
    }

    private static string Format(LogLevel level, string message)
    {
        var prefix = level switch
        {
            LogLevel.Verbose => "[v]",
            LogLevel.Info => "[i]",
            LogLevel.Warning => "[w]",
            LogLevel.Error => "[e]",
            _ => "[i]"
        };
        return $"{prefix} {message}";
    }
}

