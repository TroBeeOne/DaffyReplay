using DaffyReplay.Interop;
using DaffyReplay.Recording;

namespace DaffyReplay.Cli;

/// <summary><c>daffyreplay export sqlsim</c> and <c>daffyreplay import sqlsim</c>: to and from sqlsim's replay XML.</summary>
internal static class SqlsimCommands
{
    public const string ExportUsage = """
          daffyreplay export sqlsim FILES... --out FILE.xml [filters] [--max-events N] [--force]
              A recording as sqlsim's replay XML (Microsoft's sqlsimtools), for sqlsim or sqlsim Studio
              to replay: every request as T-SQL, procedure calls as --approximate writes them,
              transactions as BEGIN/COMMIT/ROLLBACK TRANSACTION. Bulk loads and cursor calls cannot be
              written. A file holds 100,000 events and 100 MB at most (sqlsim's limits); past that the
              export goes on in FILE-2.xml, FILE-3.xml, ... (--max-events sets fewer per file).

        """;

    public const string ImportUsage = """
          daffyreplay import sqlsim FILE.xml --out FILE.ndjson [--default-database DB] [--force]
              sqlsim's replay XML (an Extended Events capture, as its convert-xel.ps1 writes it) as a
              recording that inspect and run take like DaffyTee's. Text only: what Extended Events
              cannot tell - transactions opened through the driver, errors raised while a statement
              ran, rows per statement - the report says. --default-database for sessions whose
              events do not name one.

        """;

    public static int Export(IReadOnlyList<string> args, TextWriter output, CancellationToken cancellationToken)
    {
        var parsed = Arguments.Parse(args, new HashSet<string>([.. Arguments.FilterOptions, "--out", "--max-events"]),
                                     new HashSet<string>([.. Arguments.FilterFlags, "--force"]));
        RequireFormat(parsed, "export");
        string outPath = parsed.Get("--out") ?? throw new ArgumentException("--out FILE.xml: where to write the sqlsim replay XML");
        int maxEvents = parsed.Int("--max-events", 1) ?? Sqlsim.MaxEvents;
        if (maxEvents > Sqlsim.MaxEvents) throw new ArgumentException($"--max-events: sqlsim takes {Sqlsim.MaxEvents:N0} events a file at most");

        // A file left from an earlier, longer export would look like part of this one.
        var existing = new[] { outPath }.Concat(Enumerable.Range(2, 999).Select(n => SqlsimExport.PartPath(outPath, n))).Where(File.Exists).ToList();
        if (existing.Count > 0 && !parsed.Has("--force"))
            throw new ArgumentException($"{existing[0]} already exists: choose another --out, or add --force to replace it");
        foreach (string old in existing) File.Delete(old);

        using var files = RecordingFiles.Open(parsed.Positional.Skip(1));
        using var index = RecordingIndex.Build(files, parsed.Filter(), allowApproximate: true, cancellationToken: cancellationToken);
        var summary = SqlsimExport.Run(index, outPath, maxEvents, cancellationToken: cancellationToken);

        output.WriteLine();
        output.WriteLine($"  exported    {Inspector.N(summary.Written)} of {Inspector.N(summary.Requests)} requests " +
                         $"({Inspector.Percent(summary.Written, summary.Requests)}), {Inspector.Plural(summary.Sessions, "session")}");
        foreach (var (path, events, bytes) in summary.Files)
            output.WriteLine($"              {path}: {Inspector.N(events)} events, {Inspector.Size(bytes)}");
        foreach (var (reason, count) in summary.NotWritten.OrderByDescending(n => n.Value))
            output.WriteLine($"  not written {Inspector.N(count),6}  {reason}");
        if (index.Stats.Filtered > 0) output.WriteLine($"  filtered    {Inspector.N(index.Stats.Filtered)} left out by the filters");

        output.WriteLine();
        output.WriteLine("  To replay it (sqlsim 1.0.0):");
        output.WriteLine($"    sqlsim -S SERVER -d DATABASE -E -replay fast -replayfile \"{summary.Files.FirstOrDefault().Path ?? outPath}\"");
        output.WriteLine("        a connection per recorded session, as fast as it goes");
        output.WriteLine("    ... -replay stress -n 1 -r 1   one connection, every request in recorded order");
        if (summary.Files.Count > 1)
            output.WriteLine($"    one file at a time: sqlsim takes one -replayfile, and sessions do not carry from one file to the next");
        output.WriteLine();
        output.WriteLine("  What sqlsim does differently from a DaffyReplay run:");
        if (summary.Logins.Count > 1)
            output.WriteLine($"    - every session logs in as the login sqlsim is given; the recording used {Inspector.N(summary.Logins.Count)} ({string.Join(", ", summary.Logins.Order().Take(5))})");
        if (summary.Resets > 0)
            output.WriteLine($"    - pooled connections are not reset between uses ({Inspector.N(summary.Resets)} resets recorded): temp tables and SET options carry over");
        if (summary.Mars > 0)
            output.WriteLine($"    - {Inspector.N(summary.Mars)} MARS request(s) run one at a time in their session");
        if (summary.Cancelled > 0)
            output.WriteLine($"    - {Inspector.N(summary.Cancelled)} request(s) cancelled when recorded run to completion (sqlsim's -t caps them)");
        output.WriteLine("    - no recorded pace: fast and stress modes only; procedure calls run as T-SQL with literal values");
        output.WriteLine();
        if (summary.Written == 0) output.WriteLine("  Nothing to replay: sqlsim needs at least one event.");
        return summary.Written > 0 ? 0 : 1;
    }

