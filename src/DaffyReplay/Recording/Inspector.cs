using System.Globalization;
using DaffyReplay.Interop;

namespace DaffyReplay.Recording;

/// <summary>
/// <c>daffyreplay inspect</c>: what is in a recording, how much of it can be replayed, and what
/// would make a replay of it mean less - before anything is sent anywhere.
/// </summary>
internal static class Inspector
{
    public static readonly IReadOnlyDictionary<string, string> SkipReasons = new Dictionary<string, string>
    {
        ["gateway_command"] = "daffytee.* commands, which DaffyTee answered itself",
        ["redacted"] = "a password was masked, so the request carries no raw bytes",
        ["truncated"] = "larger than DaffyTee's --max-event-kb",
        ["bulk_without_rows"] = "bulk loads recorded without their rows (DaffyTee --bulk meta)",
        ["insert_bulk_without_rows"] = "INSERT BULK statements whose rows are not replayed (they go together)",
        ["bulk_without_insert_bulk"] = "bulk loads whose INSERT BULK is not in the recording",
        ["rpc_without_raw"] = "procedure calls without raw bytes (record with --raw)",
        ["old_datetime"] = "procedure calls without raw bytes whose datetime values DaffyTee 0.3.0 or earlier decoded wrong",
        ["distributed_transaction"] = "distributed-transaction requests (DTC)",
        ["unknown_kind"] = "requests of a kind DaffyReplay does not know",
    };

