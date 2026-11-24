using System.Text.Json;
using DaffyReplay.Cli;
using DaffyReplay.Recording;
using DaffyReplay.Replay;
using DaffyReplay.Results;
using DaffyReplay.Tds;

namespace DaffyReplay.Tests;

public sealed class FingerprintTests
{
    [Theory]
    [InlineData("SELECT * FROM t WHERE Id = 7", "select * from t where id=?")]
    [InlineData("select *\r\n  from T   where id=12 -- a comment", "select * from t where id=?")]
    [InlineData("SELECT Name FROM [dbo].[Customers] WHERE Name = N'Grace' AND x = 'it''s'", "select name from [dbo].[Customers] where name=? and x=?")]
    [InlineData("SELECT 1.5e3, 0xCAFE, -4 /* block */", "select ?,?,-?")]
    [InlineData("SELECT * FROM t WHERE Id IN (1, 2, 3)", "select * from t where id in(?)")]
    [InlineData("SELECT COUNT(*) FROM t", "select count(*) from t")]
    [InlineData("SELECT @p1, col2 FROM t2", "select @p1,col2 from t2")]
    public void Literals_whitespace_and_case_are_normalised(string sql, string expected) =>
        Assert.Equal(expected, Fingerprint.Normalize(sql));

    [Fact]
    public void A_procedure_call_is_its_procedure_and_a_parameterised_one_its_statement()
    {
        Assert.Equal("exec dbo.getcustomer", Fingerprint.Of("rpc", null, [("dbo.GetCustomer", null)], null, null, null).Text);
        Assert.Equal("select * from t where id=@id", Fingerprint.Of("rpc", null, [("sp_executesql", "SELECT * FROM t WHERE Id = @id")], null, null, null).Text);
        Assert.Equal(Fingerprint.Of("batch", "SELECT 1", [], null, null, null).Id, Fingerprint.Of("batch", "select   2", [], null, null, null).Id);
        Assert.Equal("transaction commit", Fingerprint.Of("txn", null, [], "commit", null, null).Text);
    }
}

public sealed class ReportMathTests
{
    [Fact]
    public void Percentiles_come_from_every_value_until_the_sample_is_full()
    {
        var reservoir = new Reservoir(10_000);
        for (int i = 1; i <= 1000; i++) reservoir.Add(i);
        var d = reservoir.Summarize();
        Assert.Equal((1000L, 500.5, 500.0, 950.0, 990.0, 1000.0), (d.Count, d.Mean, d.P50, d.P95, d.P99, d.Max));
    }

    [Fact]
    public void Beyond_its_capacity_a_reservoir_keeps_exact_totals_and_close_percentiles()
    {
        var reservoir = new Reservoir(2_000);
        for (int i = 1; i <= 100_000; i++) reservoir.Add(i);
        var d = reservoir.Summarize();
        Assert.Equal(100_000, d.Count);
        Assert.Equal(100_000, d.Max);
        Assert.Equal(50_000.5, d.Mean);
        Assert.InRange(d.P50, 45_000, 55_000);
        Assert.InRange(d.P95, 93_000, 97_000);
    }

    private static Execution Replayed(string status, params int[] errors)
    {
        var execution = new Execution { Status = status };
        foreach (int number in errors) execution.Response.Errors.Add(new ServerMessage(number, 1, 16, "m", "", "", 1));
        return execution;
    }

    [Fact]
    public void Outcomes_compare_the_replay_with_its_recording()
    {
        var ok = new RecordedResponse { Status = "ok" };
        var failed = new RecordedResponse { Status = "error", Errors = [new RecordedError(2627, 14, 1, "dup")] };

        Assert.Equal(Outcome.Same, Comparison.Compare(ok, Replayed("ok"), 64).Outcome);
        Assert.Equal(Outcome.NewError, Comparison.Compare(ok, Replayed("error", 547), 64).Outcome);
        Assert.Equal(Outcome.FixedError, Comparison.Compare(failed, Replayed("ok"), 64).Outcome);
        Assert.Equal(Outcome.Same, Comparison.Compare(failed, Replayed("error", 2627), 64).Outcome);
        Assert.Equal(Outcome.ChangedError, Comparison.Compare(failed, Replayed("error", 8134), 64).Outcome);
        Assert.Equal(Outcome.Timeout, Comparison.Compare(ok, Replayed("timeout"), 64).Outcome);
        Assert.Equal(Outcome.CancelledWhenRecorded, Comparison.Compare(new RecordedResponse { Status = "cancelled" }, Replayed("ok"), 64).Outcome);
    }

    [Fact]
    public void Row_counts_are_compared_per_statement_when_the_recording_decoded_them()
    {
        var recorded = new RecordedResponse { Status = "ok", Bytes = 500, RowCounts = [1, 3], ResultSets = [3] };
        var same = Replayed("ok");
        same.Response.RowCounts.AddRange([1UL, 3UL]);
        same.Response.ResultSets.Add(3);
        Assert.False(Comparison.Compare(recorded, same, 64).RowsDiffer);

        var different = Replayed("ok");
        different.Response.RowCounts.AddRange([1UL, 2UL]);
        different.Response.ResultSets.Add(2);
        Assert.True(Comparison.Compare(recorded, different, 64).RowsDiffer);

        // Too large for DaffyTee to have decoded: only the final count is compared.
        var large = new RecordedResponse { Status = "ok", Bytes = 900_000, RowCount = 150_000 };
        different.Response.DoneStatus = TdsConstants.DoneStatus.Count;
        different.Response.DoneRowCount = 150_000;
        Assert.False(Comparison.Compare(large, different, 64).RowsDiffer);
    }