    public static int Import(IReadOnlyList<string> args, TextWriter output, CancellationToken cancellationToken)
    {
        var parsed = Arguments.Parse(args, new HashSet<string>(["--out", "--default-database"]), new HashSet<string>(["--force"]));
        RequireFormat(parsed, "import");
        if (parsed.Positional.Count != 2) throw new ArgumentException("usage: daffyreplay import sqlsim FILE.xml --out FILE.ndjson");
        string xml = parsed.Positional[1];
        if (!File.Exists(xml)) throw new ArgumentException($"no such file: {xml}");
        string outPath = parsed.Get("--out") ?? throw new ArgumentException("--out FILE.ndjson: where to write the recording");
        if (string.Equals(Path.GetFullPath(xml), Path.GetFullPath(outPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("--out is the XML being imported");
        if (File.Exists(outPath) && !parsed.Has("--force"))
            throw new ArgumentException($"{outPath} already exists: choose another --out, or add --force to replace it");

        ImportSummary summary;
        string partial = outPath + ".partial";
        try
        {
            using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                summary = SqlsimImport.Run(xml, stream, parsed.Get("--default-database"), cancellationToken);
            File.Move(partial, outPath, overwrite: true);
        }
        catch (System.Xml.XmlException ex)
        {
            File.Delete(partial);
            throw new ArgumentException($"{xml} is not well-formed XML: {ex.Message}");
        }
        catch
        {
            File.Delete(partial);
            throw;
        }

        output.WriteLine();
        string window = summary.First is { } first && summary.Last is { } last
            ? $" ({first:yyyy-MM-dd HH:mm:ss} to {last:HH:mm:ss} UTC, {Inspector.Duration(last - first)})" : "";
        output.WriteLine($"  imported    {Inspector.N(summary.Requests)} requests in {Inspector.Plural(summary.Connections, "connection")}{window} -> {outPath}");
        output.WriteLine($"              from {Inspector.N(summary.Events)} events; pooled-connection resets {Inspector.N(summary.Resets)}");
        foreach (var (reason, count) in summary.Skipped.OrderByDescending(s => s.Value))
            output.WriteLine($"  left out    {Inspector.N(count),6}  {reason}");
        if (summary.HandlesCleared > 0)
            output.WriteLine($"  rewritten   {Inspector.N(summary.HandlesCleared),6}  sp_prepexec/sp_prepare calls given their returned handle as input (set to NULL, as sent)");
        if (summary.DecimalsWidened > 0)
            output.WriteLine($"  rewritten   {Inspector.N(summary.DecimalsWidened),6}  float values written with more digits than a numeric literal takes (now float literals)");
        output.WriteLine();
        output.WriteLine($"  {Sqlsim.ImportedWarningStart}: an Extended Events capture knows less than a DaffyTee recording.");
        foreach (var (lead, detail) in Sqlsim.ImportLimits) output.WriteLine($"    - {lead}: {detail}.");
        output.WriteLine();
        output.WriteLine($"  Next: daffyreplay inspect \"{outPath}\", then daffyreplay run \"{outPath}\" --target NAME");
        output.WriteLine();
        return summary.Requests > 0 ? 0 : 1;
    }

    private static void RequireFormat(Arguments parsed, string command)
    {
        if (parsed.Positional.Count == 0 || !string.Equals(parsed.Positional[0], Sqlsim.Name, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"daffyreplay {command} sqlsim ...: sqlsim's replay XML is the format {command} knows" +
                                        (parsed.Positional.Count > 0 ? $", not '{parsed.Positional[0]}'" : ""));
    }
}
