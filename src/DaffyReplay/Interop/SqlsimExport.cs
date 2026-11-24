using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using DaffyReplay.Recording;
using DaffyReplay.Replay;
using DaffyReplay.Tds;

namespace DaffyReplay.Interop;

/// <summary>What an export wrote, and what it could not.</summary>
internal sealed class ExportSummary
{
    /// <summary>Requests the recording holds, after any filters.</summary>
    public long Requests { get; set; }
    public long Written { get; set; }
    public List<(string Path, long Events, long Bytes)> Files { get; } = [];
    public SortedDictionary<string, long> NotWritten { get; } = new(StringComparer.Ordinal);
    public long Sessions { get; set; }
    public long Cancelled { get; set; }
    public long Mars { get; set; }
    public long Resets { get; set; }
    public HashSet<string> Logins { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Skip(string reason, long count = 1)
    {
        if (count > 0) NotWritten[reason] = NotWritten.GetValueOrDefault(reason) + count;
    }
}

/// <summary>
/// <c>daffyreplay export sqlsim</c>: a DaffyTee recording as sqlsim's replay XML (see
/// <see cref="Sqlsim"/>), so sqlsim and sqlsim Studio can replay it.
/// <para>
/// Every request is written as T-SQL in a sql_batch_completed event - the one form sqlsim 1.0.0
/// replays whatever driver the application used (its own procedure-call replay binds only the
/// ODBC style: a SqlClient or JDBC call's named values, or a table-valued parameter, fail there).
/// Batches go as recorded; procedure calls as <c>--approximate</c> renders them (<see cref="SqlText"/>:
/// exact declared types, table-valued and OUTPUT parameters as variables, prepared statements as
/// sp_executesql); transaction-manager requests as BEGIN, COMMIT, ROLLBACK and SAVE TRANSACTION,
/// which an Extended Events capture never has. Bulk loads, cursor calls and statements prepared
/// before the recording cannot be written as text, and are counted instead.
/// </para>
/// <para>
/// Events go in start order, which is the order a session ran them in, and each is stamped with when
/// it started - not when it completed, as Extended Events stamps it - to the 100 ns and later than the
/// one before it in its session: sqlsim's stress mode sorts by timestamp, and so runs them in the
/// order they began, as DaffyReplay's serial pace does. Each
/// recorded session is one session_id, as sqlsim's fast mode replays it: one connection each.
/// A file stops at <see cref="Sqlsim.MaxEvents"/> events or <see cref="Sqlsim.MaxBytes"/> bytes,
/// and the next one (replay-2.xml, …) carries on.
/// </para>
/// </summary>
internal static class SqlsimExport
{
    private static readonly Regex InsertBulk = new(@"^\s*insert\s+bulk\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>What the index's skip reasons mean for an export.</summary>
    private static readonly Dictionary<string, string> IndexReasons = new(StringComparer.Ordinal)
    {
        ["bulk_without_rows"] = Bulk,
        ["insert_bulk_without_rows"] = Bulk,
        ["bulk_without_insert_bulk"] = Bulk,
        ["redacted"] = "requests whose password DaffyTee masked",
        ["old_datetime"] = OldDatetime,
    };

    private const string Bulk = "bulk loads (INSERT BULK and its rows: sqlsim has no way to send rows)";
    private const string OldDatetime = "procedure calls holding datetime values DaffyTee 0.3.0 or earlier decoded wrong";

    public static ExportSummary Run(RecordingIndex index, string outPath, int maxEvents = Sqlsim.MaxEvents, long maxBytes = Sqlsim.MaxBytes,
                                    CancellationToken cancellationToken = default)
    {
        var summary = new ExportSummary { Requests = index.Stats.Requests - index.Stats.Filtered };
        foreach (var (reason, count) in index.Stats.Skipped)
            summary.Skip(IndexReasons.GetValueOrDefault(reason) ?? Inspector.SkipReasons.GetValueOrDefault(reason, reason), count);

        bool? oldDatetimes = OldDatetimes.Affects(index.Stats.DaffyTeeVersion);
        var database = new Dictionary<int, string?>();
        var stamped = new Dictionary<int, DateTime>();
        var prepared = new Dictionary<(int Session, int Handle), string>();

        string servers = string.Join(", ", index.Sessions.Values.Select(s => s.Target).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
        string recording = string.Join(", ", index.Files.Paths.Select(Path.GetFileName));
        using var files = new PartWriter(outPath, maxEvents, maxBytes,
            $"<replay source=\"{Attribute($"DaffyTee recording {recording}")}\" server=\"{Attribute(servers)}\" timestamps=\"start\" " +
            $"generated=\"{DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)}\">\n" +
            $"  <!-- Exported by daffyreplay {Program.ToolVersion()}: every request as T-SQL in a sql_batch_completed event, " +
            "in recorded order, stamped with when each started; session_id is the recorded session. -->",
            summary);

        foreach (var entry in index.Entries.InOrder())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = index.Sessions[entry.Session];
            if (entry.Kind == EntryKind.SessionStart)
            {
                database[entry.Session] = session.Database;
                summary.Sessions++;
                continue;
            }
            if (entry.Kind != EntryKind.Request) continue;

            using var document = JsonDocument.Parse(index.Files.ReadAt(entry.File, entry.Offset, entry.Length));
            var root = document.RootElement;
            var recorded = RecordedRequest.FromDocument(root);
            var data = root.GetProperty("data");
            var request = data.TryGetProperty("request", out var r) ? r : default;

            string? db = database.GetValueOrDefault(entry.Session);
            database[entry.Session] = recorded.Session.Database ?? db;   // DaffyTee gives the database after the request

            if (recorded.ResetConnection) summary.Resets++;
            string? reason = null;
            string? sql = recorded.Kind switch
            {
                "batch" when recorded.Sql is { } text && InsertBulk.IsMatch(text) => Skip(Bulk),
                "batch" => recorded.Sql ?? Skip("batches without their text"),
                "bulk" => Skip(Bulk),
                "txn" => Transaction(recorded.Transaction) ?? Skip($"transaction requests of another kind ({recorded.Transaction?.Op})"),
                "rpc" => Call(recorded, request, entry.Session),
                _ => Skip($"{recorded.Kind} requests"),
            };
            if (sql is null)
            {
                summary.Skip(reason!);
                continue;
            }
            if (!XmlText(sql))
            {
                summary.Skip("requests whose text holds characters XML cannot carry");
                continue;
            }

            DateTime stamp = recorded.StartUtc;
            if (stamped.TryGetValue(entry.Session, out var before) && stamp <= before) stamp = before.AddTicks(1);
            stamped[entry.Session] = stamp;

            string login = session.TargetLogin.Length > 0 ? session.TargetLogin : session.Login;
            if (login.Length > 0) summary.Logins.Add(login);
            if (recorded.Response.Status == "cancelled") summary.Cancelled++;
            if (recorded.MarsChannel is not null) summary.Mars++;

            if (files.Write(Event(recorded, sql, stamp, entry.Session, db, login, session)))
                summary.Written++;
            else
                summary.Skip($"requests too large for one sqlsim file ({maxBytes:N0} bytes)");

            string? Skip(string why)
            {
                reason = why;
                return null;
            }

            string? Call(RecordedRequest call, JsonElement json, int key)
            {
                if (OldDatetimes.InRequest(json, oldDatetimes)) return Skip(OldDatetime);
                string? text = SqlText.Render(call, h => prepared.GetValueOrDefault((key, h)));
                foreach (var (handle, declarations) in SqlText.Prepared(call)) prepared[(key, handle)] = declarations;
                if (text is not null)
                {
                    // sp_prepare and sp_unprepare render as comments: each execution carries its statement.
                    return text.Split('\n').All(line => line.Length == 0 || line.StartsWith("--", StringComparison.Ordinal))
                        ? Skip("sp_prepare and sp_unprepare calls (each sp_execute is written as an sp_executesql of its statement)")
                        : text;
                }
                var first = call.Calls.FirstOrDefault();
                return Skip(first is null || call.Calls.Any(c => c.Undecoded is not null) ? "procedure calls DaffyTee could not decode"
                    : call.Calls.Any(c => c.ProcId is >= TdsConstants.ProcId.Cursor and <= TdsConstants.ProcId.CursorClose) ? "server cursor calls (sp_cursor*)"
                    : call.Calls.Any(c => c.ProcId == TdsConstants.ProcId.Execute && c.Sql is null) ? "sp_execute calls of a statement prepared before the recording began"
                    : call.Calls.Any(c => c.ProcId == TdsConstants.ProcId.Execute) ? "sp_execute calls whose sp_prepare is not in the recording"
                    : "procedure calls that cannot be written as T-SQL");
            }
        }

        return summary;
    }

    /// <summary>The export's n-th file (from 1): FILE.xml, then FILE-2.xml and on.</summary>
    public static string PartPath(string path, int part) =>
        part == 1 ? path : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", $"{Path.GetFileNameWithoutExtension(path)}-{part}{Path.GetExtension(path)}");

    /// <summary>A transaction-manager request as the T-SQL that does the same; null for a distributed one.</summary>
    public static string? Transaction(RecordedTransaction? t)
    {
        if (t is null) return null;
        string isolation = t.Isolation is "read uncommitted" or "read committed" or "repeatable read" or "serializable" or "snapshot"
            ? $"SET TRANSACTION ISOLATION LEVEL {t.Isolation.ToUpperInvariant()}; "
            : "";
        string name = t.Name is { Length: > 0 } n ? " " + Bracket(n) : "";
        string again = t.BeginsNew ? $"; {isolation}BEGIN TRANSACTION" : "";
        return t.Op switch
        {
            "begin" => $"{isolation}BEGIN TRANSACTION{name}",
            "commit" => $"COMMIT TRANSACTION{again}",
            "rollback" when name.Length > 0 => $"ROLLBACK TRANSACTION{name}",
            "rollback" => $"ROLLBACK TRANSACTION{again}",
            "save" when name.Length > 0 => $"SAVE TRANSACTION{name}",
            _ => null,
        };
    }

    private static string Bracket(string name) => "[" + name.Replace("]", "]]") + "]";

    private static string Event(RecordedRequest r, string sql, DateTime stamp, int sessionId, string? database, string login, RecordedSession session)
    {
        var e = new StringBuilder(sql.Length + 640);
        e.Append("  <event name=\"sql_batch_completed\" package=\"sqlserver\" timestamp=\"")
         .Append(stamp.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture)).Append("\">");
        if (r.DurationMs is { } ms) Data(e, "duration", ((long)Math.Round(ms * 1000)).ToString(CultureInfo.InvariantCulture));
        ulong? rows = r.Response.RowCounts is { Count: > 0 } counts ? counts.Aggregate(0UL, (sum, n) => sum + n) : r.Response.RowCount;
        if (rows is { } total) Data(e, "row_count", total.ToString(CultureInfo.InvariantCulture));
        var (result, text) = r.Response.Status switch { "error" => ("1", "Error"), "cancelled" => ("2", "Abort"), _ => ("0", "OK") };
        e.Append("<data name=\"result\"><value>").Append(result).Append("</value><text><![CDATA[").Append(text).Append("]]></text></data>");
        e.Append("<data name=\"batch_text\"><value>").Append(Value(sql)).Append("</value></data>");
        Action(e, "username", login);
        Action(e, "client_app_name", session.App);
        Action(e, "client_hostname", session.Host);
        if (database is not null) Action(e, "database_name", database);
        e.Append("<action name=\"session_id\" package=\"sqlserver\"><value>").Append(sessionId.ToString(CultureInfo.InvariantCulture)).Append("</value></action>");
        e.Append("</event>\n");
        return e.ToString();
    }

