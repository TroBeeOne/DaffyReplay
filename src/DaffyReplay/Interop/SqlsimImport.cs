using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DaffyReplay.Interop;

/// <summary>What an import read, wrote and left out.</summary>
internal sealed class ImportSummary
{
    public long Events { get; set; }
    public long Requests { get; set; }
    public long Connections { get; set; }
    public long Resets { get; set; }
    public long HandlesCleared { get; set; }
    public long DecimalsWidened { get; set; }
    public SortedDictionary<string, long> Skipped { get; } = new(StringComparer.Ordinal);
    public DateTime? First { get; set; }
    public DateTime? Last { get; set; }

    public void Skip(string reason) => Skipped[reason] = Skipped.GetValueOrDefault(reason) + 1;
}

/// <summary>
/// <c>daffyreplay import sqlsim</c>: sqlsim's replay XML (see <see cref="Sqlsim"/>) as a recording
/// DaffyReplay runs like one of DaffyTee's - streamed, so a file of any size is never held whole.
/// <para>
/// Each sql_batch_completed and rpc_completed becomes a batch: Extended Events writes a procedure
/// call's statement as T-SQL that makes the same call (exec sp_executesql N'..', N'@id int', @id=5;
/// a table-valued parameter declared and filled; OUTPUT values in variables), and DaffyReplay builds
/// a batch from its text exactly. A login with is_cached=false opens a connection; is_cached=true,
/// or an sp_reset_connection, is a pooled connection reused, so the next request carries the reset,
/// as DaffyTee records it. With no login events, each session_id is one connection.
/// </para>
/// <para>
/// An event's timestamp is when it completed; it started its duration earlier (a file daffyreplay
/// export wrote says timestamps="start": its are when each started). Timestamps come to the
/// millisecond and durations to the microsecond, so on one connection a start is kept after the one
/// before it - the order the connection ran them in.
/// </para>
/// Every response is marked imported (<c>importedFrom</c>): the report then compares a batch's row
/// total, which is all XEvents' row_count says, and says what an Extended Events capture leaves out.
/// </summary>
internal static class SqlsimImport
{
    private static readonly JsonWriterOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private sealed class Connection
    {
        public required int Id { get; init; }
        public required string ConnectionId { get; init; }
        public required int Spid { get; init; }
        public required string Login { get; init; }
        public required string App { get; init; }
        public required string Host { get; init; }
        public required string? Database { get; init; }
        public required DateTime Opened { get; init; }
        public long Seq { get; set; }
        public bool Reset { get; set; }
        public DateTime LastStart { get; set; } = DateTime.MinValue;
        public DateTime LastEnd { get; set; }
        public DateTime? LoggedOut { get; set; }
    }

