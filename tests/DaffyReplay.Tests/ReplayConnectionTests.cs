using DaffyReplay.Replay;
using DaffyReplay.Target;
using DaffyReplay.Tds;
using Microsoft.Data.SqlClient;

namespace DaffyReplay.Tests;

/// <summary>
/// DaffyReplay's own TDS client against a real SQL Server: responses of any size walked whole,
/// transactions followed, cancels acknowledged. Every check asserts something the server said.
/// </summary>
public sealed class ReplayConnectionTests
{
    private static Task<ReplayConnection> OpenAsync(string? database = null) =>
        ReplayConnection.OpenAsync(TestServer.Endpoint, new LoginOptions(AppName: "DaffyReplay.Tests", Database: database), CancellationToken.None);

    private static Task<Execution> BatchAsync(ReplayConnection connection, string sql, bool reset = false, TimeSpan? timeout = null) =>
        connection.ExecuteAsync(new RequestMessage(TdsConstants.PacketType.SqlBatch,
            RequestBuilder.Batch(sql, connection.TransactionDescriptor), reset), timeout, CancellationToken.None);

    [SqlFact]
    public async Task A_large_result_is_walked_whole_without_being_held()
    {
        await using var connection = await OpenAsync();
        var result = await BatchAsync(connection,
            "SELECT N, REPLICATE(N'x', N % 50) AS Padding, CAST(N AS decimal(18,4)) AS Amount FROM dbo.Numbers(300000)");

        Assert.Equal("ok", result.Status);
        Assert.Null(result.Response.Problem);
        Assert.Equal([300000L], result.Response.ResultSets);
        Assert.Equal([300000UL], result.Response.RowCounts);
        Assert.True(result.Response.Bytes > 5_000_000, $"only {result.Response.Bytes} bytes");
        Assert.True(result.Response.Packets > 600);
    }

    [SqlFact]
    public async Task Several_statements_report_every_count_and_result_set()
    {
        await using var connection = await OpenAsync();
        var result = await BatchAsync(connection,
            "SELECT * FROM dbo.Customers WHERE Id <= 3; SELECT 1 WHERE 1 = 0; UPDATE dbo.Products SET Stock = Stock WHERE Id <= 5; " +
            "EXEC dbo.SalesReport '2026-01-01', '2026-12-31';");

        Assert.Equal("ok", result.Status);
        Assert.Equal([3L, 0L, 4L, 10L], result.Response.ResultSets);
        Assert.Equal(3UL, result.Response.RowCounts[0]);
        Assert.Contains(5UL, result.Response.RowCounts);
    }

    [SqlFact]
    public async Task The_transaction_descriptor_follows_the_server()
    {
        await using var connection = await OpenAsync();
        Assert.Equal(0UL, connection.TransactionDescriptor);

        var begin = await BatchAsync(connection, "BEGIN TRANSACTION; SELECT @@TRANCOUNT;");
        Assert.Equal("ok", begin.Status);
        Assert.StartsWith("begin 0x", Assert.Single(begin.Response.Transactions));
        Assert.NotEqual(0UL, connection.TransactionDescriptor);

        var inside = await BatchAsync(connection, "INSERT dbo.AuditLog (What) VALUES (N'replay test'); SELECT @@TRANCOUNT;");
        Assert.Equal("ok", inside.Status);

        var rollback = await BatchAsync(connection, "ROLLBACK;");
        Assert.Equal("ok", rollback.Status);
        Assert.Equal(["rollback"], rollback.Response.Transactions);
        Assert.Equal(0UL, connection.TransactionDescriptor);
    }

    /// <summary>The mutation check: without the descriptor patched in, a request inside a transaction fails.</summary>
    [SqlFact]
    public async Task A_request_with_the_wrong_descriptor_is_refused()
    {
        await using var connection = await OpenAsync();
        await BatchAsync(connection, "BEGIN TRANSACTION;");
        Assert.NotEqual(0UL, connection.TransactionDescriptor);

        var stale = await connection.ExecuteAsync(new RequestMessage(TdsConstants.PacketType.SqlBatch,
            RequestBuilder.Batch("SELECT 1;", descriptor: 0x0000004200000001)), null, CancellationToken.None);

        Assert.Equal("error", stale.Status);
        Assert.NotEmpty(stale.Response.Errors);
    }