    public static void Print(RecordingIndex index, TextWriter output, int top = 10)
    {
        var stats = index.Stats;
        var sessions = index.Sessions.Values.Where(s => s.Included && s.Seen > 0).ToList();

        output.WriteLine();
        string window = stats.FirstRequestUtc is { } first && stats.LastRequestUtc is { } last
            ? $"{first:yyyy-MM-dd HH:mm:ss} to {last:HH:mm:ss} UTC, {Duration(last - first)}"
            : "no requests";
        output.WriteLine($"  recording   {Plural(index.Files.Paths.Count, "file")}, {Size(index.Files.TotalBytes)}, {N(stats.Lines)} events ({window})");
        if (stats.ImportedFrom is not null)
            output.WriteLine($"  imported    from {stats.ImportedFrom}'s replay XML {stats.ImportedFile} (daffyreplay import): SQL text and outcomes only");
        else if (stats.DaffyTeeVersion is not null)
            output.WriteLine($"  daffytee    {stats.DaffyTeeVersion} at {stats.Source}: {stats.Settings}");
        foreach (string control in stats.Controls) output.WriteLine($"              changed {control}");

        output.WriteLine($"  sessions    {N(sessions.Count)}: {N(sessions.Count(s => !s.BeganBeforeRecording))} began during the recording" +
                         $"{(sessions.Count(s => s.BeganBeforeRecording) is > 0 and var before ? $", {N(before)} before it" : "")}" +
                         $"{(sessions.Count(s => s.Mars) is > 0 and var mars ? $"; {N(mars)} with MARS" : "")}");
        WriteBreakdown(output, "by app", sessions.GroupBy(s => s.App));
        WriteBreakdown(output, "by login", sessions.GroupBy(s => s.Login == s.TargetLogin || s.Login.Length == 0 ? s.TargetLogin : $"{s.Login} -> {s.TargetLogin}"));
        WriteBreakdown(output, "by database", sessions.GroupBy(s => s.Database ?? "(default)"));

        output.WriteLine($"  requests    {N(stats.Requests)}: {string.Join(", ", stats.Kinds.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {N(k.Value)}"))}");
        if (stats.Filtered > 0) output.WriteLine($"              filtered out {N(stats.Filtered)}");
        long considered = stats.Requests - stats.Filtered;
        output.WriteLine($"              replayable {N(stats.Replayable)} ({Percent(stats.Replayable, considered)}): " +
                         $"exactly {N(stats.Exact)}{(stats.Approximate > 0 ? $", approximately {N(stats.Approximate)} (procedure calls as T-SQL)" : "")}");
        foreach (var (reason, count) in stats.Skipped.OrderByDescending(s => s.Value))
            output.WriteLine($"              skipped {N(count)}: {SkipReasons.GetValueOrDefault(reason, reason)}");
        output.WriteLine($"              when recorded: {string.Join(", ", stats.RecordedStatus.OrderByDescending(s => s.Value).Select(s => $"{s.Key} {N(s.Value)}"))}");
        output.WriteLine($"              pooled-connection resets {N(stats.Resets)}; MARS requests {N(stats.MarsRequests)}; " +
                         $"prepared-statement calls {N(stats.PreparedCalls)}; cursor calls {N(stats.CursorCalls)}");

        output.WriteLine($"  statements  {N(stats.Fingerprints.Count)} distinct; the most frequent:");
        foreach (var f in stats.Fingerprints.Values.OrderByDescending(f => f.Count).ThenBy(f => f.Text, StringComparer.Ordinal).Take(top))
            output.WriteLine($"              {N(f.Count),8}  {f.TotalMs / Math.Max(1, f.Count),9:F2} ms avg  {Shorten(f.Text, 90)}");

        var warnings = Warnings(index).ToList();
        output.WriteLine();
        if (warnings.Count == 0)
        {
            output.WriteLine("  No warnings: the recording is complete and every request can be replayed exactly.");
        }
        else
        {
            output.WriteLine("  Warnings:");
            foreach (string warning in warnings) output.WriteLine($"    - {warning}");
        }
        output.WriteLine();
    }

    /// <summary>What would make a replay of this recording mean less. The report repeats these.</summary>
    public static IEnumerable<string> Warnings(RecordingIndex index)
    {
        var stats = index.Stats;
        var sessions = index.Sessions.Values.Where(s => s.Included && s.Seen > 0).ToList();

        if (stats.SchemaVersions.Any(v => v != 1))
            yield return $"event schema version {string.Join(", ", stats.SchemaVersions.Where(v => v != 1))}: DaffyReplay knows version 1 and reads what it recognises";
        if (stats.ImportedFrom is not null)
            yield return Sqlsim.ImportedWarning(stats.ImportedFile);
        if (stats.BadLines > 0)
            yield return $"{N(stats.BadLines)} line(s) are not DaffyTee events and were ignored";
        if (stats.ImportedFrom is null)
        {
            if (stats.DaffyTeeVersion is null)
                yield return "no proxy.start event: the recording does not say how DaffyTee was set up (it may be a slice, which is fine)";
            else if (!stats.RawOn)
                yield return "DaffyTee was not recording raw bytes (--raw): procedure calls cannot be replayed exactly";
            if (stats.Settings is { } settings && settings != "off" && !settings.Contains("sampling 100%", StringComparison.Ordinal))
                yield return $"DaffyTee was sampling ({settings}): only part of the workload was recorded";
        }
        if (stats.Dropped > 0)
            yield return $"DaffyTee dropped {N(stats.Dropped)} event(s) ({N(stats.GapEvents)} proxy.gap): those requests are holes in the replay";

        long missing = sessions.Sum(s => s.MissingSeq);
        if (missing > 0)
            yield return $"{N(missing)} request(s) are missing inside sessions (gaps in seq): sampled out, or the tee was off";

        var before = sessions.Where(s => s.BeganBeforeRecording).ToList();
        if (before.Count > 0)
            yield return $"{N(before.Count)} session(s) began before the recording (e.g. {string.Join(", ", before.Take(3).Select(s => s.Describe()))}): " +
                         "their earlier SET options, temp tables and prepared statements are not in it. Start DaffyTee before the application connects";

        if (stats.Skipped.GetValueOrDefault("rpc_without_raw") is > 0 and var noRaw)
            yield return $"{N(noRaw)} procedure call(s) carry no raw bytes and will be skipped: --approximate replays them as T-SQL " +
                         "(marked approximate in the report), or record with --raw";
        if (stats.OldDatetimeCalls > 0)
            yield return $"{N(stats.OldDatetimeCalls)} procedure call(s) without raw bytes hold datetime values that DaffyTee 0.3.0 or earlier " +
                         "decoded wrong (a time of day after 00:02:23 came out between 00:00:00 and 00:02:23): " +
                         $"{(index.AllowApproximate ? "they are skipped" : "--approximate skips them")}. Record with --raw, or with DaffyTee 0.3.1 or later";
        if (stats.Approximate > 0)
            yield return $"{N(stats.Approximate)} procedure call(s) will be replayed approximately, as T-SQL with literal values " +
                         "(table-valued and OUTPUT parameters as variables): compared on results and errors only";
        if (stats.Skipped.GetValueOrDefault("redacted") is > 0 and var redacted)
            yield return $"{N(redacted)} request(s) had a password masked by DaffyTee and carry no raw bytes: they are skipped";
        if (stats.Skipped.GetValueOrDefault("distributed_transaction") is > 0 and var dtc)
            yield return $"{N(dtc)} distributed-transaction request(s) (DTC) cannot be replayed and are skipped";

        if (stats.Skipped.GetValueOrDefault("bulk_without_rows") is > 0 and var noRows)
            yield return $"{N(noRows)} bulk load(s) were recorded without their rows (DaffyTee --bulk meta): they are skipped, with the " +
                         "INSERT BULK statements that announce them. To replay them, record with --bulk full and a larger --max-event-kb";
        if (stats.BulkNotRecorded > 0)
            yield return $"{N(stats.BulkNotRecorded)} bulk load(s) left no event (DaffyTee --bulk off): they are skipped, with the " +
                         "INSERT BULK statements that announce them. To replay them, record with --bulk full";
        if (stats.Skipped.GetValueOrDefault("truncated") > 0)
            yield return "some requests were larger than --max-event-kb and cannot be replayed: record with a larger --max-event-kb";
    }

    private static void WriteBreakdown(TextWriter output, string label, IEnumerable<IGrouping<string, RecordedSession>> groups)
    {
        var parts = groups.OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{(g.Key.Length == 0 ? "(none)" : g.Key)} {N(g.Count())}").ToList();
        if (parts.Count == 0) return;
        string shown = string.Join(", ", parts.Take(8)) + (parts.Count > 8 ? $", and {parts.Count - 8} more" : "");
        output.WriteLine($"              {label}: {shown}");
    }

    public static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public static string Plural(long count, string noun) => $"{N(count)} {noun}{(count == 1 ? "" : "s")}";

    public static string Percent(long part, long whole) => whole == 0 ? "-" : (part / (double)whole).ToString("P1", CultureInfo.InvariantCulture);

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:F2} GB",
    };

    public static string Duration(TimeSpan span) =>
        span.TotalSeconds < 60 ? $"{span.TotalSeconds:F1} s"
        : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min {span.Seconds} s"
        : $"{(int)span.TotalHours} h {span.Minutes} min";

    public static string Shorten(string text, int length) => text.Length <= length ? text : text[..(length - 3)] + "...";
}
