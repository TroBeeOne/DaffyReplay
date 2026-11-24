using DaffyReplay.Recording;
using DaffyReplay.Replay;
using DaffyReplay.Target;
using DaffyReplay.Tds;

namespace DaffyReplay.Tests;

/// <summary>
/// Prepared-statement handles, from the ODBC driver's requests in the committed recording: an
/// sp_prepexec that was handed handle 1, then sp_execute 1. On a connection where the replay is
/// handed a different number, the sp_execute has to be rewritten to it.
/// </summary>
public sealed class HandleMappingTests
{
    private static List<RecordedRequest> OdbcPrepared()
    {
        var requests = new List<RecordedRequest>();
        foreach (string line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "workload.ndjson")))
        {
            if (!line.Contains("\"request.rpc\"") || !line.Contains("ReplayProbe.Odbc")) continue;
            var request = RecordedRequest.Parse(System.Text.Encoding.UTF8.GetBytes(line));
            if (request.Calls[0].Proc is "sp_prepexec" or "sp_execute") requests.Add(request);
        }
        return requests;
    }

    [Fact]
    public void The_recording_has_what_the_test_needs()
    {
        var requests = OdbcPrepared();
        Assert.Equal("sp_prepexec", requests[0].Calls[0].Proc);
        Assert.Equal(1, requests[0].Response.OutputInt("#0"));
        Assert.Equal("sp_execute", requests[1].Calls[0].Proc);
        Assert.Equal(1, requests[1].Calls[0].Handle);
    }

    [Fact]
    public void A_handle_named_by_a_later_call_is_rewritten_in_place()
    {
        var requests = OdbcPrepared();
        var handles = new HandleMap();
        handles.Learn(HandleKind.Prepared, recorded: 1, replay: 7);

        byte[] execute = (byte[])requests[1].Raw!.Clone();
        var outputs = RequestPatcher.PatchHandles(execute, handles).Outputs;

        Assert.Empty(outputs);
        Assert.Equal(1, handles.Rewritten);
        Assert.Equal(7, RequestParser.ParseRpc(execute)[0].Handle);

        // Everything else is as recorded.
        int changed = execute.Zip(requests[1].Raw!).Count(pair => pair.First != pair.Second);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void A_prepare_says_which_output_carries_its_handle()
    {
        var requests = OdbcPrepared();
        var outputs = RequestPatcher.PatchHandles((byte[])requests[0].Raw!.Clone(), new HandleMap()).Outputs;
        var output = Assert.Single(outputs);
        Assert.Equal((0, 0, HandleKind.Prepared), (output.Call, output.Ordinal, output.Kind));
        Assert.Equal("#0", output.Key);
    }

    [SqlFact]
    public async Task Handles_are_mapped_when_the_replay_is_given_other_numbers()
    {
        var requests = OdbcPrepared();
        var (prepare, execute) = (requests[0], requests[1]);

        async Task<Execution> ReplayAsync(bool map)
        {
            await using var connection = await ReplayConnection.OpenAsync(TestServer.Endpoint, new LoginOptions(Database: "ReplayTest"), CancellationToken.None);

            // Handle 1 goes to another statement first: three rows rather than one.
            var occupy = await connection.ExecuteAsync(new RequestMessage(TdsConstants.PacketType.SqlBatch, RequestBuilder.Batch(
                "DECLARE @h int; EXEC sp_prepare @h OUTPUT, N'@P1 int', N'SELECT @P1 FROM dbo.Numbers(3)'; SELECT @h;")), null, CancellationToken.None);
            Assert.Equal("ok", occupy.Status);

            var handles = new HandleMap();
            async Task<Execution> SendAsync(RecordedRequest request)
            {
                byte[] payload = (byte[])request.Raw!.Clone();
                RequestPatcher.PatchDescriptor(payload, connection.TransactionDescriptor);
                var outputs = map ? RequestPatcher.PatchHandles(payload, handles).Outputs : [];
                var result = await connection.ExecuteAsync(new RequestMessage(TdsConstants.PacketType.Rpc, payload), null, CancellationToken.None);
                handles.Learn(outputs, request.Response, result.Response);
                return result;
            }

            var prepared = await SendAsync(prepare);
            Assert.Equal("ok", prepared.Status);
            Assert.Equal(2, prepared.Response.Outputs.Single(o => o.Ordinal == 0).Value);
            return await SendAsync(execute);
        }

        var mapped = await ReplayAsync(map: true);
        Assert.Equal("ok", mapped.Status);
        Assert.Equal(execute.Response.ResultSets, mapped.Response.ResultSets);

        // The mutation check: without the map, sp_execute 1 runs the other statement.
        var unmapped = await ReplayAsync(map: false);
        Assert.Equal([3L], unmapped.Response.ResultSets);
        Assert.NotEqual(execute.Response.ResultSets, unmapped.Response.ResultSets);
    }
    /// <summary>
    /// JDBC's server cursor (selectMethod=cursor) from the recording: sp_cursorprepexec, its fetches,
    /// sp_cursorclose. A cursor opened first on the replay connection makes the server number the
    /// replayed one differently, so every fetch must be rewritten to it - and its rows read by the
    /// metadata that came with the cursor's open, since fetch responses do not repeat it.
    /// </summary>
    [SqlFact]
    public async Task Cursor_handles_are_mapped_and_fetches_read_by_the_cursor_s_metadata()
    {
        var cursor = new List<RecordedRequest>();
        foreach (string line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "workload.ndjson")))
        {
            if (!line.Contains("\"request.rpc\"") || !line.Contains("ReplayProbe.Jdbc") || !line.Contains("sp_cursor")) continue;
            var request = RecordedRequest.Parse(System.Text.Encoding.UTF8.GetBytes(line));
            cursor.Add(request);
            if (request.Calls[0].Proc == "sp_cursorclose") break;
        }
        Assert.Equal("sp_cursorprepexec", cursor[0].Calls[0].Proc);

        async Task<(List<Execution> Results, HandleMap Handles)> ReplayAsync(bool map)
        {
            await using var connection = await ReplayConnection.OpenAsync(TestServer.Endpoint, new LoginOptions(Database: "ReplayTest"), CancellationToken.None);
            var occupy = await connection.ExecuteAsync(new RequestMessage(TdsConstants.PacketType.SqlBatch, RequestBuilder.Batch(
                "DECLARE @c int, @s int = 1, @o int = 1, @n int; EXEC sp_cursoropen @c OUTPUT, N'SELECT N FROM dbo.Numbers(3)', @s OUTPUT, @o OUTPUT, @n OUTPUT;")),
                null, CancellationToken.None);
            Assert.Equal("ok", occupy.Status);

            var handles = new HandleMap();
            var results = new List<Execution>();
            foreach (var request in cursor)
            {
                byte[] payload = (byte[])request.Raw!.Clone();
                RequestPatcher.PatchDescriptor(payload, connection.TransactionDescriptor);
                var use = map ? RequestPatcher.PatchHandles(payload, handles) : HandleUse.None;
                var result = await connection.ExecuteAsync(new RequestMessage(TdsConstants.PacketType.Rpc, payload), null,
                                                           CancellationToken.None, handles.ColumnsOf(use.Cursor));
                handles.Learn(use.Outputs, request.Response, result.Response);
                results.Add(result);
            }
            return (results, handles);
        }

        var (mapped, handles) = await ReplayAsync(map: true);
        Assert.All(mapped, r => Assert.Equal("ok", r.Status));
        Assert.True(handles.Rewritten >= cursor.Count - 1, $"only {handles.Rewritten} rewritten");
        Assert.All(mapped.Skip(1), r => Assert.Null(r.Response.Problem));
        long fetched = mapped.Skip(1).SelectMany(r => r.Response.ResultSets).Sum();
        Assert.True(fetched >= 40, $"fetched {fetched} rows");

        // The mutation check: unmapped, the fetches name the recorded number, which on this connection
        // is the other cursor - three rows of something else, or an error - never the recorded rows.
        var (unmapped, _) = await ReplayAsync(map: false);
        bool wrong = unmapped.Skip(1).Any(r => r.Status == "error" || r.Response.Problem is not null)
                     || unmapped.Skip(1).SelectMany(r => r.Response.ResultSets).Sum() != fetched;
        Assert.True(wrong, "without the map, the fetches still found the replayed cursor");
    }
}
