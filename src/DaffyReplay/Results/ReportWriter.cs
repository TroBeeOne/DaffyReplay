using DaffyReplay.Cli;
using DaffyReplay.Recording;
using DaffyReplay.Replay;
using DaffyReplay.Security;
using DaffyReplay.Target;

namespace DaffyReplay.Results;

/// <summary>Puts a finished run's report together: what ran, what was covered, what changed, and what to make of it.</summary>
internal static class ReportWriter
{
    public static Report Build(ReplayContext context, RecordingIndex index, TargetRecord target, TargetEndpoint endpoint,
                               string? serverVersion, string? security, ReplayOptions options, Arguments parsed,
                               DateTime started, DateTime ended, bool interrupted)
    {
        var stats = index.Stats;
        var sessions = index.Sessions.Values.Where(s => s.Replayable > 0).ToList();
        var (outcomes, timing, pace) = context.Report.Build();

        var recording = new RecordingInfo(
            index.Files.Paths, stats.FirstRequestUtc, stats.LastRequestUtc,
            stats.FirstRequestUtc is { } first && stats.LastRequestUtc is { } last ? Math.Round((last - first).TotalSeconds, 3) : 0,
            stats.DaffyTeeVersion, stats.Source, stats.Settings,
            sessions.SelectMany(s => new[] { s.Target, s.RoutedTo }).OfType<string>().Where(t => t.Length > 0).Distinct().Order().ToList(),
            sessions.Select(s => s.ServerVersion).OfType<string>().Distinct().Order().ToList(),
            stats.ImportedFrom, stats.ImportedFile);

        var filters = Arguments.FilterOptions.Where(parsed.Has).ToDictionary(f => f.TrimStart('-'), f => parsed.Get(f) ?? "");
        var runOptions = new Dictionary<string, string>
        {
            ["maxSessions"] = options.MaxSessions?.ToString() ?? "none",
            ["requestTimeoutSeconds"] = options.RequestTimeout?.TotalSeconds.ToString("0.###") ?? "none",
            ["keepAppName"] = options.KeepAppName.ToString().ToLowerInvariant(),
            ["keepHostName"] = options.KeepHostName.ToString().ToLowerInvariant(),
            ["packetSize"] = options.PacketSize.ToString(),
            ["recordedResponseKb"] = options.RecordedResponseKb.ToString(),
            ["replayCancels"] = options.ReplayCancels.ToString().ToLowerInvariant(),
        };

        var run = new RunInfo($"daffyreplay {Program.ToolVersion()}", started, ended, Math.Round((ended - started).TotalSeconds, 3),
            options.Pace.ToString().ToLowerInvariant(), options.Speed,
            new TargetInfo(target.Name, $"{endpoint.Host}:{endpoint.Port}", serverVersion, security),
            recording, filters, runOptions, interrupted);

        var coverage = new CoverageInfo(stats.Lines, stats.Requests, context.Report.Replayed, stats.Exact, stats.Approximate, stats.Filtered,
            new Dictionary<string, long>(stats.Skipped), context.Report.NotReplayed,
            sessions.Count, sessions.Count(s => s.BeganBeforeRecording), context.LoginFailures, context.BrokenSessions, sessions.Count(s => s.Mars));

        var report = new Report(Report.SchemaVersion, run, coverage, outcomes, timing, pace with { Overlapped = context.OverlappedRequests },
            new HandleInfo(context.HandlesRewritten, context.HandlesUnknown), []);
        return report with { Warnings = Warnings(report, index, options).ToList() };
    }

    private static IEnumerable<string> Warnings(Report report, RecordingIndex index, ReplayOptions options)
    {
        foreach (string warning in Inspector.Warnings(index)) yield return warning;

        if (report.Run.Interrupted) yield return "the run was stopped before the end: the report covers what ran";

        var dataState = report.Outcomes.NewErrors.Where(e => e.LikelyDataState).Sum(e => e.Count);
        if (dataState > 0)
            yield return $"{Inspector.N(dataState)} new error(s) are duplicate-key or foreign-key errors, typical of data that differs " +
                         "from the recording's: was the database restored to the moment recording began?";

        if (report.Outcomes.RowsDiffer > 0)
            yield return $"{Inspector.N(report.Outcomes.RowsDiffer)} request(s) returned or changed a different number of rows. " +
                         "After a restore this is usually server-generated values (identities, NEWID, GETDATE) or concurrency " +
                         "ordering differently from the recording" + (options.Pace == Pace.Serial ? "" : "; --pace serial reproduces them most closely") +
                         (index.Stats.ImportedFrom is null ? "" : ". An imported batch is held against its total as Extended Events counted it, " +
                                                                  "which now and then counts a row or two more than the batch returned");

        if (report.Coverage.SessionsLoginFailed > 0)
            yield return $"{report.Coverage.SessionsLoginFailed} session(s) could not log in: map their logins with 'daffyreplay target logins'";
        if (report.Coverage.SessionsBroken > 0)
            yield return $"{report.Coverage.SessionsBroken} session(s) lost their connection part-way";

        if (report.Pace.Overlapped > 0)
            yield return $"{Inspector.N(report.Pace.Overlapped)} request(s) were still running after {options.SerialWait.TotalSeconds:0.#} s - " +
                         "usually blocked by a lock another session releases later in the sequence - so the next one was started " +
                         "while they waited, as in the recording";

        if (options.Pace == Pace.Serial && report.Outcomes.Counts.GetValueOrDefault(Outcome.Timeout) is > 0 and var blocked)
            yield return $"{Inspector.N(blocked)} request(s) timed out in serial pace: usually blocked by a lock another session " +
                         "takes and releases only later in the sequence";

        if (report.Outcomes.DatabaseDiffers > 0)
            yield return $"{Inspector.N(report.Outcomes.DatabaseDiffers)} request(s) ended in a different database from the recording's";

        if (report.Handles.Unknown > 0)
            yield return $"{Inspector.N(report.Handles.Unknown)} prepared-statement or cursor handle(s) were used without being seen made " +
                         "(their responses were too large for DaffyTee to decode); they were sent as recorded";

        if (options.Pace == Pace.Recorded && report.Pace.Lag.Count > 0 && report.Pace.LagHost.P95 > 5)
            yield return $"the replay host fell behind the recorded schedule (host lag p95 {report.Pace.LagHost.P95:F1} ms): " +
                         "the results understate what the server could do";

        if (report.Outcomes.Counts.GetValueOrDefault(Outcome.CancelledWhenRecorded) is > 0 and var cancelled)
            yield return options.ReplayCancels
                ? $"{Inspector.N(cancelled)} request(s) were cancelled when recorded; they were cancelled again after their recorded duration"
                : $"{Inspector.N(cancelled)} request(s) were cancelled when recorded (a command timeout or Cancel); they were replayed to " +
                  "completion (--replay-cancels cancels them again)";

        if (!options.KeepAppName && index.Sessions.Values.Any(s => s.App.Length > 0))
            yield return "sessions logged in as application 'DaffyReplay': where APP_NAME() matters (Resource Governor, application logic), use --keep-app-name";

        if (index.Stats.ImportedFrom is null)
            yield return "DaffyTee 0.3 does not record the application's own login options; replayed sessions use SqlClient's " +
                     $"(packet size {options.PacketSize}, us_english unless the login's default differs)";
    }
}

