using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace DaffyReplay.Results;

/// <summary>
/// report.html: the report as one self-contained page - no scripts, fonts or styles from anywhere
/// else, so it opens offline and travels as an attachment. Tables are plain HTML and read without
/// script; the two charts (throughput over time, and each statement's p95 recorded against
/// replayed) are drawn from the report's own JSON, embedded in the page, with a hover readout.
/// Light and dark follow the viewer's setting.
/// </summary>
internal static class HtmlReport
{
    public static void Write(Report report, string path) => File.WriteAllText(path, Render(report), new UTF8Encoding(false));

    public static string Render(Report report)
    {
        var sb = new StringBuilder(64 * 1024);
        var run = report.Run;
        var c = report.Outcomes.Counts;
        long Count(string outcome) => c.GetValueOrDefault(outcome);

        string files = string.Join(", ", run.Recording.Files.Select(Path.GetFileName));
        sb.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n")
          .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
          .Append("<title>Replay report</title>\n<style>\n").Append(Css).Append("</style>\n</head>\n<body>\n<main>\n");

        // ---- header and headline numbers ---------------------------------------------------------
        sb.Append("<header>\n<p class=\"eyebrow\">DaffyReplay report</p>\n")
          .Append($"<h1>{E(files)} replayed against {E(run.Target.Name)}</h1>\n")
          .Append($"<p class=\"sub\">{E(run.Pace)} pace{(run.Pace == "recorded" && run.Speed != 1 ? $", {run.Speed:0.###}x" : "")} · " +
                  $"{E(run.Target.Server)}{(run.Target.ServerVersion is { } v ? $" ({E(v)})" : "")} · " +
                  $"{run.Started.ToLocalTime():yyyy-MM-dd HH:mm} · {E(run.Tool)}" +
                  (run.Interrupted ? " · <b>stopped before the end</b>" : "") + "</p>\n</header>\n");

        long newErrors = Count(Outcome.NewError) + Count(Outcome.ChangedError);
        long failures = Count(Outcome.ConnectionFailed) + Count(Outcome.Timeout) + Count(Outcome.NotReplayed);
        sb.Append("<section class=\"tiles\" aria-label=\"Headline numbers\">\n");
        Tile(sb, "Requests replayed", N(report.Coverage.Replayed), $"of {N(report.Coverage.Requests)} recorded");
        Tile(sb, "New or changed errors", N(newErrors), newErrors == 0 ? "none" : "failed on replay, not when recorded",
             newErrors > 0 ? "critical" : "good");
        Tile(sb, "Row counts differ", N(report.Outcomes.RowsDiffer), "requests", report.Outcomes.RowsDiffer > 0 ? "warning" : "good");
        Tile(sb, "Failed, timed out, not replayed", N(failures), "requests", failures > 0 ? "serious" : "good");
        if (report.Timing.Replay.Count > 0)
            Tile(sb, "p95, recorded → replay", $"{Ms(report.Timing.Recorded.P95)} → {Ms(report.Timing.Replay.P95)}", "per request");
        Tile(sb, "Wall time", Seconds(run.WallSeconds), $"recorded over {Seconds(run.Recording.Seconds)}");
        sb.Append("</section>\n");

        // An imported recording knows less than DaffyTee's: said once, plainly, before any number is read.
        bool imported = run.Recording.ImportedFrom is not null;
        if (imported)
        {
            sb.Append("<section class=\"callout imported\" aria-label=\"Imported recording\">\n")
              .Append($"<h2>Imported from {E(run.Recording.ImportedFrom!)}'s replay XML{(run.Recording.ImportedFile is { } file ? $" ({E(file)})" : "")}: " +
                      "an Extended Events capture, not a DaffyTee recording</h2>\n<ul>\n");
            foreach (var (lead, detail) in Interop.Sqlsim.ImportLimits) sb.Append($"<li><b>{E(lead)}</b>: {E(detail)}.</li>\n");
            sb.Append("</ul>\n</section>\n");
        }

        var warnings = report.Warnings.Where(w => !(imported && w.StartsWith(Interop.Sqlsim.ImportedWarningStart, StringComparison.Ordinal))).ToList();
        if (warnings.Count > 0)
        {
            sb.Append("<section class=\"callout\" aria-label=\"Warnings\">\n<h2>Read these first</h2>\n<ul>\n");
            foreach (string warning in warnings) sb.Append($"<li>{E(warning)}</li>\n");
            sb.Append("</ul>\n</section>\n");
        }

        // ---- outcomes ------------------------------------------------------------------------------
        sb.Append("<section>\n<h2>Outcomes</h2>\n<p class=\"lede\">Each replayed request against its recording.</p>\n");
        Table(sb, ["Outcome", "Requests"], OutcomeRows(report).Select(r => new[] { r.Label, N(r.Count) }).ToList(), numeric: [1]);
        ErrorTable(sb, "New errors", "ok when recorded, failed on replay", report.Outcomes.NewErrors, "critical");
        ErrorTable(sb, "Changed errors", "failed both times, with different errors", report.Outcomes.ChangedErrors, "serious");
        ErrorTable(sb, "Fixed errors", "failed when recorded, ok on replay", report.Outcomes.FixedErrors, "good");
        ErrorTable(sb, "Unchanged errors", "the same errors both times", report.Outcomes.UnchangedErrors, null);
        StatementTable(sb, "Row counts differ", "per statement: rows returned or changed, per statement and result set", report.Outcomes.RowMismatches);
        StatementTable(sb, "Timed out", "cancelled after --request-timeout", report.Outcomes.Timeouts);
        sb.Append("</section>\n");

        // ---- timing ----------------------------------------------------------------------------------
        var statements = report.Timing.Statements;
        sb.Append("<section>\n<h2>Timing</h2>\n")
          .Append("<p class=\"lede\">Requests that completed both times. Recorded times are production's, on its hardware and under its " +
                  "concurrency; the most telling comparison is one replay against another on the same server.</p>\n");
        Table(sb, ["", "Requests", "p50", "p95", "p99", "Max", "Total"],
        [
            ["Recorded", N(report.Timing.Recorded.Count), Ms(report.Timing.Recorded.P50), Ms(report.Timing.Recorded.P95), Ms(report.Timing.Recorded.P99), Ms(report.Timing.Recorded.Max), Seconds(report.Timing.RecordedTotalMs / 1000)],
            ["Replay", N(report.Timing.Replay.Count), Ms(report.Timing.Replay.P50), Ms(report.Timing.Replay.P95), Ms(report.Timing.Replay.P99), Ms(report.Timing.Replay.Max), Seconds(report.Timing.ReplayTotalMs / 1000)],
        ], numeric: [1, 2, 3, 4, 5, 6]);

        if (statements.Count > 0)
        {
            sb.Append("<figure class=\"chart\">\n<figcaption><span class=\"title\">p95 by statement, recorded → replay</span>")
              .Append("<span class=\"legend\"><span class=\"key dot recorded\"></span>Recorded <span class=\"key dot replay\"></span>Replay</span>")
              .Append("<span class=\"note\">The 15 statements with the most replay time; log scale. Every statement is in the table below.</span></figcaption>\n")
              .Append("<div id=\"dumbbell\" class=\"plot\" role=\"img\" aria-label=\"p95 duration by statement, recorded and replayed\"></div>\n</figure>\n");
        }

        TimingTable(sb, "Slower on replay, by total time", report.Timing.RegressionsByTotal);
        TimingTable(sb, "Faster on replay, by total time", report.Timing.ImprovementsByTotal);
        TimingTable(sb, "Slower on replay, by p95 (5 or more executions)", report.Timing.RegressionsByP95);
        TimingTable(sb, $"Every statement ({N(statements.Count)}, by replay time)", statements, open: false);
        sb.Append("</section>\n");

        // ---- pace ------------------------------------------------------------------------------------
        var pace = report.Pace;
        sb.Append("<section>\n<h2>Pace</h2>\n");
        if (pace.Throughput.Count > 1)
        {
            sb.Append("<figure class=\"chart\">\n<figcaption><span class=\"title\">Requests started, per ")
              .Append(pace.BucketSeconds switch { 60 => "minute", > 60 => $"{pace.BucketSeconds / 60:0} minutes", 1 => "second", _ => $"{pace.BucketSeconds:0.##} seconds" })
              .Append(", from the start</span><span class=\"legend\"><span class=\"key line recorded2\"></span>Recorded <span class=\"key line replay2\"></span>Replay</span>")
              .Append(run.Pace == "recorded"
                  ? "<span class=\"note\">Where the lines coincide, the replay kept the recorded pace.</span>"
                  : "<span class=\"note\">When each request started on replay, against when it started when recorded: this pace does not follow the recorded timing.</span>")
              .Append("</figcaption>\n")
              .Append("<div id=\"throughput\" class=\"plot\" role=\"img\" aria-label=\"Requests per interval, recorded and replayed\"></div>\n");
            sb.Append("<details><summary>As a table</summary>\n");
            Table(sb, ["From start", "Recorded", "Replay"],
                  pace.Throughput.Select(t => new[] { Seconds(t.Offset), N(t.Recorded), N(t.Replay) }).ToList(), numeric: [0, 1, 2]);
            sb.Append("</details>\n</figure>\n");
        }

        if (pace.Lag.Count > 0)
        {
            sb.Append("<p class=\"lede\">How late requests started behind the recorded schedule, split by cause: waiting on the server " +
                      "(the session's previous request was still running) or the replay host falling behind (it would understate the server).</p>\n");
            Table(sb, ["", "p50", "p95", "p99", "Max"],
            [
                ["Lag, all", Ms(pace.Lag.P50), Ms(pace.Lag.P95), Ms(pace.Lag.P99), Ms(pace.Lag.Max)],
                ["Waiting on the server", Ms(pace.LagServer.P50), Ms(pace.LagServer.P95), Ms(pace.LagServer.P99), Ms(pace.LagServer.Max)],
                ["Replay host behind", Ms(pace.LagHost.P50), Ms(pace.LagHost.P95), Ms(pace.LagHost.P99), Ms(pace.LagHost.Max)],
                ["Scheduler error", Ms(pace.SchedulerError.P50), Ms(pace.SchedulerError.P95), Ms(pace.SchedulerError.P99), Ms(pace.SchedulerError.Max)],
            ], numeric: [1, 2, 3, 4]);
        }
        if (pace.Overlapped > 0)
            sb.Append($"<p>{N(pace.Overlapped)} request(s) were still running (usually blocked) when serial pace started the next one.</p>\n");
        sb.Append("</section>\n");

        // ---- coverage and the run ---------------------------------------------------------------------
        var coverage = report.Coverage;
        sb.Append("<section>\n<h2>Coverage</h2>\n");
        List<string[]> coverageRows =
        [
            ["Events read", N(coverage.Events)],
            ["Requests recorded", N(coverage.Requests)],
            ["Filtered out", N(coverage.Filtered)],
            ["Replayed exactly, from raw bytes or rebuilt", N(coverage.Exact)],
            ["Replayed approximately, as T-SQL", N(coverage.Approximate)],
        ];
        coverageRows.AddRange(coverage.Skipped.OrderByDescending(s => s.Value).Select(s => new[] { $"Skipped: {Recording.Inspector.SkipReasons.GetValueOrDefault(s.Key, s.Key)}", N(s.Value) }));
        coverageRows.AddRange(coverage.NotReplayed.OrderByDescending(s => s.Value).Select(s => new[] { $"Not replayed: {s.Key}", N(s.Value) }));
        coverageRows.Add(["Sessions", N(coverage.Sessions)]);
        coverageRows.Add(["Sessions open before the recording began", N(coverage.SessionsBeforeRecording)]);
        coverageRows.Add(["Sessions with MARS (replayed in order on one connection)", N(coverage.MarsSessions)]);
        coverageRows.Add(["Sessions that could not log in", N(coverage.SessionsLoginFailed)]);
        coverageRows.Add(["Sessions whose connection broke", N(coverage.SessionsBroken)]);
        coverageRows.Add(["Prepared-statement and cursor handles mapped", N(report.Handles.Rewritten)]);
        Table(sb, ["", "Count"], coverageRows, numeric: [1]);
        sb.Append("</section>\n");

        sb.Append("<section>\n<h2>The run</h2>\n");
        List<string[]> details =
        [
            ["Recording", string.Join(", ", run.Recording.Files)],
            ["Recorded", run.Recording.FirstRequest is { } first ? $"{first:yyyy-MM-dd HH:mm:ss} UTC, over {Seconds(run.Recording.Seconds)}" : "-"],
            ["Recorded by", run.Recording.ImportedFrom is { } from
                ? $"Extended Events, in {from}'s replay XML {run.Recording.ImportedFile}, imported by daffyreplay import"
                : $"DaffyTee {run.Recording.DaffyTeeVersion ?? "?"} at {run.Recording.Source ?? "?"}: {run.Recording.Settings ?? "settings not recorded"}"],
            ["Recorded servers", string.Join(", ", run.Recording.RecordedServers)],
            ["Recorded server versions", string.Join(", ", run.Recording.ServerVersions)],
            ["Target", $"{run.Target.Name}: {run.Target.Server}"],
            ["Target answered", $"{run.Target.ServerVersion ?? "?"}; {run.Target.Security ?? ""}"],
            ["Pace", $"{run.Pace}{(run.Pace == "recorded" ? $", speed {run.Speed:0.###}" : "")}"],
            ["Started", $"{run.Started:yyyy-MM-dd HH:mm:ss} UTC"],
            ["Ended", $"{run.Ended:yyyy-MM-dd HH:mm:ss} UTC"],
        ];
        details.AddRange(run.Filters.Select(f => new[] { $"Filter --{f.Key}", f.Value }));
        details.AddRange(run.Options.Select(o => new[] { $"Option {o.Key}", o.Value }));
        Table(sb, ["", ""], details, header: false);
        sb.Append("</section>\n");

        sb.Append("<footer><p>Recordings, results and this report hold SQL and parameter values: treat them as you treat the recording itself.</p></footer>\n");
        sb.Append("</main>\n<div id=\"tip\" class=\"tip\" role=\"tooltip\" hidden></div>\n");

        // The charts' data, as JSON. The serializer escapes <, > and &, so nothing in it can close the tag.
        var chartData = new
        {
            statements = statements.Take(15).Select(s => new { s.Fingerprint, s.Statement, s.Count, recorded = s.Recorded.P95, replay = s.Replay.P95 }),
            throughput = pace.Throughput.Select(t => new { t.Offset, t.Recorded, t.Replay }),
            bucket = pace.BucketSeconds,
        };
        sb.Append("<script type=\"application/json\" id=\"data\">")
          .Append(JsonSerializer.Serialize(chartData))
          .Append("</script>\n<script>\n").Append(Script).Append("</script>\n</body>\n</html>\n");

        return sb.ToString();
    }

    private static IEnumerable<(string Label, long Count)> OutcomeRows(Report report)
    {
        var labels = new (string Key, string Label)[]
        {
            (Outcome.Same, "The same: ok both times, or the same errors"),
            (Outcome.NewError, "New error: ok when recorded, failed on replay"),
            (Outcome.ChangedError, "Changed error: failed both times, differently"),
            (Outcome.FixedError, "Fixed: failed when recorded, ok on replay"),
            (Outcome.Timeout, "Timed out on replay"),
            (Outcome.ConnectionFailed, "The connection broke"),
            (Outcome.NotReplayed, "Not replayed (its session could not log in, or broke)"),
            (Outcome.CancelledWhenRecorded, "Cancelled when recorded; replayed to completion"),
            (Outcome.IncompleteWhenRecorded, "Incomplete when recorded (the session ended first)"),
        };
        foreach (var (key, label) in labels)
            if (report.Outcomes.Counts.GetValueOrDefault(key) is > 0 and var count) yield return (label, count);
    }

    private static void Tile(StringBuilder sb, string label, string value, string note, string? status = null)
    {
        sb.Append("<div class=\"tile\">")
          .Append($"<p class=\"label\">{E(label)}</p><p class=\"value\">{E(value)}</p>")
          .Append("<p class=\"note\">");
        if (status is not null) sb.Append(StatusBadge(status));
        sb.Append($"{E(note)}</p></div>\n");
    }

    private static string StatusBadge(string status)
    {
        string icon = status switch { "good" => "✓", "warning" => "!", "serious" => "!", _ => "✕" };
        string label = status switch { "good" => "OK", "warning" => "Check", "serious" => "Attention", _ => "Problem" };
        return $"<span class=\"status {status}\"><span aria-hidden=\"true\">{icon}</span> {label}</span> ";
    }

    private static void ErrorTable(StringBuilder sb, string title, string description, IReadOnlyList<ErrorGroup> errors, string? status)
    {
        if (errors.Count == 0) return;
        sb.Append($"<h3>{(status is null ? "" : StatusBadge(status))}{E(title)} <span class=\"muted\">{E(description)}</span></h3>\n");
        Table(sb, ["Requests", "Error", "Message", "Statement"],
              errors.Select(e => new[]
              {
                  N(e.Count), e.Number == 0 ? "-" : e.Number.ToString(CultureInfo.InvariantCulture),
                  // The hint is for errors that are new on replay; one that happened when recorded too is not a sign of drift.
                  e.Message + (e.LikelyDataState && status is "critical" or "serious" ? "  (typical of data that differs: was the database restored?)" : ""),
                  e.Statement,
              }).ToList(), numeric: [0, 1], code: [3]);
    }

    private static void StatementTable(StringBuilder sb, string title, string description, IReadOnlyList<StatementGroup> groups)
    {
        if (groups.Count == 0) return;
        sb.Append($"<h3>{E(title)} <span class=\"muted\">{E(description)}</span></h3>\n");
        Table(sb, ["Requests", "Statement"], groups.Select(g => new[] { N(g.Count), g.Statement }).ToList(), numeric: [0], code: [1]);
    }

    private static void TimingTable(StringBuilder sb, string title, IReadOnlyList<StatementTiming> statements, bool open = true)
    {
        if (statements.Count == 0) return;
        if (!open) sb.Append($"<details><summary>{E(title)}</summary>\n");
        else sb.Append($"<h3>{E(title)}</h3>\n");
        Table(sb, ["Runs", "Recorded p50", "Replay p50", "Recorded p95", "Replay p95", "Total change", "Statement"],
              statements.Select(s => new[]
              {
                  N(s.Count), Ms(s.Recorded.P50), Ms(s.Replay.P50), Ms(s.Recorded.P95), Ms(s.Replay.P95),
                  (s.DeltaMs >= 0 ? "+" : "−") + Ms(Math.Abs(s.DeltaMs)), s.Statement,
              }).ToList(), numeric: [0, 1, 2, 3, 4, 5], code: [6]);
        if (!open) sb.Append("</details>\n");
    }

    private static void Table(StringBuilder sb, string[] headers, IReadOnlyList<string[]> rows, int[]? numeric = null, int[]? code = null, bool header = true)
    {
        numeric ??= [];
        code ??= [];
        sb.Append("<div class=\"table\"><table>\n");
        if (header)
        {
            sb.Append("<thead><tr>");
            for (int i = 0; i < headers.Length; i++)
                sb.Append($"<th{(numeric.Contains(i) ? " class=\"num\"" : "")}>{E(headers[i])}</th>");
            sb.Append("</tr></thead>\n");
        }
        sb.Append("<tbody>\n");
        foreach (var row in rows)
        {
            sb.Append("<tr>");
            for (int i = 0; i < row.Length; i++)
            {
                string cell = E(row[i]);
                if (code.Contains(i)) cell = $"<code>{E(Recording.Inspector.Shorten(row[i], 400))}</code>";
                sb.Append($"<td{(numeric.Contains(i) ? " class=\"num\"" : "")}>{cell}</td>");
            }
            sb.Append("</tr>\n");
        }
        sb.Append("</tbody></table></div>\n");
    }

    private static string E(string? text) => WebUtility.HtmlEncode(text ?? "");

    private static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public static string Ms(double ms) => ms switch
    {
        >= 10_000 => $"{ms / 1000:N1} s",
        >= 100 => $"{ms:N0} ms",
        >= 10 => $"{ms:N1} ms",
        _ => $"{ms:N2} ms",
    };

    private static string Seconds(double seconds) =>
        seconds < 60 ? $"{seconds:0.#} s" : seconds < 3600 ? $"{(int)(seconds / 60)} min {(int)(seconds % 60)} s" : $"{(int)(seconds / 3600)} h {(int)(seconds % 3600 / 60)} min";

    private const string Css = """
        :root {
          color-scheme: light;
          --surface: #fcfcfb; --surface-2: #f3f2ef; --rule: #e4e2dc; --grid: #e9e7e2;
          --text: #0b0b0b; --text-2: #52514e; --text-3: #6f6d68;
          --recorded: #86b6ef; --replay: #1c5cab;
          --series-1: #2a78d6; --series-2: #eb6834;
          --good: #0ca30c; --good-text: #006300; --warning: #fab219; --serious: #ec835a; --critical: #d03b3b;
          --callout: #fff7e6; --callout-rule: #f5d58c;
        }
        @media (prefers-color-scheme: dark) {
          :root:not([data-theme="light"]) {
            color-scheme: dark;
            --surface: #1a1a19; --surface-2: #232321; --rule: #383835; --grid: #2e2e2b;
            --text: #f0efec; --text-2: #c3c2b7; --text-3: #9d9b93;
            --recorded: #3987e5; --replay: #b7d3f6;
            --series-1: #3987e5; --series-2: #d95926;
            --good-text: #4cc24c;
            --callout: #2b2618; --callout-rule: #6b5a2a;
          }
        }
        :root[data-theme="dark"] {
          color-scheme: dark;
          --surface: #1a1a19; --surface-2: #232321; --rule: #383835; --grid: #2e2e2b;
          --text: #f0efec; --text-2: #c3c2b7; --text-3: #9d9b93;
          --recorded: #3987e5; --replay: #b7d3f6;
          --series-1: #3987e5; --series-2: #d95926;
          --good-text: #4cc24c;
          --callout: #2b2618; --callout-rule: #6b5a2a;
        }
        * { box-sizing: border-box; }
        body { margin: 0; background: var(--surface); color: var(--text);
               font: 15px/1.5 system-ui, -apple-system, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif; }
        main { max-width: 1120px; margin: 0 auto; padding: 32px 16px 48px; }
        header { margin-bottom: 24px; }
        .eyebrow { margin: 0; color: var(--text-3); font-size: 13px; letter-spacing: .02em; }
        h1 { margin: 4px 0 6px; font-size: 26px; line-height: 1.25; font-weight: 600; overflow-wrap: anywhere; }
        h2 { margin: 40px 0 8px; font-size: 19px; font-weight: 600; }
        h3 { margin: 24px 0 8px; font-size: 15px; font-weight: 600; }
        .sub, .lede, .muted { color: var(--text-2); }
        .sub { margin: 0; overflow-wrap: anywhere; }
        .lede { margin: 0 0 12px; max-width: 75ch; }
        .muted { font-weight: 400; }
        .tiles { display: grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr)); gap: 12px; }
        .tile { background: var(--surface-2); border-radius: 8px; padding: 14px 16px; }
        .tile p { margin: 0; }
        .tile .label { color: var(--text-2); font-size: 13px; }
        .tile .value { font-size: 26px; font-weight: 600; line-height: 1.3; margin: 2px 0; overflow-wrap: anywhere; }
        .tile .note { color: var(--text-3); font-size: 13px; }
        .status { display: inline-flex; gap: 4px; align-items: center; font-weight: 600; font-size: 12px;
                  padding: 1px 7px; border-radius: 10px; margin-right: 4px; color: var(--text); }
        .status.good { background: color-mix(in srgb, var(--good) 18%, transparent); }
        .status.warning { background: color-mix(in srgb, var(--warning) 28%, transparent); }
        .status.serious { background: color-mix(in srgb, var(--serious) 25%, transparent); }
        .status.critical { background: color-mix(in srgb, var(--critical) 22%, transparent); }
        .callout { margin-top: 20px; background: var(--callout); border: 1px solid var(--callout-rule); border-radius: 8px; padding: 4px 16px; }
        .callout h2 { margin: 12px 0 4px; font-size: 15px; }
        .callout.imported { border-left-width: 6px; }
        .callout ul { margin: 4px 0 12px; padding-left: 20px; }
        .callout li { margin: 4px 0; }
        .table { overflow-x: auto; margin: 4px 0 8px; }
        table { border-collapse: collapse; width: 100%; font-size: 13.5px; }
        th, td { text-align: left; padding: 6px 10px; border-bottom: 1px solid var(--rule); vertical-align: top; }
        th { color: var(--text-2); font-weight: 600; white-space: nowrap; }
        td.num, th.num { text-align: right; font-variant-numeric: tabular-nums; white-space: nowrap; }
        code { font: 12.5px/1.45 ui-monospace, SFMono-Regular, Consolas, "Liberation Mono", monospace; overflow-wrap: anywhere; }
        details { margin: 12px 0; }
        summary { cursor: pointer; color: var(--text-2); font-weight: 600; }
        figure.chart { margin: 16px 0 8px; }
        figcaption { display: flex; flex-wrap: wrap; gap: 4px 16px; align-items: baseline; margin-bottom: 8px; }
        figcaption .title { font-weight: 600; }
        figcaption .legend { color: var(--text-2); font-size: 13px; display: inline-flex; gap: 6px; align-items: center; }
        figcaption .note { color: var(--text-3); font-size: 13px; width: 100%; }
        .key { display: inline-block; vertical-align: middle; }
        .key.dot { width: 9px; height: 9px; border-radius: 50%; }
        .key.line { width: 16px; height: 2px; border-radius: 1px; }
        .key.recorded { background: var(--recorded); }
        .key.replay { background: var(--replay); margin-left: 8px; }
        .key.recorded2 { background: var(--series-1); }
        .key.replay2 { background: var(--series-2); margin-left: 8px; }
        .plot { width: 100%; }
        .plot svg { display: block; width: 100%; height: auto; overflow: visible; }
        .plot text { fill: var(--text-2); font-size: 12px; }
        .plot .axis { stroke: var(--rule); stroke-width: 1; }
        .plot .grid { stroke: var(--grid); stroke-width: 1; }
        .plot .hit { fill: transparent; }
        .plot .row:hover .connector, .plot .row:focus .connector { stroke: var(--text-3); }
        .tip { position: fixed; z-index: 10; pointer-events: none; background: var(--surface); color: var(--text);
               border: 1px solid var(--rule); border-radius: 6px; padding: 8px 10px; font-size: 13px;
               box-shadow: 0 4px 16px rgba(0,0,0,.12); max-width: 360px; }
        .tip .t { color: var(--text-2); margin-bottom: 4px; overflow-wrap: anywhere; }
        .tip .r { display: flex; gap: 8px; align-items: center; }
        .tip .r b { font-variant-numeric: tabular-nums; }
        .tip .k { width: 12px; height: 2px; border-radius: 1px; }
        footer { margin-top: 48px; color: var(--text-3); font-size: 13px; border-top: 1px solid var(--rule); }
        @media (max-width: 600px) { h1 { font-size: 21px; } .tile .value { font-size: 21px; } }

        """;

    private const string Script = """
        (function () {
          const data = JSON.parse(document.getElementById('data').textContent);
          const tip = document.getElementById('tip');
          const svgNs = 'http://www.w3.org/2000/svg';
          const css = name => getComputedStyle(document.documentElement).getPropertyValue(name).trim();

          function el(name, attrs, parent) {
            const node = document.createElementNS(svgNs, name);
            for (const k in attrs) node.setAttribute(k, attrs[k]);
            if (parent) parent.appendChild(node);
            return node;
          }
          function ms(v) {
            return v >= 10000 ? (v / 1000).toFixed(1) + ' s' : v >= 100 ? v.toFixed(0) + ' ms' : v >= 10 ? v.toFixed(1) + ' ms' : v.toFixed(2) + ' ms';
          }
          function showTip(evt, title, rows) {
            tip.textContent = '';
            const t = document.createElement('div'); t.className = 't'; t.textContent = title; tip.appendChild(t);
            for (const row of rows) {
              const r = document.createElement('div'); r.className = 'r';
              const k = document.createElement('span'); k.className = 'k'; k.style.background = row.color;
              const b = document.createElement('b'); b.textContent = row.value;
              const s = document.createElement('span'); s.textContent = row.label;
              r.append(k, b, s); tip.appendChild(r);
            }
            tip.hidden = false;
            const box = tip.getBoundingClientRect();
            let x = evt.clientX + 14, y = evt.clientY + 14;
            if (x + box.width > window.innerWidth - 8) x = evt.clientX - box.width - 14;
            if (y + box.height > window.innerHeight - 8) y = evt.clientY - box.height - 14;
            tip.style.left = Math.max(8, x) + 'px'; tip.style.top = Math.max(8, y) + 'px';
          }
          function hideTip() { tip.hidden = true; }

          // p95 by statement: a dumbbell per statement, recorded to replay, on a log scale.
          function dumbbell(host, rows) {
            if (!host || rows.length === 0) return;
            const width = Math.max(host.clientWidth, 320), rowH = 26, top = 8, bottom = 28;
            const left = Math.min(260, width * 0.38), right = 16;
            const height = top + rows.length * rowH + bottom;
            const svg = el('svg', { viewBox: `0 0 ${width} ${height}`, width, height });
            const values = rows.flatMap(r => [r.recorded, r.replay]).filter(v => v > 0);
            const lo = Math.pow(10, Math.floor(Math.log10(Math.min(...values, 0.1))));
            const hi = Math.pow(10, Math.ceil(Math.log10(Math.max(...values, 1))));
            const x = v => left + (Math.log10(Math.max(v, lo)) - Math.log10(lo)) / (Math.log10(hi) - Math.log10(lo)) * (width - left - right);
            for (let p = lo; p <= hi * 1.0001; p *= 10) {
              el('line', { x1: x(p), x2: x(p), y1: top, y2: height - bottom, class: 'grid' }, svg);
              const label = el('text', { x: x(p), y: height - 8, 'text-anchor': 'middle' }, svg);
              label.textContent = p >= 1000 ? (p / 1000) + ' s' : Number(p.toPrecision(1)) + ' ms';
            }
            rows.forEach((r, i) => {
              const y = top + i * rowH + rowH / 2;
              const g = el('g', { class: 'row', tabindex: 0 }, svg);
              el('rect', { x: 0, y: y - rowH / 2, width, height: rowH, class: 'hit' }, g);
              const name = el('text', { x: left - 10, y: y + 4, 'text-anchor': 'end' }, g);
              const text = r.Statement.length > 42 ? r.Statement.slice(0, 41) + '…' : r.Statement;
              name.textContent = text;
              el('line', { x1: x(r.recorded), x2: x(r.replay), y1: y, y2: y, stroke: css('--grid'), 'stroke-width': 4, 'stroke-linecap': 'round', class: 'connector' }, g);
              el('circle', { cx: x(r.recorded), cy: y, r: 5, fill: css('--recorded'), stroke: css('--surface'), 'stroke-width': 2 }, g);
              el('circle', { cx: x(r.replay), cy: y, r: 5, fill: css('--replay'), stroke: css('--surface'), 'stroke-width': 2 }, g);
              const show = evt => showTip(evt, r.Statement, [
                { color: css('--recorded'), value: ms(r.recorded), label: 'recorded p95' },
                { color: css('--replay'), value: ms(r.replay), label: 'replay p95' },
                { color: 'transparent', value: r.Count.toLocaleString(), label: 'runs' }]);
              g.addEventListener('pointermove', show);
              g.addEventListener('focus', () => { const b = g.getBoundingClientRect(); show({ clientX: b.left + left, clientY: b.top }); });
              g.addEventListener('pointerleave', hideTip);
              g.addEventListener('blur', hideTip);
            });
            el('line', { x1: left, x2: width - right, y1: height - bottom, y2: height - bottom, class: 'axis' }, svg);
            host.replaceChildren(svg);
          }

          // Requests per interval: two lines, recorded and replay, with a crosshair readout.
          function throughput(host, points, bucket) {
            if (!host || points.length < 2) return;
            const width = Math.max(host.clientWidth, 320), height = 240, left = 48, right = 72, top = 12, bottom = 28;
            const svg = el('svg', { viewBox: `0 0 ${width} ${height}`, width, height });
            const maxX = points[points.length - 1].Offset, minX = points[0].Offset;
            const maxY = Math.max(1, ...points.map(p => Math.max(p.Recorded, p.Replay)));
            // A round step - 1, 2 or 5 times a power of ten - and as many as reach the maximum.
            const raw = maxY / 4, magnitude = Math.pow(10, Math.floor(Math.log10(raw)));
            const step = [1, 2, 5, 10].map(m => m * magnitude).find(s => s >= raw);
            const ticks = Math.ceil(maxY / step), niceMax = ticks * step;
            const x = v => left + (v - minX) / Math.max(1, maxX - minX) * (width - left - right);
            const y = v => top + (1 - v / niceMax) * (height - top - bottom);
            for (let i = 0; i <= ticks; i++) {
              const v = niceMax * i / ticks;
              el('line', { x1: left, x2: width - right, y1: y(v), y2: y(v), class: i === 0 ? 'axis' : 'grid' }, svg);
              const t = el('text', { x: left - 8, y: y(v) + 4, 'text-anchor': 'end' }, svg);
              t.textContent = v >= 1000 ? (v / 1000).toFixed(v % 1000 ? 1 : 0) + 'K' : v.toFixed(0);
            }
            const fmt = s => s < 10 && bucket < 1 ? s.toFixed(1) + ' s' : s < 60 ? s.toFixed(0) + ' s' : s < 3600 ? (s / 60).toFixed(0) + ' min' : (s / 3600).toFixed(1) + ' h';
            // Time ticks at a round interval, about five of them.
            const span = Math.max(bucket, maxX - minX);
            const tickStep = [0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 14400].find(s => span / s <= 6) || 28800;
            for (let v = Math.ceil(minX / tickStep) * tickStep; v <= maxX + 1e-9; v += tickStep) {
              const t = el('text', { x: x(v), y: height - 8, 'text-anchor': v === minX ? 'start' : 'middle' }, svg);
              t.textContent = fmt(v);
            }
            const series = [{ key: 'Recorded', color: css('--series-1') }, { key: 'Replay', color: css('--series-2') }];
            for (const s of series) {
              const d = points.map((p, i) => (i ? 'L' : 'M') + x(p.Offset).toFixed(1) + ' ' + y(p[s.key]).toFixed(1)).join(' ');
              el('path', { d, fill: 'none', stroke: s.color, 'stroke-width': 2, 'stroke-linejoin': 'round', 'stroke-linecap': 'round' }, svg);
            }
            // End labels in text ink beside each line's end dot - only where the lines end apart.
            // Where they meet, labels nudged off their lines would mislead: the legend and the
            // readout carry which is which.
            const last = points[points.length - 1];
            const ends = series.map(s => ({ s, y: y(last[s.key]) }));
            const apart = Math.abs(ends[0].y - ends[1].y) >= 14;
            for (const e of ends) {
              el('circle', { cx: x(last.Offset), cy: e.y, r: 4, fill: e.s.color, stroke: css('--surface'), 'stroke-width': 2 }, svg);
              if (!apart) continue;
              const t = el('text', { x: x(last.Offset) + 10, y: e.y + 4 }, svg);
              t.textContent = e.s.key;
            }
            const cross = el('line', { y1: top, y2: height - bottom, stroke: css('--text-3'), 'stroke-width': 1, visibility: 'hidden' }, svg);
            const hit = el('rect', { x: left, y: top, width: width - left - right, height: height - top - bottom, class: 'hit' }, svg);
            hit.addEventListener('pointermove', evt => {
              const box = svg.getBoundingClientRect();
              const px = (evt.clientX - box.left) * width / box.width;
              let best = points[0];
              for (const p of points) if (Math.abs(x(p.Offset) - px) < Math.abs(x(best.Offset) - px)) best = p;
              cross.setAttribute('x1', x(best.Offset)); cross.setAttribute('x2', x(best.Offset)); cross.setAttribute('visibility', 'visible');
              showTip(evt, fmt(best.Offset) + ' to ' + fmt(best.Offset + bucket) + ' from the start', series.map(s => ({
                color: s.color, value: best[s.key].toLocaleString(), label: s.key.toLowerCase() + ' requests' })));
            });
            hit.addEventListener('pointerleave', () => { cross.setAttribute('visibility', 'hidden'); hideTip(); });
            host.replaceChildren(svg);
          }

          function draw() {
            dumbbell(document.getElementById('dumbbell'), data.statements);
            throughput(document.getElementById('throughput'), data.throughput, data.bucket);
          }
          draw();
          let pending;
          window.addEventListener('resize', () => { clearTimeout(pending); pending = setTimeout(draw, 150); });
          window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', draw);
        })();

        """;
}
