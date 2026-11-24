using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using DaffyReplay.IO;
using DaffyReplay.Recording;
using DaffyReplay.Replay;
using DaffyReplay.Results;
using DaffyReplay.Security;
using DaffyReplay.Target;

namespace DaffyReplay.Cli;

/// <summary>
/// <c>daffyreplay run</c>: index the recording, check the target is not the recorded server, show
/// what is about to happen and ask, replay at the chosen pace with progress on the console, and
/// write the run's folder - run.json, replay.log, report.json, and results.ndjson when asked.
/// </summary>
internal static class RunCommand
{
    public const string Usage = """
          daffyreplay run FILES... --target NAME [--pace recorded|fast|serial] [--speed N]
                  [filters] [--max-sessions N] [--serial-wait SECONDS] [--request-timeout SECONDS]
                  [--keep-app-name] [--keep-host-name] [--packet-size N] [--response-kb N]
                  [--same-target] [--approximate] [--replay-cancels] [--results] [--out DIR]
                  [--yes] [--verbose]
              Replay a recording against a target (see 'target add'). Restore the target's
              database to the moment recording began first: without that, inserts collide,
              updates touch other rows and the report is mostly noise.
                --pace recorded       each request at its recorded time (the default); --speed 2
                                      halves every gap
                --pace fast           no waiting: every session back to back, all at once
                                      (--max-sessions caps how many at a time)
                --pace serial         one request at a time, in recorded order: does every
                                      statement still work, and how long does each take alone.
                                      A request still running after --serial-wait seconds (2) is
                                      usually blocked by a lock another session releases later;
                                      the replay carries on, as the recording did
                --request-timeout S   cancel a request after S seconds, as a client's command
                                      timeout would (default: none; 30 for serial pace)
                --keep-app-name       log in with each session's recorded application name (it is
                                      "DaffyReplay" otherwise); --keep-host-name likewise
                --response-kb N       the --response-kb the recording was made with (default 64)
                --same-target         replay into the server the recording was made on - after a
                                      restore, on a pre-production server, it can be what you want
                --replay-cancels      cancel a request that was cancelled when recorded (a command
                                      timeout, SqlCommand.Cancel) after as long as it ran then,
                                      rather than letting it run to completion
                --approximate         replay procedure calls recorded without raw bytes (DaffyTee
                                      without --raw) as T-SQL with their values as literals, marked
                                      approximate and compared on results and errors only
                --results             also write results.ndjson: every request, recorded and replay
                                      side by side
                --out DIR             the run's folder (default replay-YYYYMMDD-HHMMSS here)
                --yes                 do not ask before starting (scripts)

        """;

    private static readonly HashSet<string> WithValue =
    [
        .. Arguments.FilterOptions, "--target", "--pace", "--speed", "--max-sessions", "--request-timeout",
        "--packet-size", "--response-kb", "--out", "--serial-wait",
    ];

    private static readonly HashSet<string> Flags =
        [.. Arguments.FilterFlags, "--keep-app-name", "--keep-host-name", "--same-target", "--results", "--yes", "--verbose", "--approximate",
         "--debug-no-descriptors", "--debug-no-handles", "--replay-cancels"];

    public static async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var parsed = Arguments.Parse(args, WithValue, Flags);
        Log.Verbose = parsed.Has("--verbose");

        string targetName = parsed.Get("--target") ?? throw new ArgumentException("--target NAME is required (daffyreplay target list shows them)");
        var store = TargetStore.Open(AppPaths.EnsureHome());
        var target = store.Find(targetName)
            ?? throw new ArgumentException($"no target '{targetName}' - add one with: daffyreplay target add {targetName}");
        var endpoint = target.Endpoint;

        var pace = (parsed.Get("--pace") ?? "recorded").ToLowerInvariant() switch
        {
            "recorded" => Pace.Recorded,
            "fast" => Pace.Fast,
            "serial" => Pace.Serial,
            var other => throw new ArgumentException($"--pace {other}: recorded, fast or serial"),
        };
        double speed = parsed.Double("--speed", 0.001) ?? 1;
        if (speed != 1 && pace != Pace.Recorded) throw new ArgumentException("--speed applies to --pace recorded only");
        int? maxSessions = parsed.Int("--max-sessions", 1);
        double? timeoutSeconds = parsed.Double("--request-timeout", 0.001);
        if (timeoutSeconds is null && pace == Pace.Serial) timeoutSeconds = 30;