/// <summary>The end of a run, on the console: the few lines that say whether anything changed.</summary>
internal static class ConsoleSummary
{
    public static void Print(Report report, TextWriter output, string folder)
    {
        var c = report.Outcomes.Counts;
        long Count(string outcome) => c.GetValueOrDefault(outcome);

        output.WriteLine();
        output.WriteLine($"  replayed    {Inspector.N(report.Coverage.Replayed)} of {Inspector.N(report.Coverage.Requests)} requests in " +
                         $"{Inspector.Duration(TimeSpan.FromSeconds(report.Run.WallSeconds))} (recorded over {Inspector.Duration(TimeSpan.FromSeconds(report.Run.Recording.Seconds))}), " +
                         $"{Inspector.Plural(report.Coverage.Sessions, "session")}, pace {report.Run.Pace}");
        output.WriteLine($"  outcomes    same {Inspector.N(Count(Outcome.Same))}; new errors {Inspector.N(Count(Outcome.NewError))}; " +
                         $"fixed {Inspector.N(Count(Outcome.FixedError))}; changed {Inspector.N(Count(Outcome.ChangedError))}; " +
                         $"timeouts {Inspector.N(Count(Outcome.Timeout))}; failed {Inspector.N(Count(Outcome.ConnectionFailed))}" +
                         (Count(Outcome.NotReplayed) > 0 ? $"; not replayed {Inspector.N(Count(Outcome.NotReplayed))}" : ""));
        output.WriteLine($"              rows differ {Inspector.N(report.Outcomes.RowsDiffer)}" +
                         (Count(Outcome.CancelledWhenRecorded) > 0 ? $"; cancelled when recorded {Inspector.N(Count(Outcome.CancelledWhenRecorded))}" : "") +
                         (report.Coverage.Skipped.Values.Sum() is > 0 and var skipped ? $"; skipped {Inspector.N(skipped)} (inspect says why)" : ""));

        foreach (var error in report.Outcomes.NewErrors.Take(5))
            output.WriteLine($"  new error   {Inspector.N(error.Count),6} x Msg {error.Number}: {Inspector.Shorten(error.Message, 70)}" +
                             $"{(error.LikelyDataState ? " (data state?)" : "")}  in {Inspector.Shorten(error.Statement, 50)}");

        var t = report.Timing;
        if (t.Replay.Count > 0)
            output.WriteLine($"  timing      recorded p50 {t.Recorded.P50:F2} ms, p95 {t.Recorded.P95:F2} ms, total {t.RecordedTotalMs / 1000:F1} s  |  " +
                             $"replay p50 {t.Replay.P50:F2} ms, p95 {t.Replay.P95:F2} ms, total {t.ReplayTotalMs / 1000:F1} s");
        foreach (var statement in t.RegressionsByTotal.Where(s => s.DeltaMs >= 10 && s.DeltaMs >= s.RecordedTotalMs * 0.1).Take(3))
            output.WriteLine($"  slower      +{statement.DeltaMs / 1000:F2} s over {Inspector.N(statement.Count)}: {Inspector.Shorten(statement.Statement, 80)}");

        var p = report.Pace;
        if (p.Lag.Count > 0)
            output.WriteLine($"  lag         p95 {p.Lag.P95:F1} ms (waiting on the server {p.LagServer.P95:F1}, replay host behind {p.LagHost.P95:F1}), " +
                             $"max {p.Lag.Max:F1} ms; scheduler error p99 {p.SchedulerError.P99:F2} ms");
        if (report.Handles.Rewritten > 0) output.WriteLine($"  handles     {Inspector.N(report.Handles.Rewritten)} prepared-statement or cursor handle(s) mapped");

        foreach (string warning in report.Warnings.Where(w => !w.StartsWith("DaffyTee 0.3", StringComparison.Ordinal)
                                                              && !w.StartsWith("sessions logged in as", StringComparison.Ordinal)))
            output.WriteLine($"  warning     {warning}");
        output.WriteLine($"  report      {Path.Combine(folder, "report.html")}");
        output.WriteLine();
    }
}
