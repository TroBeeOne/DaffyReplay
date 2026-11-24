using DaffyReplay.Recording;
using DaffyReplay.Results;

namespace DaffyReplay.Cli;

/// <summary>
/// <c>daffyreplay compare RUN1 RUN2</c>: two replays of the same recording, side by side - before
/// and after an index, a compatibility-level change, an upgrade. Recorded production timings come
/// from other hardware under other concurrency; two replays on the same test server do not, which
/// makes this the most telling comparison DaffyReplay offers. Statements are matched by fingerprint.
/// </summary>
internal static class CompareCommand
{
    public const string Usage = """
          daffyreplay compare RUN1 RUN2 [--top N]
              Two runs' reports side by side (their folders, or their report.json files): outcomes,
              timings, and the statements that got slower or faster from RUN1 to RUN2.

        """;

    public static int Run(IReadOnlyList<string> args, TextWriter output)
    {
        var parsed = Arguments.Parse(args, new HashSet<string>(["--top"]), new HashSet<string>());
        if (parsed.Positional.Count != 2) throw new ArgumentException("usage: daffyreplay compare RUN1 RUN2");
        int top = parsed.Int("--top", 1) ?? 10;

        var (a, aName) = Load(parsed.Positional[0]);
        var (b, bName) = Load(parsed.Positional[1]);

        output.WriteLine();
        output.WriteLine($"  A           {aName}: {Describe(a)}");
        output.WriteLine($"  B           {bName}: {Describe(b)}");

        var warnings = new List<string>();
        if (!a.Run.Recording.Files.Select(Path.GetFileName).SequenceEqual(b.Run.Recording.Files.Select(Path.GetFileName)))
            warnings.Add("the two runs replayed different recordings: the comparison may say little");
        if (a.Run.Pace != b.Run.Pace) warnings.Add($"the runs used different paces ({a.Run.Pace}, {b.Run.Pace}): timings are not like for like");
        if (a.Run.Target.Server != b.Run.Target.Server) warnings.Add($"the runs replayed into different servers ({a.Run.Target.Server}, {b.Run.Target.Server})");

        long Count(Report r, string outcome) => r.Outcomes.Counts.GetValueOrDefault(outcome);
        output.WriteLine();
        Row(output, "replayed", a.Coverage.Replayed, b.Coverage.Replayed);
        Row(output, "new errors", Count(a, Outcome.NewError), Count(b, Outcome.NewError));
        Row(output, "changed errors", Count(a, Outcome.ChangedError), Count(b, Outcome.ChangedError));
        Row(output, "fixed errors", Count(a, Outcome.FixedError), Count(b, Outcome.FixedError));
        Row(output, "rows differ", a.Outcomes.RowsDiffer, b.Outcomes.RowsDiffer);
        Row(output, "timeouts", Count(a, Outcome.Timeout), Count(b, Outcome.Timeout));
        Row(output, "failed", Count(a, Outcome.ConnectionFailed) + Count(a, Outcome.NotReplayed), Count(b, Outcome.ConnectionFailed) + Count(b, Outcome.NotReplayed));

        output.WriteLine();
        Time(output, "p50", a.Timing.Replay.P50, b.Timing.Replay.P50);
        Time(output, "p95", a.Timing.Replay.P95, b.Timing.Replay.P95);
        Time(output, "p99", a.Timing.Replay.P99, b.Timing.Replay.P99);
        Time(output, "total", a.Timing.ReplayTotalMs, b.Timing.ReplayTotalMs);

        // Errors in B that A did not have, by error and statement.
        var aErrors = a.Outcomes.NewErrors.Concat(a.Outcomes.ChangedErrors).Select(e => (e.Number, e.Fingerprint)).ToHashSet();
        var appeared = b.Outcomes.NewErrors.Concat(b.Outcomes.ChangedErrors).Where(e => !aErrors.Contains((e.Number, e.Fingerprint))).ToList();
        if (appeared.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("  errors in B that A did not have:");
            foreach (var e in appeared.Take(top))
                output.WriteLine($"    {Inspector.N(e.Count),6} x Msg {e.Number}: {Inspector.Shorten(e.Message, 70)}  in {Inspector.Shorten(e.Statement, 60)}");
        }

        var aStatements = a.Timing.Statements.ToDictionary(s => s.Fingerprint);
        var matched = b.Timing.Statements.Where(s => aStatements.ContainsKey(s.Fingerprint))
            .Select(s => (Statement: s, Before: aStatements[s.Fingerprint]))
            .Select(m => (m.Statement, m.Before, Delta: m.Statement.ReplayTotalMs - m.Before.ReplayTotalMs))
            .ToList();

        void Statements(string title, IEnumerable<(StatementTiming Statement, StatementTiming Before, double Delta)> list)
        {
            var shown = list.Take(top).ToList();
            if (shown.Count == 0) return;
            output.WriteLine();
            output.WriteLine($"  {title}:");
            output.WriteLine($"    {"change",10}  {"p95 A",9} -> {"p95 B",-9}  {"runs",6}  statement");
            foreach (var (s, before, delta) in shown)
                output.WriteLine($"    {Signed(delta),10}  {HtmlReport.Ms(before.Replay.P95),9} -> {HtmlReport.Ms(s.Replay.P95),-9}  {Inspector.N(s.Count),6}  {Inspector.Shorten(s.Statement, 70)}");
        }

        Statements("slower in B, by total time", matched.Where(m => m.Delta > 0.5).OrderByDescending(m => m.Delta));
        Statements("faster in B, by total time", matched.Where(m => m.Delta < -0.5).OrderBy(m => m.Delta));

        int onlyA = a.Timing.Statements.Count(s => !b.Timing.Statements.Any(x => x.Fingerprint == s.Fingerprint));
        int onlyB = b.Timing.Statements.Count(s => !aStatements.ContainsKey(s.Fingerprint));
        if (onlyA + onlyB > 0)
            output.WriteLine($"\n  {onlyA} statement(s) appear only in A's report, {onlyB} only in B's (each report keeps its 200 busiest)");

        foreach (string warning in warnings) output.WriteLine($"\n  warning     {warning}");
        output.WriteLine();
        return 0;
    }

    private static (Report Report, string Name) Load(string path)
    {
        string json = Directory.Exists(path) ? Path.Combine(path, "report.json") : path;
        if (!File.Exists(json)) throw new ArgumentException($"no report.json at {json}");
        string name = Directory.Exists(path) ? Path.GetFileName(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar)) : Path.GetFileName(json);
        return (Report.Load(json), name);
    }

    private static string Describe(Report r) =>
        $"{r.Run.Pace} pace into {r.Run.Target.Name} ({r.Run.Target.ServerVersion ?? r.Run.Target.Server}), {r.Run.Started.ToLocalTime():yyyy-MM-dd HH:mm}";

    private static void Row(TextWriter output, string label, long a, long b) =>
        output.WriteLine($"  {label,-14} {Inspector.N(a),10} -> {Inspector.N(b),-10}{(a == b ? "" : b > a ? $"  (+{Inspector.N(b - a)})" : $"  (-{Inspector.N(a - b)})")}");

    private static void Time(TextWriter output, string label, double a, double b) =>
        output.WriteLine($"  {"replay " + label,-14} {HtmlReport.Ms(a),10} -> {HtmlReport.Ms(b),-10}{(a > 0 ? $"  ({(b - a) / a:+0%;-0%;0%})" : "")}");

    private static string Signed(double ms) => (ms >= 0 ? "+" : "-") + HtmlReport.Ms(Math.Abs(ms));
}