        var options = new ReplayOptions(
            Pace: pace,
            Speed: speed,
            MaxSessions: maxSessions,
            RequestTimeout: timeoutSeconds is { } s ? TimeSpan.FromSeconds(s) : null,
            KeepAppName: parsed.Has("--keep-app-name"),
            KeepHostName: parsed.Has("--keep-host-name"),
            PacketSize: parsed.Int("--packet-size", 512) ?? LoginOptions.DefaultPacketSize,
            RecordedResponseKb: parsed.Int("--response-kb", 0) ?? 64,
            AllowApproximate: parsed.Has("--approximate"))
        {
            SerialWait = TimeSpan.FromSeconds(parsed.Double("--serial-wait", 0.01) ?? 2),
            PatchDescriptors = !parsed.Has("--debug-no-descriptors"),
            MapHandles = !parsed.Has("--debug-no-handles"),
            ReplayCancels = parsed.Has("--replay-cancels"),
        };

        Console.WriteLine();
        Console.WriteLine($"  DaffyReplay {Program.ToolVersion()}: reading the recording...");
        using var files = RecordingFiles.Open(parsed.Positional);
        using var index = RecordingIndex.Build(files, parsed.Filter(), options.AllowApproximate, cancellationToken: cancellationToken);

        var sessions = index.Sessions.Values.Where(s => s.Replayable > 0).ToList();
        if (index.Stats.Replayable == 0)
        {
            Console.WriteLine("  Nothing to replay: no request in the recording passes the filters and can be replayed.");
            Inspector.Print(index, Console.Out, top: 5);
            return 1;
        }

        // Never into the server the recording was made on, unless that is plainly what is wanted.
        var recordedServers = sessions.SelectMany(s => new[] { s.Target, s.RoutedTo }).OfType<string>()
            .Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (await MatchingServerAsync(endpoint, recordedServers, cancellationToken) is { } same && !parsed.Has("--same-target"))
        {
            Console.Error.WriteLine($"  Refused: target '{target.Name}' ({endpoint.Host}:{endpoint.Port}) is the server this recording was made on ({same}).");
            Console.Error.WriteLine("  A replay writes everything the application wrote. To replay into it anyway - a restored pre-production");
            Console.Error.WriteLine("  server, say - add --same-target.");
            return 1;
        }

        long firstTicks = sessions.Min(s => Math.Min(s.StartTicks ?? long.MaxValue, s.FirstRequestTicks));
        long lastTicks = sessions.Max(s => s.LastRequestTicks);
        var recordedSpan = TimeSpan.FromTicks(Math.Max(0, lastTicks - firstTicks));
        var databases = sessions.Select(s => s.Database ?? "(login default)").Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        PrintBanner(index, target, endpoint, options, sessions.Count, recordedSpan, databases);
        if (!parsed.Has("--yes"))
        {
            if (Console.IsInputRedirected)
            {
                Console.Error.WriteLine("  Not started: add --yes to replay without being asked (in a script).");
                return 1;
            }
            if (!Prompter.ForConsole(Console.Out).Confirm("Replay now", false))
            {
                Console.WriteLine("  Not started.");
                return 1;
            }
        }

        string folder = Path.GetFullPath(parsed.Get("--out") ?? $"replay-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(folder);
        using var log = LogFile.Attach(Path.Combine(folder, "replay.log"));
        using var results = parsed.Has("--results") ? new RollingFileWriter(Path.Combine(folder, "results.ndjson")) : null;

        // Throughput buckets sized to the recording: a chart of 30 to 120 points, at a round interval.
        double[] intervals = [0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600];
        double bucketSeconds = intervals.FirstOrDefault(i => recordedSpan.TotalSeconds / i <= 120, 3600);
        var context = new ReplayContext
        {
            Index = index,
            Target = target,
            Options = options,
            Report = new ReportBuilder(new DateTime(firstTicks, DateTimeKind.Utc), bucketSeconds),
            Results = results,
            RecordingStartTicks = firstTicks,
        };

        var started = DateTime.UtcNow;
        WriteRunJson(folder, index, target, endpoint, options, parsed, started, null, false);
        Log.Info("run.start", $"replaying {index.Stats.Replayable:N0} requests in {sessions.Count:N0} sessions to {target.Name} ({endpoint.Describe()}), pace {Describe(options)}");

        string? serverVersion = null;
        var check = await TargetCheck.RunAsync(endpoint, cancellationToken);
        if (!check.Ok)
        {
            Console.Error.WriteLine($"  The target could not be logged in to ({check.Failure}): {check.Problem}");
            return 1;
        }
        serverVersion = check.Summary;

        bool interrupted;
        using (var progress = new ProgressDisplay(context, index.Stats.Replayable, recordedSpan, options))
        {
            await Scheduler.RunAsync(context, cancellationToken);
            interrupted = cancellationToken.IsCancellationRequested;
        }

        var ended = DateTime.UtcNow;
        if (interrupted) Log.Warn("run.interrupted", "stopped before the end; the report covers what ran");

        var report = ReportWriter.Build(context, index, target, endpoint, serverVersion, check.Detail, options, parsed, started, ended, interrupted);
        report.Save(Path.Combine(folder, "report.json"));
        HtmlReport.Write(report, Path.Combine(folder, "report.html"));
        WriteRunJson(folder, index, target, endpoint, options, parsed, started, ended, interrupted);
        Log.Info("run.end", $"{context.Requests:N0} requests in {(ended - started).TotalSeconds:F1} s; report: {Path.Combine(folder, "report.json")}");

        ConsoleSummary.Print(report, Console.Out, folder);
        return interrupted ? 130 : 0;
    }