    /// <param name="database">For sessions whose events do not say their database (no database_name action).</param>
    public static ImportSummary Run(string xmlPath, Stream output, string? database, CancellationToken cancellationToken = default)
    {
        var summary = new ImportSummary();
        string file = Path.GetFileName(xmlPath);
        var json = new Utf8JsonWriter(output, JsonOptions);
        var open = new Dictionary<string, Connection>(StringComparer.Ordinal);
        int connections = 0;
        bool started = false;

        void Write(string type, DateTime time, string partition, Action<Utf8JsonWriter> data)
        {
            json.WriteStartObject();
            json.WriteString("specversion", "1.0");
            json.WriteString("id", Guid.CreateVersion7(time).ToString());
            json.WriteString("source", $"{Sqlsim.Name}://{file}");
            json.WriteString("type", type);
            json.WriteString("time", Iso(time));
            json.WriteString("datacontenttype", "application/json");
            json.WriteString("partitionkey", partition);
            json.WriteStartObject("data");
            json.WriteNumber("v", 1);
            data(json);
            json.WriteEndObject();
            json.WriteEndObject();
            json.Flush();
            output.WriteByte((byte)'\n');
            json.Reset();
        }

        void Session(Utf8JsonWriter w, Connection c, string? db)
        {
            w.WriteStartObject("session");
            w.WriteNumber("id", c.Id);
            w.WriteString("connectionId", c.ConnectionId);
            w.WriteString("login", "");
            w.WriteString("targetLogin", c.Login);
            w.WriteNumber("spid", c.Spid);
            if (db is not null) w.WriteString("database", db);
            w.WriteString("host", c.Host);
            w.WriteString("app", c.App);
            w.WriteEndObject();
        }

        Connection Open(string sid, DateTime time, string? db, IReadOnlyDictionary<string, string> actions)
        {
            if (open.Remove(sid, out var previous)) Close(previous);
            var c = new Connection
            {
                Id = ++connections,
                ConnectionId = Guid.CreateVersion7(time).ToString(),
                Spid = int.TryParse(sid, NumberStyles.Integer, CultureInfo.InvariantCulture, out int spid) ? spid : 0,
                Login = First(actions, "username", "server_principal_name", "session_server_principal_name", "nt_username") ?? "",
                App = First(actions, "client_app_name") ?? "",
                Host = First(actions, "client_hostname") ?? "",
                Database = db ?? database,
                Opened = time,
                LastEnd = time,
            };
            open[sid] = c;
            summary.Connections++;
            Write("session.start", time, $"s{c.Id}", w => Session(w, c, c.Database));
            return c;
        }

        void Close(Connection c)
        {
            DateTime at = c.LoggedOut ?? c.LastEnd;
            Write("session.end", at, $"s{c.Id}", w =>
            {
                w.WriteNumber("durationMs", Math.Round((at - c.Opened).TotalMilliseconds, 3));
                Session(w, c, c.Database);
                w.WriteNumber("requests", c.Seq);
                w.WriteString("reason", c.LoggedOut is null ? "end of the capture" : "logout");
            });
        }

        // XmlTextReader with Normalization off keeps a statement's CR LF as written; XmlReader.Create's
        // reader would turn it into LF, changing string literals and the text the plan cache matches.
        using var reader = new XmlTextReader(xmlPath)
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            Normalization = false,
            WhitespaceHandling = WhitespaceHandling.All,
        };
        if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "replay")
            throw new InvalidDataException($"{file} is not sqlsim replay XML: its root is not <replay>");
        // daffyreplay export stamps each event with when it started (timestamps="start"); Extended Events, when it completed.
        bool startStamps = reader.GetAttribute("timestamps") == "start";
        int depth = reader.Depth;
        reader.Read();

        while (!reader.EOF)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != depth + 1 || reader.LocalName != "event")
            {
                reader.Read();
                continue;
            }

            var element = (XElement)XNode.ReadFrom(reader);
            summary.Events++;
            string name = (string?)element.Attribute("name") ?? "";
            if (!TryTime((string?)element.Attribute("timestamp"), out DateTime at))
            {
                summary.Skip("events without a timestamp");
                continue;
            }
            var data = Values(element, "data");
            var actions = Values(element, "action");
            string sid = actions.GetValueOrDefault("session_id") ?? "0";
            string? db = NonEmpty(actions.GetValueOrDefault("database_name")) ?? NonEmpty(data.GetValueOrDefault("database_name"));

            if (!started)
            {
                started = true;
                Write("proxy.start", at, "proxy", w =>
                {
                    w.WriteBoolean("teeEnabled", true);
                    w.WriteString("settings", $"imported from sqlsim replay XML {file}");
                    w.WriteString("importedFrom", Sqlsim.Name);
                    w.WriteString("importedFile", file);
                });
            }

            switch (name)
            {
                case "login":
                    if (data.GetValueOrDefault("is_cached") == "true" && open.TryGetValue(sid, out var reused))
                    {
                        reused.Reset = true;
                        reused.LoggedOut = null;
                    }
                    else
                    {
                        Open(sid, at, db, actions);
                    }
                    continue;

                case "logout":
                    if (open.TryGetValue(sid, out var leaving)) leaving.LoggedOut = at;
                    continue;

                case "sql_batch_completed" or "rpc_completed":
                    break;

                default:
                    summary.Skip($"{name} events (only batches, procedure calls, logins and logouts are read)");
                    continue;
            }

            string proc = name == "rpc_completed" ? data.GetValueOrDefault("object_name") ?? "" : "";
            if (proc.Equals("sp_reset_connection", StringComparison.OrdinalIgnoreCase))
            {
                if (open.TryGetValue(sid, out var reset)) reset.Reset = true;
                continue;
            }
            if (proc.StartsWith("sp_cursor", StringComparison.OrdinalIgnoreCase))
            {
                summary.Skip("server cursor calls (sp_cursor*), which need their cursor's handle from the server");
                continue;
            }

            string? sql = NonEmpty(name == "rpc_completed" ? data.GetValueOrDefault("statement") : data.GetValueOrDefault("batch_text"));
            if (sql is null)
            {
                summary.Skip("events without SQL text (the capture had collect_statement or collect_batch_text off)");
                continue;
            }
            if (InsertBulk.IsMatch(sql))
            {
                summary.Skip("bulk loads (INSERT BULK: Extended Events does not have their rows)");
                continue;
            }

            if (!open.TryGetValue(sid, out var c) || c.LoggedOut is not null) c = Open(sid, at, db, actions);
            long micros = long.TryParse(data.GetValueOrDefault("duration"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long d) ? d : 0;
            DateTime start = startStamps ? at : at.AddTicks(-micros * 10);
            if (start <= c.LastStart) start = c.LastStart.AddTicks(1);
            c.LastStart = start;
            c.LastEnd = at > c.LastEnd ? at : c.LastEnd;
            c.Seq++;
            bool resetHere = c.Reset;
            c.Reset = false;
            if (resetHere) summary.Resets++;

            string text = Fix(sql, summary);
            string status = data.GetValueOrDefault("result") switch { "1" => "error", "2" => "cancelled", _ => "ok" };
            ulong? rowTotal = name == "sql_batch_completed"
                              && ulong.TryParse(data.GetValueOrDefault("row_count"), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong rows)
                ? rows : null;

            Write("request.batch", start, $"s{c.Id}", w =>
            {
                w.WriteNumber("seq", c.Seq);
                w.WriteNumber("durationMs", micros / 1000.0);
                Session(w, c, db ?? c.Database);
                w.WriteStartObject("request");
                w.WriteString("kind", "batch");
                w.WriteString("sql", text);
                if (resetHere) w.WriteBoolean("resetConnection", true);
                w.WriteString("importedEvent", name);
                if (proc.Length > 0) w.WriteString("proc", proc);
                w.WriteEndObject();
                w.WriteStartObject("response");
                w.WriteString("status", status);
                w.WriteString("importedFrom", Sqlsim.Name);
                if (rowTotal is { } total) w.WriteNumber("rowTotal", total);
                w.WriteEndObject();
            });
            summary.Requests++;
            summary.First = summary.First is { } first && first < start ? first : start;
            summary.Last = summary.Last is { } last && last > start ? last : start;
        }

        foreach (var c in open.Values.OrderBy(c => c.Id)) Close(c);
        json.Flush();
        return summary;
    }

    // ---- text that does not run again as Extended Events wrote it ---------------------------------

    private static readonly Regex InsertBulk = new(@"^\s*insert\s+bulk\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex PrepareOutput = new(@"\bexec(?:ute)?\s+sp_(?:prepexec|prepare)\s+@(\w+)\s+output\b",
                                                      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Two things Extended Events writes that fail when run as written. The handle sp_prepexec or
    /// sp_prepare returned is set as its input (<c>set @p1=1</c>), and the server then looks that
    /// handle up: it is set to NULL, as the client sent it. A float comes with more digits than a
    /// numeric literal may have (3.141592653589793115997963468544185161590576): it becomes a float
    /// literal (…E0), the type it was.
    /// </summary>
    public static string Fix(string sql, ImportSummary? summary = null)
    {
        if (PrepareOutput.Match(sql) is { Success: true } prepare)
        {
            var assignment = new Regex(@"(?im)^([ \t]*set[ \t]+@" + Regex.Escape(prepare.Groups[1].Value) + @"[ \t]*=[ \t]*)-?\d+(?=[ \t]*\r?$)",
                                       RegexOptions.CultureInvariant);
            string cleared = assignment.Replace(sql, "${1}NULL", 1);
            if (cleared != sql)
            {
                sql = cleared;
                if (summary is not null) summary.HandlesCleared++;
            }
        }
        return WidenLongDecimals(sql, summary);
    }

    /// <summary>Decimal literals of more than 38 digits, outside strings, identifiers and comments, as float literals.</summary>
    private static string WidenLongDecimals(string sql, ImportSummary? summary)
    {
        StringBuilder? sb = null;
        int copied = 0, i = 0;
        while (i < sql.Length)
        {
            char ch = sql[i];
            char next = i + 1 < sql.Length ? sql[i + 1] : '\0';
            if (ch is '\'' or '"') { i = SkipQuoted(sql, i, ch); continue; }
            if (ch == '[') { i = SkipQuoted(sql, i, ']'); continue; }
            if (ch == '-' && next == '-') { i = sql.IndexOf('\n', i) is var nl && nl < 0 ? sql.Length : nl; continue; }
            if (ch == '/' && next == '*') { i = SkipBlockComment(sql, i); continue; }
            if (!char.IsAsciiDigit(ch) || i > 0 && (char.IsLetterOrDigit(sql[i - 1]) || sql[i - 1] is '_' or '@' or '#' or '$' or '.'))
            {
                i++;
                continue;
            }

            int digits = 0;
            while (i < sql.Length && char.IsAsciiDigit(sql[i])) { i++; digits++; }
            if (i >= sql.Length || sql[i] != '.') continue;
            i++;
            while (i < sql.Length && char.IsAsciiDigit(sql[i])) { i++; digits++; }
            if (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '.')) continue;   // 1.5e3, or not a number
            if (digits <= 38) continue;

            sb ??= new StringBuilder(sql.Length + 8);
            sb.Append(sql, copied, i - copied).Append("E0");
            copied = i;
            if (summary is not null) summary.DecimalsWidened++;
        }
        return sb is null ? sql : sb.Append(sql, copied, sql.Length - copied).ToString();
    }

    /// <summary>Past a quoted string or identifier; a doubled closing character is part of it.</summary>
    private static int SkipQuoted(string sql, int open, char close)
    {
        int i = open + 1;
        while (i < sql.Length)
        {
            if (sql[i] == close)
            {
                if (i + 1 < sql.Length && sql[i + 1] == close) { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return sql.Length;
    }

    /// <summary>Past a /* comment */, which T-SQL lets nest.</summary>
    private static int SkipBlockComment(string sql, int open)
    {
        int depth = 0, i = open;
        while (i < sql.Length - 1)
        {
            if (sql[i] == '/' && sql[i + 1] == '*') { depth++; i += 2; continue; }
            if (sql[i] == '*' && sql[i + 1] == '/') { depth--; i += 2; if (depth == 0) return i; continue; }
            i++;
        }
        return sql.Length;
    }

    // ---- reading events ---------------------------------------------------------------------------

    private static Dictionary<string, string> Values(XElement element, string kind)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var child in element.Elements(kind))
        {
            if ((string?)child.Attribute("name") is { } key && child.Element("value") is { } value) values[key] = value.Value;
        }
        return values;
    }

    private static string? First(IReadOnlyDictionary<string, string> values, params string[] names) =>
        names.Select(n => NonEmpty(values.GetValueOrDefault(n))).FirstOrDefault(v => v is not null);

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool TryTime(string? text, out DateTime time)
    {
        bool ok = DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out time);
        time = DateTime.SpecifyKind(time, DateTimeKind.Utc);
        return ok;
    }

    private static string Iso(DateTime time) => time.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
}