    [SqlFact]
    public async Task Every_type_comes_back_through_the_walker()
    {
        int id;
        await using (var setup = new SqlConnection(TestServer.SqlClient))
        {
            await setup.OpenAsync();
            await using var insert = new SqlCommand("""
                INSERT dbo.TypeZoo (cTiny, cSmall, cInt, cBig, cBit, cDec, cNum, cMoney, cSmallMoney, cReal, cFloat,
                    cDate, cTime, cDt, cSdt, cDt2, cDto, cChar, cVarchar, cNchar, cNvarchar, cVarcharMax, cNvarcharMax,
                    cBinary, cVarbinary, cVarbinaryMax, cGuid, cXml, cVariant, cText, cNtext, cImage)
                OUTPUT INSERTED.Id
                VALUES (1, 2, 3, 4, 1, 12345.6789012345, 123.456, 1.5, 2.25, 1.5, 2.5,
                    '2026-10-10', '12:34:56.1234567', '2026-10-10T12:00:00', '2026-10-10T12:00:00', '2026-10-10T12:00:00.1234567',
                    '2026-10-10T12:00:00.1234567+02:00', 'char', 'varchar', N'nchar', N'nvarchar',
                    REPLICATE(CAST('v' AS varchar(max)), 100000), REPLICATE(CAST(N'ü' AS nvarchar(max)), 70000),
                    0x0102030405060708, 0x0A0B, CAST(REPLICATE(CAST(0xFF AS varbinary(max)), 200000) AS varbinary(max)),
                    NEWID(), N'<a><b>1</b></a>', CAST(42 AS sql_variant),
                    REPLICATE(CAST('t' AS varchar(max)), 20000), N'ntext', 0x0102);
                """, setup);
            id = (int)(await insert.ExecuteScalarAsync())!;
        }

        await using var connection = await OpenAsync();
        var result = await BatchAsync(connection, $"SELECT * FROM dbo.TypeZoo WHERE Id = {id}; SELECT * FROM dbo.TypeZoo WHERE Id = {id} AND cInt IS NULL;");

        Assert.Equal("ok", result.Status);
        Assert.Null(result.Response.Problem);
        Assert.Equal([1L, 0L], result.Response.ResultSets);
        Assert.True(result.Response.Bytes > 400_000, $"only {result.Response.Bytes} bytes");

        var nulls = await BatchAsync(connection, "SELECT CAST(NULL AS nvarchar(max)), CAST(NULL AS text), CAST(NULL AS sql_variant), CAST(NULL AS xml), CAST(NULL AS int);");
        Assert.Equal([1L], nulls.Response.ResultSets);
        Assert.Null(nulls.Response.Problem);
    }

    [SqlFact]
    public async Task Errors_come_back_with_their_numbers_and_the_connection_carries_on()
    {
        await using var connection = await OpenAsync();
        var result = await BatchAsync(connection, "SELECT 1/0;");
        Assert.Equal("error", result.Status);
        Assert.Equal(8134, Assert.Single(result.Response.Errors).Number);

        var raised = await BatchAsync(connection, "EXEC dbo.FailWith 50000, N'from the test', 16, 3;");
        Assert.Equal("error", raised.Status);
        Assert.Equal(50000, raised.Response.Errors[0].Number);
        Assert.Equal(3, raised.Response.Errors[0].State);

        var after = await BatchAsync(connection, "SELECT 1;");
        Assert.Equal("ok", after.Status);
    }

    [SqlFact]
    public async Task A_timeout_sends_an_attention_and_waits_for_its_acknowledgement()
    {
        await using var connection = await OpenAsync();
        var started = DateTime.UtcNow;
        var result = await BatchAsync(connection, "WAITFOR DELAY '00:00:10'; SELECT 1;", timeout: TimeSpan.FromMilliseconds(300));

        Assert.Equal("timeout", result.Status);
        Assert.True(result.Response.AttentionAcknowledged);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));

        var after = await BatchAsync(connection, "SELECT 2;");
        Assert.Equal("ok", after.Status);
        Assert.Equal([1L], after.Response.ResultSets);
    }

    [SqlFact]
    public async Task A_reset_returns_the_session_to_its_login_state()
    {
        await using var connection = await OpenAsync();
        var use = await BatchAsync(connection, "USE ReplayOther;");
        Assert.Equal("ReplayOther", use.Response.Database);
        Assert.Equal("ReplayOther", connection.Database);

        var reset = await BatchAsync(connection, "SELECT DB_NAME();", reset: true);
        Assert.Equal("ok", reset.Status);
        Assert.True(reset.Response.ResetAcknowledged);
        Assert.Equal("ReplayTest", connection.Database);
    }

    [SqlFact]
    public async Task SqlTransaction_requests_are_rebuilt_and_answered()
    {
        await using var connection = await OpenAsync();
        async Task<Execution> Tm(string op, string? name = null, string? isolation = null) =>
            await connection.ExecuteAsync(new RequestMessage(TdsConstants.PacketType.TransactionManager,
                RequestBuilder.Transaction(op, name, isolation, false, connection.TransactionDescriptor)!), null, CancellationToken.None);

        var begin = await Tm("begin", isolation: "read committed");
        Assert.Equal("ok", begin.Status);
        Assert.NotEqual(0UL, connection.TransactionDescriptor);

        Assert.Equal("ok", (await BatchAsync(connection, "INSERT dbo.AuditLog (What) VALUES (N'tm test');")).Status);
        Assert.Equal("ok", (await Tm("save", "sp1")).Status);
        Assert.Equal("ok", (await BatchAsync(connection, "INSERT dbo.AuditLog (What) VALUES (N'tm test 2');")).Status);
        Assert.Equal("ok", (await Tm("rollback", "sp1")).Status);
        Assert.NotEqual(0UL, connection.TransactionDescriptor);

        var commit = await Tm("commit");
        Assert.Equal("ok", commit.Status);
        Assert.Equal(0UL, connection.TransactionDescriptor);
    }
}