    private static string Describe(ReplayOptions options) => options.Pace switch
    {
        Pace.Recorded => $"recorded, {options.Speed:0.###}x",
        Pace.Fast => options.MaxSessions is int max ? $"fast, at most {max} sessions at a time" : "fast",
        _ => "serial",
    };

    private static void PrintBanner(RecordingIndex index, TargetRecord target, TargetEndpoint endpoint, ReplayOptions options,
                                    int sessions, TimeSpan recordedSpan, IReadOnlyList<string> databases)
    {
        var stats = index.Stats;
        Console.WriteLine();
        Console.WriteLine($"  recording   {string.Join(", ", index.Files.Paths.Select(Path.GetFileName))}: {Inspector.N(stats.Replayable)} requests " +
                          $"in {Inspector.Plural(sessions, "session")}, recorded {stats.FirstRequestUtc:yyyy-MM-dd HH:mm:ss} UTC over {Inspector.Duration(recordedSpan)}");
        if (stats.Requests - stats.Filtered - stats.Replayable is > 0 and var skipped)
            Console.WriteLine($"              {Inspector.N(skipped)} more cannot be replayed (daffyreplay inspect says why)");
        Console.WriteLine($"  target      {target.Name}: {endpoint.Describe()}" +
                          (target.Logins.Count > 0 ? $" ({Inspector.Plural(target.Logins.Count, "mapped login")})" : ""));
        Console.WriteLine($"  databases   {(endpoint.Database is { } forced ? $"{forced} (the target's), for every session" : $"as recorded: {string.Join(", ", databases)}")}");
        string duration = options.Pace == Pace.Recorded ? $" (about {Inspector.Duration(recordedSpan / options.Speed)})" : "";
        Console.WriteLine($"  pace        {Describe(options)}{duration}" +
                          (options.RequestTimeout is { } t ? $"; requests cancelled after {t.TotalSeconds:0.###} s" : ""));
        foreach (string warning in Inspector.Warnings(index)) Console.WriteLine($"  warning     {warning}");
        Console.WriteLine();
        Console.WriteLine($"  This sends every recorded write - inserts, updates, deletes - to {target.Name}. Restore its");
        Console.WriteLine("  databases to the moment recording began before each replay, or most of what follows is noise.");
        Console.WriteLine();
    }

    /// <summary>
    /// The recorded server the target is, if it is one of them: the same port, and the same host by
    /// name or by any address either resolves to.
    /// </summary>
    internal static async Task<string?> MatchingServerAsync(TargetEndpoint endpoint, IEnumerable<string> recorded, CancellationToken cancellationToken)
    {
        var targetAddresses = await ResolveAsync(endpoint.Host, cancellationToken);
        foreach (string server in recorded)
        {
            int colon = server.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(server[(colon + 1)..], out int port) || port != endpoint.Port) continue;

            string host = server[..colon].Trim('[', ']');
            if (string.Equals(host, endpoint.Host, StringComparison.OrdinalIgnoreCase)) return server;
            var addresses = await ResolveAsync(host, cancellationToken);
            if (addresses.Overlaps(targetAddresses)) return server;
        }
        return null;
    }

    private static async Task<HashSet<string>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, cancellationToken);
            return addresses.Select(a => IPAddress.IsLoopback(a) ? "loopback" : a.ToString()).ToHashSet();
        }
        catch (System.Net.Sockets.SocketException)
        {
            return [];
        }
    }

    private static void WriteRunJson(string folder, RecordingIndex index, TargetRecord target, TargetEndpoint endpoint, ReplayOptions options,
                                     Arguments parsed, DateTime started, DateTime? ended, bool interrupted)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("tool", $"daffyreplay {Program.ToolVersion()}");
            w.WriteStartArray("sources");
            foreach (string path in index.Files.Paths) w.WriteStringValue(path);
            w.WriteEndArray();
            w.WriteStartObject("filters");
            foreach (string name in Arguments.FilterOptions)
                if (parsed.Get(name) is { } value) w.WriteString(name.TrimStart('-'), value);
            foreach (string name in Arguments.FilterFlags)
                if (parsed.Has(name)) w.WriteBoolean(name.TrimStart('-'), true);
            w.WriteEndObject();
            w.WriteStartObject("target");
            w.WriteString("name", target.Name);
            w.WriteString("server", $"{endpoint.Host}:{endpoint.Port}");
            w.WriteString("login", endpoint.UserName);
            w.WriteString("security", endpoint.DescribeSecurity());
            w.WriteEndObject();
            w.WriteString("pace", options.Pace.ToString().ToLowerInvariant());
            w.WriteNumber("speed", options.Speed);
            if (options.MaxSessions is int max) w.WriteNumber("maxSessions", max);
            if (options.RequestTimeout is { } timeout) w.WriteNumber("requestTimeoutSeconds", timeout.TotalSeconds);
            w.WriteBoolean("keepAppName", options.KeepAppName);
            w.WriteNumber("packetSize", options.PacketSize);
            w.WriteString("daffyTeeVersion", index.Stats.DaffyTeeVersion);
            w.WriteString("started", started);
            if (ended is { } end) w.WriteString("ended", end);
            if (interrupted) w.WriteBoolean("interrupted", true);
            w.WriteEndObject();
        }
        File.WriteAllBytes(Path.Combine(folder, "run.json"), buffer.WrittenSpan.ToArray());
    }
}