    private static void Data(StringBuilder e, string name, string value) =>
        e.Append("<data name=\"").Append(name).Append("\"><value>").Append(value).Append("</value></data>");

    private static void Action(StringBuilder e, string name, string value)
    {
        if (value.Length == 0 || !XmlText(value)) return;
        e.Append("<action name=\"").Append(name).Append("\" package=\"sqlserver\"><value>").Append(Value(value)).Append("</value></action>");
    }

    /// <summary>
    /// Text in a CDATA section, as Extended Events writes it - except text a CDATA section cannot keep:
    /// "]]>" (and sqlsim reads only a value's first CDATA section), and a carriage return, which an XML
    /// parser turns into a line feed there (sqlsim Studio's does). That text is escaped, CR as &amp;#13;.
    /// </summary>
    private static string Value(string text) =>
        text.Contains("]]>", StringComparison.Ordinal) || text.Contains('\r')
            ? text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\r", "&#13;")
            : "<![CDATA[" + text + "]]>";

    private static string Attribute(string text) => SecurityElement.Escape(text) ?? "";

    /// <summary>Whether XML 1.0 can carry the text at all: most control characters it cannot, escaped or not.</summary>
    private static bool XmlText(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (XmlConvert.IsXmlChar(c)) continue;
            if (i + 1 < text.Length && XmlConvert.IsXmlSurrogatePair(text[i + 1], c)) { i++; continue; }
            return false;
        }
        return true;
    }

