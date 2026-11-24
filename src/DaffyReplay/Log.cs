namespace DaffyReplay;

internal enum LogLevel { Debug, Info, Warn, Error }

/// <summary>One operational log line. <see cref="Code"/> is stable, so scripts can key on it.</summary>
internal readonly record struct LogEntry(DateTime TimestampUtc, LogLevel Level, string Code, int Session, string Message);

/// <summary>
/// DaffyReplay's own log - connections, skipped requests, the scheduler's health - as opposed to the
/// per-request results, which are a separate file with a separate audience.
///
/// Every line carries a stable event code (session.open, request.skipped, ...) alongside the text a
/// person reads, so the same call feeds the console and replay.log, NDJSON in DaffyTee's style.
/// The console gets Info and above (Debug too with --verbose); the file gets everything. While a run
/// shows its progress line, <see cref="ConsoleWriter"/> is that display, which clears its line first.
///
/// Passwords and connection strings never go through here.
/// </summary>
internal static class Log
{
    /// <summary>--verbose: per-session chatter (Debug) on the console too.</summary>
    public static bool Verbose;

    /// <summary>Also receives every entry, for replay.log.</summary>
    public static Action<LogEntry>? Sink;

    /// <summary>Where console lines go; the progress display swaps itself in while a run is going.</summary>
    public static Action<string> ConsoleWriter = Console.WriteLine;

    public static void Debug(string code, string message, int session = 0) => Write(LogLevel.Debug, code, message, session);
    public static void Info(string code, string message, int session = 0) => Write(LogLevel.Info, code, message, session);
    public static void Warn(string code, string message, int session = 0) => Write(LogLevel.Warn, code, message, session);
    public static void Error(string code, string message, int session = 0) => Write(LogLevel.Error, code, message, session);

    private static void Write(LogLevel level, string code, string message, int session)
    {
        var entry = new LogEntry(DateTime.UtcNow, level, code, session, message);

        if (Verbose || level != LogLevel.Debug)
        {
            string marker = level switch { LogLevel.Warn => "warning: ", LogLevel.Error => "error: ", _ => "" };
            string prefix = session > 0 ? $"[s{session}] " : "";
            try { ConsoleWriter($"{entry.TimestampUtc.ToLocalTime():HH:mm:ss} {prefix}{marker}{message}"); }
            catch { /* the console must never be the thing that breaks a run */ }
        }

        try { Sink?.Invoke(entry); }
        catch { /* nor the log file */ }
    }
}
