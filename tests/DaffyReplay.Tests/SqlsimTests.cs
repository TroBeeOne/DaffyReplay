using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using DaffyReplay.Interop;
using DaffyReplay.Recording;
using DaffyReplay.Replay;
using DaffyReplay.Results;

namespace DaffyReplay.Tests;

/// <summary>To and from sqlsim's replay XML (daffyreplay export sqlsim, import sqlsim), with no network.</summary>
public sealed class SqlsimTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("daffyreplay-sqlsim-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "workload.ndjson");

    // ---- import ------------------------------------------------------------------------------------

    /// <summary>
    /// The shapes convert-xel.ps1 writes: a connection that logs in, is pooled and reused (a cached
    /// login, then sp_reset_connection); a session with no login event; and events import leaves out.
    /// </summary>
    private const string Capture = """
        <?xml version="1.0" encoding="utf-8"?>
        <replay source="C:\temp\capture*.xel" server="localhost" generated="2026-10-31T09:00:00">
          <event name="login" package="sqlserver" timestamp="2026-10-31T14:00:00.000Z"><data name="is_cached"><value>false</value></data><action name="database_name" package="sqlserver"><value><![CDATA[Shop]]></value></action><action name="session_id" package="sqlserver"><value>61</value></action><action name="username" package="sqlserver"><value><![CDATA[app_user]]></value></action></event>
          <event name="sql_batch_completed" package="sqlserver" timestamp="2026-10-31T14:00:00.010Z"><data name="duration"><value>4000</value></data><data name="row_count"><value>3</value></data><data name="result"><value>0</value><text><![CDATA[OK]]></text></data><data name="batch_text"><value><![CDATA[SELECT 1; SELECT 2 UNION ALL SELECT 3]]></value></data><action name="database_name" package="sqlserver"><value><![CDATA[Shop]]></value></action><action name="session_id" package="sqlserver"><value>61</value></action></event>
          <event name="rpc_completed" package="sqlserver" timestamp="2026-10-31T14:00:00.020Z"><data name="duration"><value>1000</value></data><data name="row_count"><value>1</value></data><data name="result"><value>0</value></data><data name="object_name"><value><![CDATA[sp_executesql]]></value></data><data name="statement"><value><![CDATA[exec sp_executesql N'SELECT @id',N'@id int',@id=5]]></value></data><action name="session_id" package="sqlserver"><value>61</value></action></event>
          <event name="rpc_completed" package="sqlserver" timestamp="2026-10-31T14:00:00.022Z"><data name="duration"><value>500</value></data><data name="result"><value>0</value></data><data name="object_name"><value><![CDATA[sp_prepexec]]></value></data><data name="statement"><value><![CDATA[declare @p1 int
        set @p1=1
        exec sp_prepexec @p1 output,N'@x float',N'SELECT @x',@x=3.141592653589793115997963468544185161590576
        select @p1]]></value></data><action name="session_id" package="sqlserver"><value>61</value></action></event>
          <event name="rpc_completed" package="sqlserver" timestamp="2026-10-31T14:00:00.024Z"><data name="object_name"><value>sp_cursoropen</value></data><data name="statement"><value>exec sp_cursoropen</value></data><action name="session_id" package="sqlserver"><value>61</value></action></event>
          <event name="sql_batch_completed" package="sqlserver" timestamp="2026-10-31T14:00:00.026Z"><data name="batch_text"><value>insert bulk dbo.T ([a] Int)</value></data><action name="session_id" package="sqlserver"><value>61</value></action></event>
          <event name="logout" package="sqlserver" timestamp="2026-10-31T14:00:00.030Z"><action name="session_id" package="sqlserver"><value>61</value></action></event>
          <event name="login" package="sqlserver" timestamp="2026-10-31T14:00:00.040Z"><data name="is_cached"><value>true</value></data><action name="session_id" package="sqlserver"><value>61</value></action></event>
          <event name="rpc_completed" package="sqlserver" timestamp="2026-10-31T14:00:00.041Z"><data name="object_name"><value>sp_reset_connection</value></data><data name="statement"><value>exec sp_reset_connection</value></data><action name="session_id" package="sqlserver"><value>61</value></action></event>
          <event name="sql_batch_completed" package="sqlserver" timestamp="2026-10-31T14:00:00.050Z"><data name="duration"><value>2000</value></data><data name="result"><value>1</value></data><data name="batch_text"><value><![CDATA[SELECT * FROM dbo.Missing]]></value></data><action name="session_id" package="sqlserver"><value>61</value></action></event>
          <event name="logout" package="sqlserver" timestamp="2026-10-31T14:00:00.060Z"><action name="session_id" package="sqlserver"><value>61</value></action></event>
          <event name="sql_batch_completed" package="sqlserver" timestamp="2026-10-31T14:00:00.070Z"><data name="duration"><value>100</value></data><data name="row_count"><value>0</value></data><data name="result"><value>0</value></data><data name="batch_text"><value>SELECT N&apos;x&apos; WHERE 1 &lt; 0</value></data><action name="session_id" package="sqlserver"><value>62</value></action></event>
          <event name="error_reported" package="sqlserver" timestamp="2026-10-31T14:00:00.080Z"><action name="session_id" package="sqlserver"><value>62</value></action></event>
        </replay>
        """;

    private (ImportSummary Summary, string Path, List<JsonElement> Events) Import(string xml, string? database = null)
    {
        string source = Path.Combine(_dir, $"capture-{Guid.NewGuid():N}.xml");
        File.WriteAllText(source, xml);
        string output = Path.ChangeExtension(source, ".ndjson");
        ImportSummary summary;
        using (var stream = File.Create(output)) summary = SqlsimImport.Run(source, stream, database);
        var events = File.ReadLines(output).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
        return (summary, output, events);
    }

    private static List<JsonElement> Requests(List<JsonElement> events) =>
        events.Where(e => e.GetProperty("type").GetString() == "request.batch").ToList();

    [Fact]
    public void A_sqlsim_capture_imports_as_batches_on_the_connections_it_ran_on()
    {
        var (summary, path, events) = Import(Capture, database: "Fallback");

        Assert.Equal(5, summary.Requests);
        Assert.Equal(2, summary.Connections);
        Assert.Equal(1, summary.Resets);
        Assert.Contains(summary.Skipped.Keys, k => k.StartsWith("server cursor calls"));
        Assert.Contains(summary.Skipped.Keys, k => k.StartsWith("bulk loads"));
        Assert.Contains(summary.Skipped.Keys, k => k.StartsWith("error_reported events"));

        var start = events[0];
        Assert.Equal("proxy.start", start.GetProperty("type").GetString());
        Assert.Equal("sqlsim", start.GetProperty("data").GetProperty("importedFrom").GetString());

        var requests = Requests(events);
        var first = requests[0].GetProperty("data");
        Assert.Equal("2026-10-31T14:00:00.0060000Z", requests[0].GetProperty("time").GetString());   // completed .010, took 4 ms
        Assert.Equal("app_user", first.GetProperty("session").GetProperty("targetLogin").GetString());
        Assert.Equal("Shop", first.GetProperty("session").GetProperty("database").GetString());
        Assert.Equal(3UL, first.GetProperty("response").GetProperty("rowTotal").GetUInt64());
        Assert.Equal("sqlsim", first.GetProperty("response").GetProperty("importedFrom").GetString());

        var call = requests[1].GetProperty("data");
        Assert.Equal("exec sp_executesql N'SELECT @id',N'@id int',@id=5", call.GetProperty("request").GetProperty("sql").GetString());
        Assert.False(call.GetProperty("response").TryGetProperty("rowTotal", out _));   // a procedure call's rows are not compared

        string prepared = requests[2].GetProperty("data").GetProperty("request").GetProperty("sql").GetString()!;
        Assert.Contains("set @p1=NULL", prepared);
        Assert.Contains("@x=3.141592653589793115997963468544185161590576E0", prepared);

        var afterReset = requests[3].GetProperty("data");
        Assert.True(afterReset.GetProperty("request").GetProperty("resetConnection").GetBoolean());
        Assert.Equal("error", afterReset.GetProperty("response").GetProperty("status").GetString());
        Assert.Equal(first.GetProperty("session").GetProperty("connectionId").GetString(), afterReset.GetProperty("session").GetProperty("connectionId").GetString());
        Assert.Equal(4, afterReset.GetProperty("seq").GetInt32());
        Assert.Equal(1, requests.Count(r => r.GetProperty("data").GetProperty("request").TryGetProperty("resetConnection", out _)));

        var other = requests[4].GetProperty("data");
        Assert.Equal("SELECT N'x' WHERE 1 < 0", other.GetProperty("request").GetProperty("sql").GetString());
        Assert.Equal("Fallback", other.GetProperty("session").GetProperty("database").GetString());
        Assert.NotEqual(first.GetProperty("session").GetProperty("connectionId").GetString(), other.GetProperty("session").GetProperty("connectionId").GetString());

        // DaffyReplay takes it like a recording, and says where it came from.
        using var index = RecordingIndex.Build(RecordingFiles.Open([path]));
        Assert.Equal("sqlsim", index.Stats.ImportedFrom);
        Assert.Equal(5, index.Stats.Exact);
        Assert.Equal(1, index.Stats.Resets);
        var warnings = Inspector.Warnings(index).ToList();
        Assert.StartsWith(Sqlsim.ImportedWarningStart, warnings[0]);
        Assert.DoesNotContain(warnings, w => w.Contains("proxy.start") || w.Contains("sampling") || w.Contains("raw bytes"));
    }

    [Fact]
    public void Starts_on_a_connection_keep_the_order_it_ran_them_in()
    {
        // Both completed in the same millisecond; the second took longer, so subtracting durations would put it first.
        string xml = """
            <replay>
              <event name="sql_batch_completed" timestamp="2026-10-31T14:00:00.100Z"><data name="duration"><value>50</value></data><data name="batch_text"><value>SELECT 1</value></data><action name="session_id"><value>51</value></action></event>
              <event name="sql_batch_completed" timestamp="2026-10-31T14:00:00.100Z"><data name="duration"><value>100</value></data><data name="batch_text"><value>SELECT 2</value></data><action name="session_id"><value>51</value></action></event>
            </replay>
            """;
        var requests = Requests(Import(xml).Events);
        var starts = requests.Select(r => DateTime.Parse(r.GetProperty("time").GetString()!).ToUniversalTime()).ToList();
        Assert.True(starts[1] > starts[0]);
        Assert.Equal([1, 2], requests.Select(r => r.GetProperty("data").GetProperty("seq").GetInt32()));
    }

    [Fact]
    public void A_file_that_is_not_replay_xml_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => Import("<events><event name=\"x\"/></events>"));
    }

    [Theory]
    [InlineData("declare @p1 int\r\nset @p1=7\r\nexec sp_prepexec @p1 output,N'@a int',N'SELECT @a',1\r\nselect @p1",
                "declare @p1 int\r\nset @p1=NULL\r\nexec sp_prepexec @p1 output,N'@a int',N'SELECT @a',1\r\nselect @p1")]
    [InlineData("declare @p1 int\nset @p1=3\nexec sp_prepare @p1 output,NULL,N'SELECT 1',1\nselect @p1",
                "declare @p1 int\nset @p1=NULL\nexec sp_prepare @p1 output,NULL,N'SELECT 1',1\nselect @p1")]
    [InlineData("declare @p2 int\nset @p2=7\nexec dbo.Proc @x=@p2 output\nselect @p2", "declare @p2 int\nset @p2=7\nexec dbo.Proc @x=@p2 output\nselect @p2")]
    [InlineData("SELECT 3.141592653589793115997963468544185161590576, 1.5, 2.5e3", "SELECT 3.141592653589793115997963468544185161590576E0, 1.5, 2.5e3")]
    [InlineData("SELECT '3.141592653589793115997963468544185161590576' -- 3.141592653589793115997963468544185161590576",
                "SELECT '3.141592653589793115997963468544185161590576' -- 3.141592653589793115997963468544185161590576")]
    [InlineData("/* 3.141592653589793115997963468544185161590576 /* nested */ */ SELECT [3.141592653589793115997963468544185161590576]",
                "/* 3.141592653589793115997963468544185161590576 /* nested */ */ SELECT [3.141592653589793115997963468544185161590576]")]
    public void Text_Extended_Events_writes_that_would_fail_is_fixed_and_nothing_else(string written, string replayed)
    {
        Assert.Equal(replayed, SqlsimImport.Fix(written));
    }

    // ---- the comparison an imported recording gets ------------------------------------------------

    private static (string, bool?) Compare(RecordedResponse recorded, params ulong[] replayCounts)
    {
        var replay = new Execution();
        replay.Response.RowCounts.AddRange(replayCounts);
        return Comparison.Compare(recorded, replay, recordedResponseKb: 64);
    }

    [Fact]
    public void An_imported_batch_is_compared_on_its_row_total_and_a_procedure_call_not_at_all()
    {
        var batch = new RecordedResponse { ImportedFrom = "sqlsim", RowTotal = 3 };
        Assert.False(Compare(batch, 1, 2).Item2);
        Assert.True(Compare(batch, 1, 1).Item2);
        Assert.Null(Compare(batch).Item2);                                          // SET NOCOUNT ON: nothing counted to add up
        Assert.Null(Compare(new RecordedResponse { ImportedFrom = "sqlsim" }, 1).Item2);

        // A DaffyTee recording is compared as it always was: per statement.
        Assert.True(Compare(new RecordedResponse { RowCounts = [3] }, 1, 2).Item2);
        Assert.False(Compare(new RecordedResponse { RowCounts = [1, 2] }, 1, 2).Item2);
    }

    // ---- export ------------------------------------------------------------------------------------

    private (ExportSummary Summary, List<XDocument> Files) Export(string recording, int maxEvents = Sqlsim.MaxEvents)
    {
        string output = Path.Combine(_dir, $"export-{Guid.NewGuid():N}.xml");
        using var index = RecordingIndex.Build(RecordingFiles.Open([recording]), allowApproximate: true);
        var summary = SqlsimExport.Run(index, output, maxEvents);
        return (summary, summary.Files.Select(f => XDocument.Load(f.Path)).ToList());
    }

    private static IEnumerable<XElement> Events(XDocument document) => document.Root!.Elements("event");

    private static string? Value(XElement e, string kind, string name) =>
        e.Elements(kind).FirstOrDefault(x => (string?)x.Attribute("name") == name)?.Element("value")?.Value;

    [Fact]
    public void A_recording_exports_as_sqlsim_replay_xml_in_session_order()
    {
        var (summary, files) = Export(Fixture);
        var document = Assert.Single(files);
        var events = Events(document).ToList();

        Assert.Equal("start", (string?)document.Root!.Attribute("timestamps"));
        Assert.Equal(summary.Written, events.Count);
        Assert.Equal(summary.Requests, summary.Written + summary.NotWritten.Values.Sum());
        Assert.True(summary.Written > summary.Requests * 0.95);
        Assert.Contains(summary.NotWritten.Keys, k => k.StartsWith("server cursor calls"));
        Assert.Contains(summary.NotWritten.Keys, k => k.StartsWith("bulk loads"));

        Assert.All(events, e => Assert.Equal("sql_batch_completed", (string?)e.Attribute("name")));
        Assert.All(events, e => Assert.False(string.IsNullOrWhiteSpace(Value(e, "data", "batch_text"))));
        var texts = events.Select(e => Value(e, "data", "batch_text")!).ToList();
        Assert.Contains(texts, t => t.StartsWith("EXEC sp_executesql N'"));
        Assert.Contains(texts, t => t.Contains("BEGIN TRANSACTION"));
        Assert.Contains(texts, t => t.StartsWith("COMMIT TRANSACTION"));
        Assert.DoesNotContain(texts, t => t.Contains("sp_cursor", StringComparison.OrdinalIgnoreCase) || t.StartsWith("insert bulk", StringComparison.OrdinalIgnoreCase));

        // sqlsim's stress mode sorts by timestamp: within a session they must be strictly in order.
        foreach (var session in events.GroupBy(e => Value(e, "action", "session_id")))
        {
            var stamps = session.Select(e => (string)e.Attribute("timestamp")!).ToList();
            for (int i = 1; i < stamps.Count; i++) Assert.True(string.CompareOrdinal(stamps[i - 1], stamps[i]) < 0);
        }
    }

    [Fact]
    public void An_export_goes_on_in_a_second_file_at_the_event_limit()
    {
        var (summary, files) = Export(Fixture, maxEvents: 500);
        Assert.Equal((int)Math.Ceiling(summary.Written / 500.0), files.Count);
        Assert.All(files, f => Assert.InRange(Events(f).Count(), 1, 500));
        Assert.Equal(summary.Written, files.Sum(f => Events(f).Count()));
        Assert.EndsWith("-2.xml", summary.Files[1].Path);
    }

    [Fact]
    public void An_exported_file_imports_back_to_the_same_statements_at_the_same_starts()
    {
        var (summary, _) = Export(Fixture);
        string ndjson = Path.Combine(_dir, "round-trip.ndjson");
        using (var stream = File.Create(ndjson)) SqlsimImport.Run(summary.Files[0].Path, stream, null);

        var exported = Events(XDocument.Load(summary.Files[0].Path))
            .Select(e => ((string)e.Attribute("timestamp")!, Value(e, "data", "batch_text")!)).ToList();
        var imported = File.ReadLines(ndjson).Select(l => JsonDocument.Parse(l).RootElement)
            .Where(e => e.GetProperty("type").GetString() == "request.batch")
            .Select(e => (e.GetProperty("time").GetString()!, e.GetProperty("data").GetProperty("request").GetProperty("sql").GetString()!)).ToList();
        Assert.Equal(exported.Order(), imported.Order());
    }

    private string Recording(params string[] batches)
    {
        string path = Path.Combine(_dir, $"recording-{Guid.NewGuid():N}.ndjson");
        var lines = new List<string>
        {
            """{"type":"session.start","time":"2026-10-31T14:00:00Z","data":{"v":1,"session":{"id":1,"connectionId":"c1","login":"app","targetLogin":"svc_app","database":"Shop"}}}""",
        };
        for (int i = 0; i < batches.Length; i++)
        {
            string sql = JsonSerializer.Serialize(batches[i]);
            lines.Add($$$"""{"type":"request.batch","time":"2026-10-31T14:00:0{{{i + 1}}}Z","data":{"v":1,"seq":{{{i + 1}}},"durationMs":1.5,"session":{"id":1,"connectionId":"c1","login":"app","targetLogin":"svc_app","database":"Shop"},"request":{"kind":"batch","sql":{{{sql}}}},"response":{"status":"ok","rowCounts":[1,2]} } }""");
        }
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void Text_a_CDATA_section_cannot_hold_is_escaped_and_text_XML_cannot_hold_is_left_out()
    {
        var (summary, files) = Export(Recording("SELECT N']]>' AS x", "SELECT '\u0001'", "SELECT 1"));
        var events = Events(Assert.Single(files)).ToList();

        Assert.Equal(2, summary.Written);
        Assert.Equal(1, summary.NotWritten["requests whose text holds characters XML cannot carry"]);
        Assert.Equal("SELECT N']]>' AS x", Value(events[0], "data", "batch_text"));
        Assert.DoesNotContain("<![CDATA[SELECT N']]>", File.ReadAllText(summary.Files[0].Path));   // sqlsim reads one CDATA section only
        Assert.Equal("3", Value(events[0], "data", "row_count"));
        Assert.Equal("1500", Value(events[0], "data", "duration"));
        Assert.Equal("svc_app", Value(events[0], "action", "username"));
        Assert.Equal("Shop", Value(events[0], "action", "database_name"));
    }

    [Fact]
    public void A_statement_s_line_ends_survive_both_ways()
    {
        const string sql = "SELECT 'a\r\nb'\r\nFROM t";

        // Extended Events writes them raw in a CDATA section, where an XML parser would make CR LF a LF.
        var (_, _, imported) = Import("<replay><event name=\"sql_batch_completed\" timestamp=\"2026-10-31T14:00:00.000Z\">" +
                                      $"<data name=\"batch_text\"><value><![CDATA[{sql}]]></value></data>" +
                                      "<action name=\"session_id\"><value>51</value></action></event></replay>");
        Assert.Equal(sql, Requests(imported)[0].GetProperty("data").GetProperty("request").GetProperty("sql").GetString());

        // An export escapes the CR, so any parser keeps it.
        var (summary, files) = Export(Recording(sql));
        Assert.Equal(sql, Value(Events(Assert.Single(files)).Single(), "data", "batch_text"));
        Assert.Contains("&#13;", File.ReadAllText(summary.Files[0].Path));
    }

    [Theory]
    [InlineData("begin", null, "serializable", false, "SET TRANSACTION ISOLATION LEVEL SERIALIZABLE; BEGIN TRANSACTION")]
    [InlineData("begin", null, null, false, "BEGIN TRANSACTION")]
    [InlineData("commit", null, null, false, "COMMIT TRANSACTION")]
    [InlineData("commit", null, "read committed", true, "COMMIT TRANSACTION; SET TRANSACTION ISOLATION LEVEL READ COMMITTED; BEGIN TRANSACTION")]
    [InlineData("rollback", null, null, false, "ROLLBACK TRANSACTION")]
    [InlineData("rollback", "point]1", null, false, "ROLLBACK TRANSACTION [point]]1]")]
    [InlineData("save", "point1", null, false, "SAVE TRANSACTION [point1]")]
    [InlineData("propagate", null, null, false, null)]
    public void Transaction_requests_export_as_the_T_SQL_that_does_the_same(string op, string? name, string? isolation, bool beginsNew, string? sql)
    {
        Assert.Equal(sql, SqlsimExport.Transaction(new RecordedTransaction(op, name, isolation, beginsNew)));
    }
}
