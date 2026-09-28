using System;

namespace BALLxPITOnlineCoop.Core;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>
/// Logging for the networking code. It runs on background threads, so the plugin points the sink
/// at a queue that is flushed to BepInEx on the main thread.
/// </summary>
public static class Log
{
    public static Action<LogLevel, string> Sink = (level, message) => Console.WriteLine($"[{level}] {message}");

    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warning, message);
    public static void Error(string message) => Write(LogLevel.Error, message);

    private static void Write(LogLevel level, string message)
    {
        try
        {
            Sink?.Invoke(level, message);
        }
        catch
        {
            // Logging must never take the stream down.
        }
    }
}
