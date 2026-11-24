using System.Text;
using System.Text.Json;

namespace ReplayProbe;

/// <summary>
/// A recording made up rather than recorded, with gaps chosen in advance: for checking that the
/// recorded pace keeps time. Several sessions, each sending trivial batches at known offsets -
/// gaps from 1 ms to 40 ms, some sessions overlapping - in DaffyTee's own event schema, raw bytes
/// included, so DaffyReplay treats it exactly as it treats a real one.
/// </summary>
internal static class Synthetic
{
    public static int Run(string path, int requests, int sessions)
    {
        var random = new Random(42);
        var start = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false)) { NewLine = "\n" };

        writer.WriteLine(Event("proxy.start", start.AddMilliseconds(-500), "proxy", new Dictionary<string, object?>
        {
            ["v"] = 1, ["teeEnabled"] = true, ["settings"] = "on, sampling 100% of requests, bulk full, raw payloads, passwords masked",
            ["redactSecrets"] = true, ["sink"] = "file", ["version"] = "synthetic",
        }));

        var offsets = new List<(int Session, double Ms)>();
        for (int s = 1; s <= sessions; s++)
        {
            double at = random.Next(0, 200);
            for (int i = 0; i < requests / sessions; i++)
            {
                offsets.Add((s, at));
                at += 1 + random.NextDouble() * 39;
            }
        }

        var bySession = offsets.GroupBy(o => o.Session).ToDictionary(g => g.Key, g => g.Select(o => o.Ms).ToList());
        foreach (var (session, times) in bySession)
        {
            writer.WriteLine(Event("session.start", start.AddMilliseconds(times[0] - 20), $"s{session}", new Dictionary<string, object?>
            {
                ["v"] = 1, ["session"] = Session(session), ["serverVersion"] = "Microsoft SQL Server 16.0.4295", ["mars"] = false,
            }));
        }

        var seq = new Dictionary<int, long>();
        foreach (var (session, ms) in offsets.OrderBy(o => o.Ms))
        {
            long n = seq[session] = seq.GetValueOrDefault(session) + 1;
            byte[] raw = Batch("SELECT 1;");
            writer.WriteLine(Event("request.batch", start.AddTicks((long)(ms * TimeSpan.TicksPerMillisecond)), $"s{session}", new Dictionary<string, object?>
            {
                ["v"] = 1, ["seq"] = n, ["durationMs"] = 0.4, ["session"] = Session(session),
                ["request"] = new Dictionary<string, object?>
                {
                    ["kind"] = "batch", ["txn"] = "0x0000000000000000", ["bytes"] = raw.Length, ["packets"] = 1,
                    ["sql"] = "SELECT 1;", ["raw"] = Convert.ToBase64String(raw),
                },
                ["response"] = new Dictionary<string, object?>
                {
                    ["status"] = "ok", ["rowCount"] = 1, ["done"] = "DONE", ["doneStatus"] = "0x0010", ["bytes"] = 30, ["packets"] = 1,
                    ["firstByteMs"] = 0.3, ["rowCounts"] = new[] { 1 }, ["resultSets"] = new[] { 1 },
                },
            }));
        }

        foreach (var (session, times) in bySession)
        {
            writer.WriteLine(Event("session.end", start.AddMilliseconds(times[^1] + 5), $"s{session}", new Dictionary<string, object?>
            {
                ["v"] = 1, ["session"] = Session(session), ["reason"] = "client disconnected",
            }));
        }

        Console.WriteLine($"  wrote {offsets.Count} requests in {sessions} sessions over {offsets.Max(o => o.Ms) / 1000:F1} s to {path}");
        return 0;
    }

    private static Dictionary<string, object?> Session(int id) => new()
    {
        ["id"] = id, ["connectionId"] = $"00000000-0000-7000-8000-{id:D12}", ["login"] = "app", ["targetLogin"] = "svc_app",
        ["target"] = "127.0.0.1:14361", ["spid"] = 50 + id, ["database"] = "ReplayTest", ["host"] = "DEV01", ["app"] = "Synthetic",
        ["client"] = "127.0.0.1:50000",
    };

    private static string Event(string type, DateTime time, string partition, Dictionary<string, object?> data) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["specversion"] = "1.0", ["id"] = Guid.CreateVersion7().ToString(), ["source"] = "daffytee://SYNTHETIC:1",
            ["type"] = type, ["time"] = time.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"), ["datacontenttype"] = "application/json",
            ["partitionkey"] = partition, ["data"] = data,
        });

    /// <summary>SQL_BATCH: ALL_HEADERS (one transaction descriptor, zero), then the text.</summary>
    private static byte[] Batch(string sql)
    {
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes(22));
        bytes.AddRange(BitConverter.GetBytes(18));
        bytes.AddRange(BitConverter.GetBytes((short)2));
        bytes.AddRange(new byte[8]);
        bytes.AddRange(BitConverter.GetBytes(1));
        bytes.AddRange(Encoding.Unicode.GetBytes(sql));
        return [.. bytes];
    }
}