    /// <summary>The export's files: replay.xml, then replay-2.xml and on, each within sqlsim's limits.</summary>
    private sealed class PartWriter(string path, int maxEvents, long maxBytes, string header, ExportSummary summary) : IDisposable
    {
        private const string Declaration = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";
        private const string Footer = "</replay>\n";
        private static readonly UTF8Encoding Utf8 = new(false);

        private FileStream? _stream;
        private string _current = "";
        private long _bytes, _events;

        /// <summary>Writes one event, in a new file if this one is full; false if the event is too large for any file.</summary>
        public bool Write(string xml)
        {
            byte[] bytes = Utf8.GetBytes(xml);
            long overhead = Utf8.GetByteCount(Declaration) + Utf8.GetByteCount(header) + 1 + Utf8.GetByteCount(Footer);
            if (bytes.Length + overhead > maxBytes) return false;
            if (_stream is not null && (_events >= maxEvents || _bytes + bytes.Length + Footer.Length > maxBytes)) End();
            if (_stream is null) Begin();
            _stream!.Write(bytes);
            _bytes += bytes.Length;
            _events++;
            return true;
        }

        private void Begin()
        {
            _current = PartPath(path, summary.Files.Count + 1);
            _stream = new FileStream(_current, FileMode.Create, FileAccess.Write, FileShare.Read);
            byte[] start = Utf8.GetBytes(Declaration + header + "\n");
            _stream.Write(start);
            _bytes = start.Length;
            _events = 0;
        }

        private void End()
        {
            if (_stream is null) return;
            _stream.Write(Utf8.GetBytes(Footer));
            _bytes += Footer.Length;
            _stream.Dispose();
            _stream = null;
            summary.Files.Add((_current, _events, _bytes));
        }

        public void Dispose()
        {
            if (_stream is null && summary.Files.Count == 0) Begin();   // nothing to export still makes a valid, empty file
            End();
        }
    }
}