/// <summary>
/// While a run is going: elapsed time against recorded time, sessions, requests and their rate,
/// new errors so far, and lag. One line rewritten in place at a terminal; a line every ten seconds
/// when output goes to a file. Log lines clear it first, so the two never tangle.
/// </summary>
internal sealed class ProgressDisplay : IDisposable
{
    private readonly ReplayContext _context;
    private readonly long _total;
    private readonly ReplayOptions _options;
    private readonly bool _terminal = !Console.IsOutputRedirected;
    private readonly Timer _timer;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly object _gate = new();
    private long _lastRequests;
    private int _lineLength;

    public ProgressDisplay(ReplayContext context, long total, TimeSpan recordedSpan, ReplayOptions options)
    {
        _context = context;
        _total = total;
        _options = options;
        Log.ConsoleWriter = WriteLogLine;
        var period = _terminal ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(10);
        _timer = new Timer(_ => Tick(), null, period, period);
    }

    private void Tick()
    {
        lock (_gate)
        {
            long requests = _context.Requests;
            double rate = (requests - _lastRequests) / (_terminal ? 1.0 : 10.0);
            _lastRequests = requests;

            string line = $"  {Inspector.Duration(_stopwatch.Elapsed),9} " +
                          (_options.Pace == Pace.Recorded ? $"at recorded +{Inspector.Duration(_context.RecordedPosition)}  " : "") +
                          $"sessions {_context.ActiveSessions,4}  requests {Inspector.N(requests)}/{Inspector.N(_total)} ({rate:N0}/s)  " +
                          $"new errors {Inspector.N(_context.NewErrors)}" +
                          (_options.Pace == Pace.Recorded ? $"  lag {_context.LastLagMs:F1} ms" : "");
            if (_terminal)
            {
                Console.Write("\r" + line.PadRight(_lineLength));
                _lineLength = line.Length;
            }
            else
            {
                Console.WriteLine(line);
            }
        }
    }

    private void WriteLogLine(string text)
    {
        lock (_gate)
        {
            if (_terminal && _lineLength > 0)
            {
                Console.Write("\r" + new string(' ', _lineLength) + "\r");
                _lineLength = 0;
            }
            Console.WriteLine(text);
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        lock (_gate)
        {
            if (_terminal && _lineLength > 0) Console.WriteLine();
            _lineLength = 0;
        }
        Log.ConsoleWriter = Console.WriteLine;
    }
}
