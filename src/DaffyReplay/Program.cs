using System.Reflection;
using DaffyReplay.Cli;
using DaffyReplay.Recording;
using DaffyReplay.Security;

namespace DaffyReplay;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // varchar values arrive in their collation's code page (1252 and friends), which .NET only
        // knows once the code-page provider is registered.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        // --config-dir applies to every command, so it is taken out before anything else looks.
        var rest = new List<string>(args);
        int configIndex = rest.FindIndex(a => a == "--config-dir");
        if (configIndex >= 0 && configIndex + 1 < rest.Count)
        {
            AppPaths.Home = rest[configIndex + 1];
            rest.RemoveRange(configIndex, 2);
        }

        try
        {
            string first = rest.Count > 0 ? rest[0] : "";
            switch (first)
            {
                case "" or "--help" or "-h" or "help":
                    PrintUsage();
                    return first.Length == 0 ? 1 : 0;

                case "--version" or "-v" or "version":
                    Console.WriteLine($"daffyreplay {ToolVersion()}");
                    Console.WriteLine($"dotnet      {Environment.Version}");
                    return 0;

                case "inspect":
                    return Inspect(rest.Skip(1).ToList(), cts.Token);

                case "run" or "replay":
                    return await RunCommand.RunAsync(rest.Skip(1).ToList(), cts.Token);

                case "report":
                    return Report(rest.Skip(1).ToList());

                case "compare":
                    return CompareCommand.Run(rest.Skip(1).ToList(), Console.Out);

                case "export":
                    return SqlsimCommands.Export(rest.Skip(1).ToList(), Console.Out, cts.Token);

                case "import":
                    return SqlsimCommands.Import(rest.Skip(1).ToList(), Console.Out, cts.Token);

                case "target" or "targets":
                {
                    var store = TargetStore.Open(AppPaths.EnsureHome());
                    var commands = new TargetCommands(store, Console.Out, Prompter.ForConsole(Console.Out));
                    return await commands.RunAsync(rest.Skip(1).ToList(), cts.Token);
                }

                default:
                    Console.Error.WriteLine($"unknown command '{first}' - daffyreplay --help lists them");
                    return 1;
            }
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"fatal: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }

    private static int Inspect(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var parsed = Arguments.Parse(args, new HashSet<string>([.. Arguments.FilterOptions, "--top"]), new HashSet<string>([.. Arguments.FilterFlags, "--approximate"]));
        using var files = RecordingFiles.Open(parsed.Positional);
        using var index = RecordingIndex.Build(files, parsed.Filter(), parsed.Has("--approximate"), cancellationToken: cancellationToken);
        Inspector.Print(index, Console.Out, parsed.Int("--top", 1) ?? 10);
        return 0;
    }

    /// <summary>report.html again, from a run folder's report.json: after an upgrade, or for a report copied elsewhere.</summary>
    private static int Report(IReadOnlyList<string> args)
    {
        if (args.Count != 1) throw new ArgumentException("usage: daffyreplay report RUN_DIR (the folder a run wrote, or its report.json)");
        string json = Directory.Exists(args[0]) ? Path.Combine(args[0], "report.json") : args[0];
        if (!File.Exists(json)) throw new ArgumentException($"no report.json at {json}");
        var report = Results.Report.Load(json);
        string html = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(json))!, "report.html");
        Results.HtmlReport.Write(report, html);
        Console.WriteLine($"  wrote {html}");
        return 0;
    }

    public static string ToolVersion()
    {
        string version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        int plus = version.IndexOf('+');
        return plus < 0 ? version : version[..plus];
    }

    private static void PrintUsage()
    {
        Console.WriteLine($"""
            daffyreplay {ToolVersion()} - replays a DaffyTee recording against a test SQL Server

              daffyreplay inspect FILES... [filters] [--top N] [--approximate]
                  What is in a recording, how much of it can be replayed, and anything that would
                  make a replay of it mean less. FILES are DaffyTee's event files (NDJSON); wildcards
                  work: "D:\capture\events-202610*.ndjson".

            {RunCommand.Usage}
              daffyreplay report RUN_DIR
                  Write report.html again from a run's report.json.

            {CompareCommand.Usage}
            {SqlsimCommands.ExportUsage}
            {SqlsimCommands.ImportUsage}
              Filters (inspect, run and export):
            {Arguments.FilterUsage}
              daffyreplay target ...
                  The servers a replay may go to, and the logins it uses there.

            {TargetCommands.Usage}
              Common:
                --config-dir DIR    keep the target store and its key in DIR
                                    (default {AppPaths.Home})
                --version | --help
            """);
    }
}