    [Fact]
    public void A_report_survives_being_written_and_read()
    {
        var builder = new ReportBuilder(new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc), 10);
        var (outcomes, timing, pace) = builder.Build();
        var report = new Report(1,
            new RunInfo("daffyreplay test", DateTime.UtcNow, DateTime.UtcNow, 1, "serial", 1, new TargetInfo("T", "h:1", null, null),
                new RecordingInfo(["a.ndjson"], null, null, 0, "0.3.0", null, null, [], []), new Dictionary<string, string>(), new Dictionary<string, string>(), false),
            new CoverageInfo(1, 1, 1, 1, 0, 0, new Dictionary<string, long>(), new Dictionary<string, long>(), 1, 0, 0, 0, 0),
            outcomes, timing, pace with { Overlapped = 3 }, new HandleInfo(2, 0), ["a warning"]);

        string path = Path.GetTempFileName();
        try
        {
            report.Save(path);
            var back = Report.Load(path);
            Assert.Equal("serial", back.Run.Pace);
            Assert.Equal(3, back.Pace.Overlapped);
            Assert.Equal(["a warning"], back.Warnings);
            Assert.Contains("\"handles\"", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public sealed class RequestTests
{
    [Theory]
    [InlineData("begin", null, "serializable", false)]
    [InlineData("commit", null, null, false)]
    [InlineData("commit", null, "unchanged", true)]
    [InlineData("rollback", "partial", null, false)]
    [InlineData("save", "partial", null, false)]
    public void Transaction_requests_are_rebuilt_as_SqlClient_writes_them(string op, string? name, string? isolation, bool beginsNew)
    {
        byte[] payload = RequestBuilder.Transaction(op, name, isolation, beginsNew, descriptor: 0x42)!;
        var parsed = TransactionRequestParser.Parse(payload);
        Assert.Equal(op, parsed.Operation);
        Assert.Equal(name, parsed.Name);
        Assert.Equal(op == "begin" || beginsNew, parsed.BeginsNew);   // a begin always begins one
        if (op == "begin") Assert.Equal(isolation, parsed.IsolationLevel);
        Assert.Equal(0x42UL, AllHeaders.TransactionDescriptor(payload));
    }

    [Fact]
    public void The_descriptor_and_outstanding_count_are_patched_in_place()
    {
        byte[] payload = RequestBuilder.Batch("SELECT 1", descriptor: 0x1111);
        payload[18] = 9;   // the outstanding-request count, as a client with several in flight would send
        Assert.True(RequestPatcher.PatchDescriptor(payload, 0x0000004B00000001));
        Assert.Equal(0x0000004B00000001UL, AllHeaders.TransactionDescriptor(payload));
        Assert.Equal(1, BitConverter.ToInt32(payload, 18));
        Assert.Equal("SELECT 1", RequestParser.ParseBatchText(payload));
        Assert.False(RequestPatcher.PatchDescriptor([0x81, 0x01, 0x00], 1));
    }
}

public sealed class ArgumentTests
{
    private static readonly HashSet<string> WithValue = [.. Arguments.FilterOptions, "--target", "--speed"];
    private static readonly HashSet<string> Flags = ["--yes"];

    [Fact]
    public void Values_flags_and_files_are_told_apart()
    {
        var parsed = Arguments.Parse(["a.ndjson", "--target", "DEV01", "--yes", "b*.ndjson", "--speed", "2.5", "--from", "2026-10-10T14:00:00"], WithValue, Flags);
        Assert.Equal(["a.ndjson", "b*.ndjson"], parsed.Positional);
        Assert.Equal("DEV01", parsed.Get("--target"));
        Assert.True(parsed.Has("--yes"));
        Assert.Equal(2.5, parsed.Double("--speed", 0));
        Assert.Equal(new DateTime(2026, 10, 10, 14, 0, 0, DateTimeKind.Utc), parsed.Filter().From);
    }

    [Fact]
    public void Mistakes_are_named()
    {
        Assert.Throws<ArgumentException>(() => Arguments.Parse(["--nope"], WithValue, Flags));
        Assert.Throws<ArgumentException>(() => Arguments.Parse(["--target"], WithValue, Flags));
        Assert.Throws<ArgumentException>(() => Arguments.Parse(["--speed", "fast"], WithValue, Flags).Double("--speed", 0));
        Assert.Throws<ArgumentException>(() => Arguments.Parse(["--exclude", "("], WithValue, Flags).Filter());
    }

    [Fact]
    public void Wildcard_filters_match_without_regard_to_case()
    {
        Assert.True(ReplayFilter.Like("ReplayProbe.Load3", "replayprobe.load*"));
        Assert.False(ReplayFilter.Like("OtherApp", "Replay*"));
        Assert.True(ReplayFilter.Like("svc_app", "SVC_APP"));
    }
}
