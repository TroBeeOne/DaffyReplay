using System.Text.Json;
using DaffyReplay.Replay;
using DaffyReplay.Target;
using DaffyReplay.Tds;

namespace DaffyReplay.Tests;

/// <summary>
/// Bulk loads, from the committed recording. SqlClient negotiates column encryption at login, so
/// the column metadata of its bulk loads carries a CEK table - two zero bytes, when no key is in
/// use - and the server reads a bulk load by what its session negotiated. Replayed without the same
/// negotiation, the server takes those two bytes for the first column and refuses it (4816).
/// </summary>
public sealed class BulkLoadTests
{
    internal static (string InsertBulk, byte[] Rows) RecordedBulkLoad()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "workload.ndjson");
        string? insert = null;
        foreach (string line in File.ReadLines(path))
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            string type = root.GetProperty("type").GetString()!;
            var request = type.StartsWith("request.") ? root.GetProperty("data").GetProperty("request") : default;
            if (type == "request.batch" && request.GetProperty("sql").GetString()!.StartsWith("insert bulk"))
                insert = request.GetProperty("sql").GetString();
            if (type == "request.bulk")
                return (insert!, request.GetProperty("raw").GetBytesFromBase64());
        }
        throw new InvalidDataException("the fixture has no bulk load");
    }

    [Fact]
    public void A_SqlClient_bulk_load_is_recognised_as_negotiating_column_encryption()
    {
        var (_, rows) = RecordedBulkLoad();
        Assert.True(ResponseWalker.HasCekTable(rows));

        // The same load from a client that did not negotiate it: the two-byte CEK table taken out.
        byte[] plain = [.. rows.AsSpan(0, 3), .. rows.AsSpan(5)];
        Assert.False(ResponseWalker.HasCekTable(plain));

        Assert.Null(ResponseWalker.HasCekTable([0xFD, 0, 0]));
    }

    [SqlFact]
    public async Task A_recorded_bulk_load_replays_with_the_login_its_client_used()
    {
        var (insert, rows) = RecordedBulkLoad();

        async Task<Execution> LoadAsync(bool columnEncryption)
        {
            await using var connection = await ReplayConnection.OpenAsync(TestServer.Endpoint,
                new LoginOptions(Database: "ReplayTest", ColumnEncryption: columnEncryption), CancellationToken.None);
            Assert.Equal(columnEncryption, connection.ColumnEncryption);

            var announce = await connection.ExecuteAsync(new RequestMessage(TdsConstants.PacketType.SqlBatch, RequestBuilder.Batch(insert)), null, CancellationToken.None);
            Assert.Equal("ok", announce.Status);
            return await connection.ExecuteAsync(new RequestMessage(TdsConstants.PacketType.BulkLoad, (byte[])rows.Clone()), null, CancellationToken.None);
        }

        var matched = await LoadAsync(columnEncryption: true);
        Assert.Equal("ok", matched.Status);
        Assert.Equal(1000UL, matched.Response.RowCount);

        // The mutation check: the same bytes under a login that did not negotiate it are refused.
        var mismatched = await LoadAsync(columnEncryption: false);
        Assert.Equal("error", mismatched.Status);
        Assert.Equal(4816, mismatched.Response.Errors[0].Number);
    }

    /// <summary>
    /// Why the index replays an INSERT BULK only with its rows: sent alone, it takes the session's
    /// next request down with it.
    /// </summary>
    [SqlFact]
    public async Task An_INSERT_BULK_without_its_rows_fails_the_request_after_it()
    {
        var (insert, _) = RecordedBulkLoad();
        await using var connection = await ReplayConnection.OpenAsync(TestServer.Endpoint, new LoginOptions(Database: "ReplayTest"), CancellationToken.None);

        var announce = await connection.ExecuteAsync(new RequestMessage(TdsConstants.PacketType.SqlBatch, RequestBuilder.Batch(insert)), null, CancellationToken.None);
        Assert.Equal("ok", announce.Status);
        var next = await connection.ExecuteAsync(new RequestMessage(TdsConstants.PacketType.SqlBatch, RequestBuilder.Batch("SELECT 1")), null, CancellationToken.None);
        Assert.Equal("error", next.Status);
        Assert.Equal(4022, next.Response.Errors[0].Number);
    }

    [SqlFact]
    public async Task Results_are_walked_with_column_encryption_negotiated()
    {
        await using var connection = await ReplayConnection.OpenAsync(TestServer.Endpoint, new LoginOptions(ColumnEncryption: true), CancellationToken.None);
        Assert.True(connection.ColumnEncryption);
        var result = await connection.ExecuteAsync(new RequestMessage(TdsConstants.PacketType.SqlBatch,
            RequestBuilder.Batch("SELECT TOP (150) * FROM dbo.Customers ORDER BY Id; SELECT N FROM dbo.Numbers(20000);")), null, CancellationToken.None);
        Assert.Equal("ok", result.Status);
        Assert.Null(result.Response.Problem);
        Assert.Equal([150L, 20000L], result.Response.ResultSets);
    }
}
