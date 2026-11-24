using System.Data;
using System.Data.SqlTypes;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DaffyReplay.Recording;
using DaffyReplay.Replay;
using DaffyReplay.Tds;
using Microsoft.Data.SqlClient;

namespace DaffyReplay.Tests;

/// <summary>Procedure calls recorded without raw bytes, rendered as T-SQL for an approximate replay.</summary>
public sealed class SqlTextTests
{
    private static IEnumerable<RecordedRequest> Recorded(Func<string, bool> pick) =>
        File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "workload.ndjson"))
            .Where(line => line.Contains("\"request.rpc\"") && pick(line))
            .Select(line => RecordedRequest.Parse(System.Text.Encoding.UTF8.GetBytes(line)));

    private static string Literal(string json, string type) => SqlText.Literal(JsonDocument.Parse(json).RootElement, type);

    [Fact]
    public void Values_become_literals_of_their_declared_type()
    {
        Assert.Equal("NULL", Literal("null", "int"));
        Assert.Equal("1234567890.0123456789", Literal("1234567890.0123456789", "decimal(38,10)"));
        Assert.Equal("1", Literal("true", "bit"));
        Assert.Equal("N'it''s'", Literal("\"it's\"", "nvarchar(50)"));
        Assert.Equal("'café'", Literal("\"café\"", "varchar(100)"));
        Assert.Equal("0xCAFE", Literal("\"yv4=\"", "varbinary(100)"));
        Assert.Equal("'2026-10-10'", Literal("\"2026-10-10\"", "date"));
        Assert.Equal("'2026-10-10T12:00:00.123'", Literal("\"2026-10-10T12:00:00.1233333\"", "datetime"));
        Assert.Equal("'2026-10-10T12:00:00'", Literal("\"2026-10-10T12:00:00\"", "smalldatetime"));
        Assert.Equal("99999999999999999999999999999999999999", Literal("\"99999999999999999999999999999999999999\"", "decimal(38,0)"));
        Assert.StartsWith("NULL /*", Literal("{\"ciphertext\":\"AA==\"}", "varbinary(8000)"));
    }

    [Fact]
    public void A_procedure_with_a_table_valued_and_an_output_parameter_declares_them()
    {
        var order = Recorded(l => l.Contains("dbo.PlaceOrder") && l.Contains("probe order")).First();
        string sql = SqlText.Render(order)!;

        Assert.Contains("DECLARE @tvp1 dbo.OrderLineList;", sql);
        Assert.Contains("INSERT @tvp1 VALUES", sql);
        Assert.Contains("(3, 2)", sql);
        Assert.Matches(@"DECLARE @out2 int = NULL;", sql);
        Assert.Contains("EXEC dbo.PlaceOrder @CustomerId = 9, @Lines = @tvp1, @Note = N'probe order', @OrderId = @out2 OUTPUT;", sql);
    }

    [Fact]
    public void A_prepared_statement_is_executed_as_sp_executesql_with_its_prepare_s_declarations()
    {
        var calls = Recorded(l => l.Contains("ReplayProbe.Odbc") && (l.Contains("sp_prepexec") || l.Contains("sp_execute\""))).ToList();
        const string Statement = "EXEC sp_executesql N'SELECT COUNT(*) FROM dbo.Orders WHERE CustomerId = @P1',\n    N'@P1 int',\n    ";
        Assert.StartsWith(Statement, SqlText.Render(calls[0]));

        // Without the declarations from the prepare, an sp_execute cannot be rendered...
        Assert.Null(SqlText.Render(calls[1]));
        // ...and with them it can.
        Assert.StartsWith(Statement, SqlText.Render(calls[1], handle => handle == 1 ? "@P1 int" : null));
    }

    [Fact]
    public void Datetimes_are_decoded_whatever_the_time_of_day()
    {
        // 2026-10-10 12:00:00 as DATETIMEN: days since 1900, then three-hundredths of a second since midnight.
        byte[] value = [0x08, 0xDF, 0xB4, 0x00, 0x00, 0x00, 0xC1, 0xC5, 0x00];
        var cursor = new TdsCursor(value);
        Assert.Equal(new DateTime(2026, 10, 10, 12, 0, 0), TypeInfoReader.ReadValue(ref cursor, new TypeInfo(0x6F, 8)));
    }

    /// <summary>As SqlClient and SQL Server show them (.000, .003, .007), and as DaffyTee 0.3.1 decodes them.</summary>
    [Theory]
    [InlineData(0, "00:00:00")]
    [InlineData(1, "00:00:00.003")]
    [InlineData(2, "00:00:00.007")]
    [InlineData(42_949, "00:02:23.163")]      // the last value DaffyTee 0.3.0 got right
    [InlineData(42_950, "00:02:23.167")]      // the first it got wrong
    [InlineData(12_960_000, "12:00:00")]
    [InlineData(25_919_999, "23:59:59.997")]
    public void Datetimes_are_rounded_to_the_millisecond(int units, string time)
    {
        var expected = new DateTime(2026, 10, 10) + TimeSpan.Parse(time, CultureInfo.InvariantCulture);
        Assert.Equal(expected, Datetime(units, nullable: true));
        Assert.Equal(expected, Datetime(units, nullable: false));
    }

    [Fact]
    public void A_decoded_datetime_renders_as_a_literal_SQL_Server_accepts()
    {
        // DaffyTee writes the decoded value as System.Text.Json does; a literal takes no more than three places.
        string json = JsonSerializer.Serialize(Datetime(14_296_537, nullable: true));
        Assert.Equal("\"2026-10-10T13:14:15.123\"", json);
        Assert.Equal("'2026-10-10T13:14:15.123'", Literal(json, "datetime"));
    }

    /// <summary>
    /// A datetime parameter at 13:14:15.123, decoded from the bytes the client sends and rendered for
    /// an approximate replay, reaches the server as the value the client sent. Tick-exact, it would be
    /// .1233333, which SQL Server refuses as a datetime literal (error 241).
    /// </summary>
    [SqlFact]
    public async Task A_rendered_datetime_reaches_the_server_as_the_value_the_client_sent()
    {
        var sent = new DateTime(2026, 10, 10, 13, 14, 15, 123);
        var wire = new SqlDateTime(sent);
        Assert.Equal(14_296_537, wire.TimeTicks);
        string value = JsonSerializer.Serialize(Datetime(wire.TimeTicks, nullable: true));

        string json = $$$"""
            {"type":"request.rpc","time":"2026-10-10T13:14:15Z","data":{"v":1,"seq":1,"session":{"id":1},"request":{"kind":"rpc","calls":[
                {"proc":"sp_executesql","procId":10,"sql":"SELECT @d","paramDecl":"@d datetime",
                 "params":[{"name":"@d","type":"datetime","dir":"in","value":{{{value}}}}]}]} } }
            """;
        string sql = SqlText.Render(RecordedRequest.Parse(Encoding.UTF8.GetBytes(json.ReplaceLineEndings(""))))!;
        Assert.Contains("@d = '2026-10-10T13:14:15.123'", sql);

        await using var connection = new SqlConnection(TestServer.SqlClient);
        await connection.OpenAsync();
        await using var direct = new SqlCommand("SELECT @d", connection);
        direct.Parameters.Add("@d", SqlDbType.DateTime).Value = sent;
        await using var rendered = new SqlCommand(sql, connection);
        Assert.Equal(await direct.ExecuteScalarAsync(), await rendered.ExecuteScalarAsync());
    }

    /// <summary>2026-10-10 (day 46,303 since 1900) and a time in 1/300-second units: DATETIMN with its length byte, or fixed DATETIME.</summary>
    private static object? Datetime(int units, bool nullable)
    {
        var bytes = new List<byte>();
        if (nullable) bytes.Add(8);
        bytes.AddRange(BitConverter.GetBytes(46_303));
        bytes.AddRange(BitConverter.GetBytes(units));
        var cursor = new TdsCursor(bytes.ToArray());
        return TypeInfoReader.ReadValue(ref cursor, new TypeInfo(nullable ? (byte)0x6F : (byte)0x3D, 8));
    }
}
