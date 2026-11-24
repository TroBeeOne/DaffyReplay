using System.Text.Json;
using System.Text.RegularExpressions;
using DaffyReplay.Results;

namespace DaffyReplay.Recording;

/// <summary>Which part of a recording to replay. Every filter that is set must match.</summary>
internal sealed record ReplayFilter(
    DateTime? From = null, DateTime? To = null, string? App = null, string? Login = null,
    string? Database = null, int? Session = null, Regex? Exclude = null, bool NoBulk = false)
{
    public bool Any => From is not null || To is not null || App is not null || Login is not null
                       || Database is not null || Session is not null || Exclude is not null || NoBulk;

    /// <summary>A whole session in or out, by who and what it was.</summary>
    public bool Includes(RecordedSession session) =>
        (App is null || Like(session.App, App))
        && (Login is null || Like(session.Login, Login) || Like(session.TargetLogin, Login))
        && (Session is null || session.DaffyTeeId == Session);

    /// <summary>One request in or out, by when it ran, where, what it said, and whether it is part of a bulk load.</summary>
    public bool Includes(DateTime start, string? database, string fingerprintText, string? sql, bool bulk = false) =>
        !(NoBulk && bulk)
        && (From is null || start >= From) && (To is null || start <= To)
        && (Database is null || Like(database ?? "", Database))
        && (Exclude is null || !(Exclude.IsMatch(fingerprintText) || sql is not null && Exclude.IsMatch(sql)));

    /// <summary>Equality without regard to case, with * standing for anything.</summary>
    public static bool Like(string value, string pattern) =>
        pattern.Contains('*')
            ? Regex.IsMatch(value, "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase)
            : string.Equals(value, pattern, StringComparison.OrdinalIgnoreCase);
}

/// <summary>One recorded session: a client connection to DaffyTee, and its connection to the server.</summary>
internal sealed class RecordedSession
{
    public required int Key { get; init; }
    public required string ConnectionId { get; init; }
    public int DaffyTeeId { get; set; }
    public string Login { get; set; } = "";
    public string TargetLogin { get; set; } = "";
    public string Target { get; set; } = "";
    public string? RoutedTo { get; set; }
    public string? Database { get; set; }
    public string Host { get; set; } = "";
    public string App { get; set; } = "";
    public string Client { get; set; } = "";
    public bool Mars { get; set; }
    public string? ServerVersion { get; set; }

    /// <summary>
    /// What the session's recorded bulk loads say about its login: true when their metadata carries
    /// a CEK table (the client negotiated column encryption), false when not, null with no bulk load.
    /// </summary>
    public bool? BulkColumnEncryption { get; set; }

    /// <summary>The session.start and session.end events' times, when the recording has them.</summary>
    public long? StartTicks { get; set; }
    public long? EndTicks { get; set; }
    public string? EndReason { get; set; }

    public long FirstRequestTicks { get; set; } = long.MaxValue;
    public long LastRequestTicks { get; set; } = long.MinValue;
    public long FirstSeq { get; set; } = long.MaxValue;
    public long LastSeq { get; set; } = long.MinValue;

    /// <summary>Request events seen for the session, replayable or not.</summary>
    public int Seen { get; set; }

    /// <summary>Requests that went into the index, to be replayed.</summary>
    public int Replayable { get; set; }

    /// <summary>
    /// The session's last INSERT BULK, until what follows it shows whether its rows will be replayed
    /// (see <see cref="RecordingIndex"/>). Its entry is null when the INSERT BULK was filtered out.
    /// </summary>
    internal BulkAnnouncement? PendingInsertBulk { get; set; }

    /// <summary>Bulk loads DaffyTee numbered but recorded no event for (--bulk off): not gaps.</summary>
    public int Unrecorded { get; set; }

    public bool Included { get; set; } = true;

    /// <summary>
    /// The session was already open when recording began (no session.start), or the tee was off for
    /// its first requests: whatever it set up then - SET options, temp tables, prepared statements -
    /// the replay does not have.
    /// </summary>
    public bool BeganBeforeRecording => StartTicks is null || FirstSeq > 1 && FirstSeq != long.MaxValue;

    /// <summary>Requests numbered but missing: sampled out, or recorded while the tee was off.</summary>
    public long MissingSeq => Seen == 0 ? 0 : Math.Max(0, LastSeq - FirstSeq + 1 - Seen - Unrecorded);

    public string Describe() => $"s{DaffyTeeId} {Login}->{TargetLogin} app={App}";
}

/// <summary>How a request will be replayed, or why it will not be.</summary>
internal enum Replayability { Exact, Approximate, Skipped }

/// <summary>An INSERT BULK waiting for its rows: its seq, and its index entry unless it was filtered out.</summary>
internal readonly record struct BulkAnnouncement(long Seq, IndexEntry? Entry);

internal sealed class FingerprintStats(string id, string text, string kind)
{
    public string Id { get; } = id;
    public string Text { get; } = text;
    public string Kind { get; } = kind;
    public long Count { get; set; }
    public double TotalMs { get; set; }
}

/// <summary>What the index pass learned about a recording: what <c>inspect</c> prints and the report repeats.</summary>
internal sealed class RecordingStats
{
    public long Lines { get; set; }
    public long BadLines { get; set; }
    public Dictionary<string, long> EventTypes { get; } = new(StringComparer.Ordinal);
    public HashSet<int> SchemaVersions { get; } = [];

    public DateTime? FirstRequestUtc { get; set; }
    public DateTime? LastRequestUtc { get; set; }

    public string? DaffyTeeVersion { get; set; }
    public string? Source { get; set; }
    public string? Settings { get; set; }

    /// <summary>A recording <c>daffyreplay import</c> wrote: what it was imported from ("sqlsim"), and the file.</summary>
    public string? ImportedFrom { get; set; }
    public string? ImportedFile { get; set; }
    public List<string> Controls { get; } = [];
    public long Dropped { get; set; }
    public int GapEvents { get; set; }
    public bool Stopped { get; set; }
    public int LoginFailures { get; set; }

    public long Requests { get; set; }
    public Dictionary<string, long> Kinds { get; } = new(StringComparer.Ordinal);
    public long Exact { get; set; }
    public long Approximate { get; set; }
    public Dictionary<string, long> Skipped { get; } = new(StringComparer.Ordinal);
    public long Filtered { get; set; }
    public Dictionary<string, long> RecordedStatus { get; } = new(StringComparer.Ordinal);
    public long Resets { get; set; }
    public long MarsRequests { get; set; }

    /// <summary>Bulk loads that left no event at all (DaffyTee --bulk off), seen as the gap after their INSERT BULK.</summary>
    public long BulkNotRecorded { get; set; }
    public long PreparedCalls { get; set; }
    public long CursorCalls { get; set; }

    /// <summary>Procedure calls without raw bytes holding datetime values a DaffyTee before 0.3.1 decoded wrong (see <see cref="OldDatetimes"/>).</summary>
    public long OldDatetimeCalls { get; set; }

    public Dictionary<string, FingerprintStats> Fingerprints { get; } = new(StringComparer.Ordinal);

    public long Replayable => Exact + Approximate;

    /// <summary>Whether the recording says requests carry raw bytes: proxy.start's settings, unless changed since.</summary>
    public bool RawOn => Settings?.Contains("raw payloads", StringComparison.Ordinal) == true;

    public void CountSkip(string reason) => Skipped[reason] = Skipped.GetValueOrDefault(reason) + 1;
}

/// <summary>
/// The index pass: one streaming read of every recording file. It builds the replay order - a
/// compact entry per session start, request and session end, sorted by recorded start time - plus
/// the table of sessions and everything <c>inspect</c> reports. Events are written when a request
/// completes but stamped with when it started, so file order is not start order; within a session
/// <c>seq</c> is the order, across sessions the start time is.
/// <para>
/// A bulk load is two requests: the INSERT BULK batch that announces it, then its rows. The server
/// takes whatever follows an INSERT BULK to be the rows and refuses anything else (4022), so an
/// INSERT BULK is held back until the session's next request shows whether its rows will be
/// replayed - recorded without them (DaffyTee's default, --bulk meta), recorded not at all (--bulk
/// off) or filtered out, the two are left out together.
/// </para>
/// </summary>
internal sealed class RecordingIndex : IDisposable
{
    public RecordingFiles Files { get; }
    public SortedIndex Entries { get; } = new();
    public RecordingStats Stats { get; } = new();
    public Dictionary<int, RecordedSession> Sessions { get; } = [];
    public ReplayFilter Filter { get; }

    /// <summary>Requests without raw bytes are replayed as rendered T-SQL, marked approximate, rather than skipped.</summary>
    public bool AllowApproximate { get; }

    private readonly Dictionary<string, int> _sessionKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the DaffyTee of the latest proxy.start decoded datetimes wrong; null before one is seen.</summary>
    private bool? _oldDatetimes;

    private RecordingIndex(RecordingFiles files, ReplayFilter filter, bool allowApproximate)
    {
        Files = files;
        Filter = filter;
        AllowApproximate = allowApproximate;
    }

    public static RecordingIndex Build(RecordingFiles files, ReplayFilter? filter = null, bool allowApproximate = false,
                                       Action<long>? progress = null, CancellationToken cancellationToken = default)
    {
        var index = new RecordingIndex(files, filter ?? new ReplayFilter(), allowApproximate);
        files.ReadLines(index.Add, progress, cancellationToken);
        index.Complete();
        return index;
    }

    private void Add(in RecordingLine line)
    {
        Stats.Lines++;
        JsonDocument document;
        try
        {
            var reader = new Utf8JsonReader(line.Bytes);
            document = JsonDocument.ParseValue(ref reader);
        }
        catch (JsonException)
        {
            Stats.BadLines++;
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || RecordedRequest.Text(root, "type") is not { } type)
            {
                Stats.BadLines++;
                return;
            }

            Stats.EventTypes[type] = Stats.EventTypes.GetValueOrDefault(type) + 1;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return;
            if (RecordedRequest.Long(data, "v") is { } version) Stats.SchemaVersions.Add((int)version);

            switch (type)
            {
                case "session.start":
                {
                    var session = SessionFor(root, data);
                    session.StartTicks = RecordedRequest.Time(root).Ticks;
                    session.Mars = RecordedRequest.Bool(data, "mars");
                    session.ServerVersion = RecordedRequest.Text(data, "serverVersion");
                    session.Database ??= RecordedRequest.Text(data.GetProperty("session"), "database");
                    break;
                }

                case "session.end":
                {
                    var session = SessionFor(root, data);
                    session.EndTicks = RecordedRequest.Time(root).Ticks;
                    session.EndReason = RecordedRequest.Text(data, "reason");
                    break;
                }

                case "session.login_failed":
                    Stats.LoginFailures++;
                    break;

                case "proxy.start":
                    Stats.ImportedFrom ??= RecordedRequest.Text(data, "importedFrom");
                    Stats.ImportedFile ??= RecordedRequest.Text(data, "importedFile");
                    Stats.DaffyTeeVersion ??= RecordedRequest.Text(data, "version");
                    _oldDatetimes = OldDatetimes.Affects(RecordedRequest.Text(data, "version"));
                    Stats.Source ??= RecordedRequest.Text(root, "source");
                    TakeSettings(data);
                    break;

                case "proxy.control":
                    Stats.Controls.Add($"{RecordedRequest.Time(root):yyyy-MM-dd HH:mm:ss} {RecordedRequest.Text(data, "change")} (by {RecordedRequest.Text(data, "by")})");
                    TakeSettings(data);
                    break;

                case "proxy.gap":
                    Stats.GapEvents++;
                    Stats.Dropped += RecordedRequest.Long(data, "dropped") ?? 0;
                    break;

                case "proxy.stop":
                    Stats.Stopped = true;
                    break;

                default:
                    if (type.StartsWith("request.", StringComparison.Ordinal)) AddRequest(line, root, data, type);
                    break;
            }
        }
    }

    /// <summary>
    /// The capture settings while the tee was on: a DaffyTee started with --tee off says only "off"
    /// at proxy.start, and what it recorded with comes in the proxy.control event that switched it on.
    /// </summary>
    private void TakeSettings(JsonElement data)
    {
        if (RecordedRequest.Text(data, "settings") is not { } settings) return;
        if (Stats.Settings is null || RecordedRequest.Bool(data, "teeEnabled")) Stats.Settings = settings;
    }

    private void AddRequest(in RecordingLine line, JsonElement root, JsonElement data, string type)
    {
        Stats.Requests++;
        var session = SessionFor(root, data);
        var start = RecordedRequest.Time(root);
        long seq = RecordedRequest.Long(data, "seq") ?? 0;

        session.Seen++;
        session.FirstSeq = Math.Min(session.FirstSeq, seq);
        session.LastSeq = Math.Max(session.LastSeq, seq);
        if (Stats.FirstRequestUtc is null || start < Stats.FirstRequestUtc) Stats.FirstRequestUtc = start;
        if (Stats.LastRequestUtc is null || start > Stats.LastRequestUtc) Stats.LastRequestUtc = start;

        var request = data.TryGetProperty("request", out var r) ? r : default;
        var response = data.TryGetProperty("response", out var p) ? p : default;
        string kind = RecordedRequest.Text(request, "kind") ?? type[8..];
        Stats.Kinds[kind] = Stats.Kinds.GetValueOrDefault(kind) + 1;

        string status = RecordedRequest.Text(response, "status") ?? "incomplete";
        Stats.RecordedStatus[status] = Stats.RecordedStatus.GetValueOrDefault(status) + 1;
        if (RecordedRequest.Bool(request, "resetConnection")) Stats.Resets++;
        if (RecordedRequest.Long(request, "marsChannel") is not null) Stats.MarsRequests++;

        string? sql = RecordedRequest.Text(request, "sql");
        var calls = RecordedRequest.Array(request, "calls")
            .Select(c => (Proc: RecordedRequest.Text(c, "proc") ?? "", Sql: RecordedRequest.Text(c, "sql"), Id: RecordedRequest.Long(c, "procId") ?? 0))
            .ToList();
        foreach (var call in calls)
        {
            if (call.Id is >= 11 and <= 15) Stats.PreparedCalls++;
            else if (call.Id is >= 1 and <= 9) Stats.CursorCalls++;
        }

        string? op = request.ValueKind == JsonValueKind.Object && request.TryGetProperty("transaction", out var t) ? RecordedRequest.Text(t, "op") : null;
        string? table = request.ValueKind == JsonValueKind.Object && request.TryGetProperty("bulk", out var b) ? RecordedRequest.Text(b, "table") : null;
        string? gateway = RecordedRequest.Text(request, "gatewayCommand");
        var (id, text) = Fingerprint.Of(kind, sql, calls.Select(c => (c.Proc, c.Sql)).ToList(), op, table, gateway);

        // Rows settle the INSERT BULK before them; so does anything else that follows it, except on
        // MARS, where another channel's request can come between the two.
        BulkAnnouncement? announced = null;
        if (kind == "bulk" || !session.Mars)
        {
            announced = session.PendingInsertBulk;
            session.PendingInsertBulk = null;
            if (announced is { } before && kind != "bulk" && seq > before.Seq + 1)
            {
                session.Unrecorded++;
                Stats.BulkNotRecorded++;
            }
        }

        bool insertBulk = kind == "batch" && sql is not null && InsertBulk.IsMatch(sql);
        if (!session.Included
            || !Filter.Includes(start, RecordedRequest.Text(data.GetProperty("session"), "database"), text, sql, bulk: insertBulk || kind == "bulk")
            || kind == "bulk" && announced is { Entry: null })
        {
            Stats.Filtered++;
            if (insertBulk) Announce(session, new BulkAnnouncement(seq, null));
            Settle(session, announced, rows: false, filtered: kind == "bulk");
            return;
        }

        var stats = Stats.Fingerprints.TryGetValue(id, out var existing) ? existing : Stats.Fingerprints[id] = new FingerprintStats(id, text, kind);
        stats.Count++;
        stats.TotalMs += RecordedRequest.Double(data, "durationMs") ?? 0;

        var (replayability, reason) = Classify(kind, request, op, gateway, AllowApproximate);
        // Without raw bytes a procedure call is rendered from its decoded values, and a DaffyTee
        // before 0.3.1 decoded most datetimes wrong: such a call is not replayed with them.
        if ((replayability == Replayability.Approximate || reason == "rpc_without_raw") && OldDatetimes.InRequest(request, _oldDatetimes))
        {
            Stats.OldDatetimeCalls++;
            if (replayability == Replayability.Approximate) (replayability, reason) = (Replayability.Skipped, "old_datetime");
        }
        if (kind == "bulk" && replayability != Replayability.Skipped && announced is null)
            (replayability, reason) = (Replayability.Skipped, "bulk_without_insert_bulk");
        if (kind == "bulk" && replayability == Replayability.Exact && session.BulkColumnEncryption is null
            && request.TryGetProperty("raw", out var bulkRaw)
            && Tds.ResponseWalker.HasCekTable(bulkRaw.GetBytesFromBase64()) is bool cekTable)
            session.BulkColumnEncryption = cekTable;
        if (replayability == Replayability.Skipped)
        {
            Stats.CountSkip(reason!);
            Settle(session, announced, rows: false, filtered: false);
            return;
        }

        Settle(session, announced, rows: kind == "bulk", filtered: false);
        var entry = new IndexEntry(start.Ticks, line.Offset, seq, session.Key, line.Bytes.Length, (short)line.File, EntryKind.Request);
        if (insertBulk) Announce(session, new BulkAnnouncement(seq, entry));
        else Accept(session, entry, replayability);
    }

    private static readonly Regex InsertBulk = new(@"^\s*insert\s+bulk\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private void Accept(RecordedSession session, in IndexEntry entry, Replayability how)
    {
        if (how == Replayability.Approximate) Stats.Approximate++;
        else Stats.Exact++;
        session.Replayable++;
        session.FirstRequestTicks = Math.Min(session.FirstRequestTicks, entry.Ticks);
        session.LastRequestTicks = Math.Max(session.LastRequestTicks, entry.Ticks);
        Entries.Add(entry);
    }

    private void Announce(RecordedSession session, BulkAnnouncement announcement)
    {
        // Only on MARS can an INSERT BULK still be waiting here: one whose rows never came.
        Settle(session, session.PendingInsertBulk, rows: false, filtered: false);
        session.PendingInsertBulk = announcement;
    }

    /// <summary>An INSERT BULK goes in with its rows, or is left out with them.</summary>
    private void Settle(RecordedSession session, BulkAnnouncement? announced, bool rows, bool filtered)
    {
        if (announced is not { Entry: { } entry }) return;
        if (rows) Accept(session, entry, Replayability.Exact);
        else if (filtered) Stats.Filtered++;
        else Stats.CountSkip("insert_bulk_without_rows");
    }

    /// <summary>
    /// Whether a request can be replayed, and how. Raw bytes replay exactly; without them a batch is
    /// rebuilt exactly from its text, a transaction-manager request from its few fields, and a
    /// procedure call only approximately, as T-SQL. Some requests are never replayed.
    /// </summary>
    public static (Replayability How, string? Reason) Classify(string kind, JsonElement request, string? transactionOp, string? gatewayCommand, bool allowApproximate)
    {
        if (gatewayCommand is not null) return (Replayability.Skipped, "gateway_command");
        if (RecordedRequest.Bool(request, "truncated")) return (Replayability.Skipped, "truncated");
        if (RecordedRequest.Bool(request, "redacted")) return (Replayability.Skipped, "redacted");

        bool raw = request.ValueKind == JsonValueKind.Object && request.TryGetProperty("raw", out _);
        switch (kind)
        {
            case "txn" when transactionOp is "propagate" or "promote" or "get_dtc_address":
                return (Replayability.Skipped, "distributed_transaction");
            case "batch" or "txn":
                return (Replayability.Exact, null);
            case "rpc" when raw:
                return (Replayability.Exact, null);
            case "rpc":
                return allowApproximate ? (Replayability.Approximate, null) : (Replayability.Skipped, "rpc_without_raw");
            case "bulk" when raw:
                return (Replayability.Exact, null);
            case "bulk":
                return (Replayability.Skipped, "bulk_without_rows");
            default:
                return (Replayability.Skipped, "unknown_kind");
        }
    }

    private RecordedSession SessionFor(JsonElement root, JsonElement data)
    {
        var s = data.TryGetProperty("session", out var element) ? element : default;
        string connectionId = RecordedRequest.Text(s, "connectionId")
                              ?? $"{RecordedRequest.Text(root, "source")}/{RecordedRequest.Text(root, "partitionkey")}";

        if (_sessionKeys.TryGetValue(connectionId, out int key)) return Sessions[key];

        key = Sessions.Count + 1;
        _sessionKeys[connectionId] = key;
        var session = new RecordedSession
        {
            Key = key,
            ConnectionId = connectionId,
            DaffyTeeId = (int)(RecordedRequest.Long(s, "id") ?? 0),
            Login = RecordedRequest.Text(s, "login") ?? "",
            TargetLogin = RecordedRequest.Text(s, "targetLogin") ?? "",
            Target = RecordedRequest.Text(s, "target") ?? "",
            RoutedTo = RecordedRequest.Text(s, "routedTo"),
            Database = RecordedRequest.Text(s, "database"),
            Host = RecordedRequest.Text(s, "host") ?? "",
            App = RecordedRequest.Text(s, "app") ?? "",
            Client = RecordedRequest.Text(s, "client") ?? "",
        };
        session.Included = Filter.Includes(session);
        Sessions[key] = session;
        return session;
    }

    /// <summary>
    /// Adds each session's opening and closing to the order: at its session.start and session.end
    /// when the recording has them, and never after its first request or before its last.
    /// </summary>
    private void Complete()
    {
        foreach (var session in Sessions.Values)
        {
            Settle(session, session.PendingInsertBulk, rows: false, filtered: false);
            session.PendingInsertBulk = null;
            if (session.Replayable == 0) continue;
            long open = Math.Min(session.StartTicks ?? long.MaxValue, session.FirstRequestTicks);
            long close = Math.Max(session.EndTicks ?? long.MinValue, session.LastRequestTicks);
            Entries.Add(new IndexEntry(open, 0, 0, session.Key, 0, 0, EntryKind.SessionStart));
            Entries.Add(new IndexEntry(close, 0, long.MaxValue, session.Key, 0, 0, EntryKind.SessionEnd));
        }
    }

    public RecordedRequest Load(in IndexEntry entry) =>
        RecordedRequest.Parse(Files.ReadAt(entry.File, entry.Offset, entry.Length));

    public void Dispose()
    {
        Entries.Dispose();
        Files.Dispose();
    }
}
