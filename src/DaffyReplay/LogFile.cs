using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DaffyReplay.IO;

namespace DaffyReplay;

/// <summary>
/// replay.log: the operational log as JSON lines, one object per line -
/// {"ts":"...Z","level":"warn","event":"request.skipped","session":3,"msg":"..."} - written exactly as
/// DaffyTee writes its --log-file, so the same tooling reads both. The console keeps its
/// human-readable form; this is the same entries, for machines.
/// </summary>
internal static class LogFile
{
    private static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static RollingFileWriter Attach(string path)
    {
        var file = new RollingFileWriter(path);
        Log.Sink = entry => file.Write(Format(entry));
        return file;
    }

    public static string Format(LogEntry entry)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer, Options))
        {
            w.WriteStartObject();
            w.WriteString("ts", entry.TimestampUtc);
            w.WriteString("level", entry.Level.ToString().ToLowerInvariant());
            w.WriteString("event", entry.Code);
            if (entry.Session > 0) w.WriteNumber("session", entry.Session);
            w.WriteString("msg", entry.Message);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n";
    }
}
