using System.Text.Json;
using System.Text.RegularExpressions;
using DaffyReplay.Recording;

namespace DaffyReplay.Tests;

/// <summary>Reading and indexing a recording - the committed one, made through DaffyTee 0.3.0 - with no network.</summary>
public sealed class RecordingTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "workload.ndjson");

    private static RecordingIndex Index(ReplayFilter? filter = null, string? path = null) =>
        RecordingIndex.Build(RecordingFiles.Open([path ?? Fixture]), filter);

    /// <summary>Counted from the fixture's text, so a fixture recorded again does not need the tests changed.</summary>
    private static int Lines(params string[] containing) =>
        File.ReadLines(Fixture).Count(line => containing.All(line.Contains));

    [Fact]
    public void The_fixture_is_indexed_whole_and_every_request_replays_exactly()
    {
        using var index = Index();
        var stats = index.Stats;

        int requests = Lines("\"type\":\"request."), sessions = Lines("\"type\":\"session.start\"");
        Assert.True(requests > 1000 && sessions > 30);
        Assert.Equal(requests, stats.Requests);
        Assert.Equal(requests, stats.Exact);
        Assert.Equal(0, stats.Approximate);
        Assert.Empty(stats.Skipped);
        Assert.Equal(sessions, index.Sessions.Count);
        Assert.True(stats.RawOn);
        Assert.Equal("0.3.0", stats.DaffyTeeVersion);
        Assert.Equal(requests + 2 * sessions, index.Entries.Count);
        Assert.True(stats.CursorCalls > 10, "the fixture has JDBC's server cursors");
        Assert.Empty(Inspector.Warnings(index));
    }

    [Fact]
    public void Entries_come_in_start_order_and_each_session_in_seq_order_between_its_start_and_end()
    {
        using var index = Index();
        var entries = index.Entries.InOrder().ToList();

        for (int i = 1; i < entries.Count; i++) Assert.True(entries[i - 1].Ticks <= entries[i].Ticks);

        foreach (var session in entries.GroupBy(e => e.Session))
        {
            var list = session.ToList();
            Assert.Equal(EntryKind.SessionStart, list[0].Kind);
            Assert.Equal(EntryKind.SessionEnd, list[^1].Kind);
            var seqs = list.Where(e => e.Kind == EntryKind.Request).Select(e => e.Seq).ToList();
            Assert.Equal(seqs.Order(), seqs);
        }
    }

    [Fact]
    public void An_event_is_read_back_by_where_the_index_found_it()
    {
        using var index = Index();
        var first = index.Entries.InOrder().First(e => e.Kind == EntryKind.Request);
        var request = index.Load(first);

        Assert.Equal(first.Seq, request.Seq);
        Assert.Equal(first.Ticks, request.StartUtc.Ticks);
        Assert.NotNull(request.Raw);
    }

    [Fact]
    public void A_large_index_spills_to_disk_and_merges_back_in_the_same_order()
    {
        var random = new Random(5);
        var entries = Enumerable.Range(0, 10_000)
            .Select(i => new IndexEntry(random.Next(1000), i, i, random.Next(50), 10, 0, EntryKind.Request))
            .ToList();

        using var spilled = new SortedIndex(chunkEntries: 700);
        foreach (var entry in entries) spilled.Add(entry);
        Assert.True(spilled.SpilledChunks > 10);

        var expected = entries.ToArray();
        Array.Sort(expected, (a, b) => IndexEntry.Compare(a, b));
        Assert.Equal(expected, spilled.InOrder());
        Assert.Equal(expected, spilled.InOrder());   // and again
    }

    [Fact]
    public void Filters_pick_sessions_and_requests()
    {
        using (var odbc = Index(new ReplayFilter(App: "ReplayProbe.Odbc")))
        {
            int expected = Lines("\"type\":\"request.", "\"app\":\"ReplayProbe.Odbc\"");
            Assert.True(expected > 10);
            Assert.Equal(expected, odbc.Stats.Replayable);
            Assert.Equal(odbc.Stats.Requests - expected, odbc.Stats.Filtered);
        }

        using (var load = Index(new ReplayFilter(App: "ReplayProbe.Load*", Login: "orders")))
            Assert.All(load.Sessions.Values.Where(s => s.Replayable > 0), s => Assert.Equal("orders", s.Login));

        using var excluded = Index(new ReplayFilter(Exclude: new Regex("auditlog", RegexOptions.IgnoreCase)));
        Assert.True(excluded.Stats.Filtered > 0);
        Assert.DoesNotContain(excluded.Stats.Fingerprints.Values, f => f.Text.Contains("auditlog"));
    }

    [Fact]
    public void Inspect_warns_about_what_would_make_a_replay_mean_less()
    {
        string path = Path.Combine(Path.GetTempPath(), $"daffyreplay-test-{Guid.NewGuid():N}.ndjson");
        try
        {
            var lines = File.ReadAllLines(Fixture).ToList();
            // An rpc without its raw bytes, a gap report, a session with no session.start, and a stray line.
            int rpc = lines.FindIndex(l => l.Contains("\"request.rpc\""));
            lines[rpc] = Regex.Replace(lines[rpc], "\"raw\":\"[^\"]*\",?", "").Replace(",}", "}");
            lines.Add("""{"specversion":"1.0","type":"proxy.gap","time":"2026-10-10T15:47:00Z","partitionkey":"proxy","data":{"v":1,"dropped":12,"reasons":{"queue_full":12}}}""");
            int start = lines.FindIndex(l => l.Contains("\"session.start\""));
            lines.RemoveAt(start);
            lines.Add("not an event");
            File.WriteAllLines(path, lines);

            using var index = Index(path: path);
            var warnings = Inspector.Warnings(index).ToList();

            Assert.Equal(1, index.Stats.Skipped["rpc_without_raw"]);
            Assert.Equal(1, index.Stats.BadLines);
            Assert.Contains(warnings, w => w.Contains("dropped 12 event"));
            Assert.Contains(warnings, w => w.Contains("began before the recording"));
            Assert.Contains(warnings, w => w.Contains("not DaffyTee events"));

            // Allowed as approximate, the rpc is counted to be replayed as T-SQL instead.
            using var approximate = RecordingIndex.Build(RecordingFiles.Open([path]), allowApproximate: true);
            Assert.Equal(1, approximate.Stats.Approximate);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// DaffyTee started with --tee off says only "off" at proxy.start; the settings it recorded with
    /// arrive with the proxy.control event that switched the tee on.
    /// </summary>
    [Fact]
    public void A_recording_switched_on_at_the_prompt_takes_its_settings_from_the_switch()
    {
        string path = Path.Combine(Path.GetTempPath(), $"daffyreplay-test-{Guid.NewGuid():N}.ndjson");
        try
        {
            var lines = File.ReadAllLines(Fixture).ToList();
            int start = lines.FindIndex(l => l.Contains("\"type\":\"proxy.start\""));
            string settings = Regex.Match(lines[start], "\"settings\":\"([^\"]*)\"").Groups[1].Value;
            Assert.Contains("raw payloads", settings);
            lines[start] = lines[start].Replace("\"teeEnabled\":true", "\"teeEnabled\":false").Replace($"\"settings\":\"{settings}\"", "\"settings\":\"off\"");
            lines.Insert(start + 1, $$$"""{"specversion":"1.0","type":"proxy.control","time":"2026-10-10T16:32:32Z","partitionkey":"proxy","data":{"v":1,"change":"tee on","by":"console","teeEnabled":true,"settings":"{{{settings}}}"}}""");
            lines.Add("""{"specversion":"1.0","type":"proxy.control","time":"2026-10-10T18:00:00Z","partitionkey":"proxy","data":{"v":1,"change":"tee off","by":"console","teeEnabled":false,"settings":"off"}}""");
            File.WriteAllLines(path, lines);

            using var index = Index(path: path);
            Assert.Equal(settings, index.Stats.Settings);
            Assert.True(index.Stats.RawOn);
            Assert.Equal(2, index.Stats.Controls.Count);
            Assert.Empty(Inspector.Warnings(index));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A bulk load is an INSERT BULK batch and then its rows, and the server takes whatever follows
    /// an INSERT BULK to be the rows (4022 otherwise): the two are replayed together or not at all.
    /// </summary>
    [Fact]
    public void A_bulk_load_is_replayed_whole_or_not_at_all()
    {
        int loads = Lines("\"type\":\"request.bulk\""), requests = Lines("\"type\":\"request.");
        Assert.True(loads > 0);

        static List<RecordedRequest> Replayed(RecordingIndex index) =>
            index.Entries.InOrder().Where(e => e.Kind == EntryKind.Request).Select(e => index.Load(e)).ToList();
        static bool IsBulk(RecordedRequest r) => r.Kind == "bulk" || r.Sql?.StartsWith("insert bulk", StringComparison.OrdinalIgnoreCase) == true;

        // --no-bulk, or either half filtered out alone: both go.
        foreach (var filter in new[]
                 {
                     new ReplayFilter(NoBulk: true),
                     new ReplayFilter(Exclude: new Regex("^insert bulk", RegexOptions.IgnoreCase)),
                     new ReplayFilter(Exclude: new Regex("^bulk load", RegexOptions.IgnoreCase)),
                 })
        {
            using var index = Index(filter);
            Assert.Equal(2 * loads, index.Stats.Filtered);
            Assert.Equal(requests - 2 * loads, index.Stats.Exact);
            Assert.Empty(index.Stats.Skipped);
            Assert.DoesNotContain(Replayed(index), IsBulk);
        }

        string path = Path.Combine(Path.GetTempPath(), $"daffyreplay-test-{Guid.NewGuid():N}.ndjson");
        try
        {
            // Recorded with DaffyTee's default, --bulk meta: the bulk events carry no rows.
            File.WriteAllLines(path, File.ReadLines(Fixture).Select(l => l.Contains("\"type\":\"request.bulk\"")
                ? Regex.Replace(l, "\"raw\":\"[^\"]*\",?", "").Replace(",}", "}") : l));
            using (var meta = Index(path: path))
            {
                Assert.Equal(loads, meta.Stats.Skipped["bulk_without_rows"]);
                Assert.Equal(loads, meta.Stats.Skipped["insert_bulk_without_rows"]);
                Assert.Equal(requests - 2 * loads, meta.Stats.Exact);
                Assert.DoesNotContain(Replayed(meta), IsBulk);
                Assert.Contains(Inspector.Warnings(meta), w => w.Contains("--bulk meta"));
            }

            // --bulk off: no bulk event at all, only the gap in seq where it was.
            File.WriteAllLines(path, File.ReadLines(Fixture).Where(l => !l.Contains("\"type\":\"request.bulk\"")));
            using (var off = Index(path: path))
            {
                Assert.Equal(loads, off.Stats.Skipped["insert_bulk_without_rows"]);
                Assert.Equal(loads, off.Stats.BulkNotRecorded);
                Assert.Equal(0, off.Sessions.Values.Sum(s => s.MissingSeq));
                Assert.DoesNotContain(Replayed(off), IsBulk);
                var warnings = Inspector.Warnings(off).ToList();
                Assert.Contains(warnings, w => w.Contains("--bulk off"));
                Assert.DoesNotContain(warnings, w => w.Contains("gaps in seq"));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// DaffyTee 0.3.0 decoded a datetime after 00:02:23 wrong - the fixture's noon @dt is recorded as
    /// 00:01:47.1614635 - so a procedure call without raw bytes holding one is not replayed with it.
    /// </summary>
    [Theory]
    [InlineData(null, null, true)]                                     // as recorded by 0.3.0
    [InlineData(null, "2026-10-10T00:00:00", false)]                   // midnight: no overflow lands there
    [InlineData("0.3.1", "2026-10-10T13:14:15.123", false)]            // decoded right
    [InlineData("", null, true)]                                       // no proxy.start: 7 places give 0.3.0 away
    [InlineData("", "2026-10-10T13:14:15.123", false)]                 // ...and 3 do not
    public void A_procedure_call_with_a_datetime_DaffyTee_0_3_0_decoded_wrong_is_not_replayed_approximately(
        string? version, string? value, bool wrong)
    {
        string path = Path.Combine(Path.GetTempPath(), $"daffyreplay-test-{Guid.NewGuid():N}.ndjson");
        try
        {
            var lines = File.ReadAllLines(Fixture).ToList();
            int rpc = lines.FindIndex(l => l.Contains("\"name\":\"@dt\",\"type\":\"datetime\""));
            Assert.Contains("\"value\":\"2026-10-10T00:01:47.1614635\"", lines[rpc]);
            lines[rpc] = Regex.Replace(lines[rpc], "\"raw\":\"[^\"]*\",?", "").Replace(",}", "}");
            if (value is not null) lines[rpc] = lines[rpc].Replace("2026-10-10T00:01:47.1614635", value);

            int start = lines.FindIndex(l => l.Contains("\"type\":\"proxy.start\""));
            if (version == "") lines.RemoveAt(start);
            else if (version is not null) lines[start] = lines[start].Replace("\"version\":\"0.3.0\"", $"\"version\":\"{version}\"");
            File.WriteAllLines(path, lines);

            using var approximate = RecordingIndex.Build(RecordingFiles.Open([path]), allowApproximate: true);
            Assert.Equal(wrong ? 1 : 0, approximate.Stats.OldDatetimeCalls);
            Assert.Equal(wrong ? 0 : 1, approximate.Stats.Approximate);
            Assert.Equal(wrong ? 1 : 0, approximate.Stats.Skipped.GetValueOrDefault("old_datetime"));
            Assert.Equal(wrong, Inspector.Warnings(approximate).Any(w => w.Contains("decoded wrong") && w.Contains("they are skipped")));

            // Not allowed as approximate, it is skipped for its missing raw bytes, and inspect says what --approximate would do.
            using var exact = Index(path: path);
            Assert.Equal(1, exact.Stats.Skipped["rpc_without_raw"]);
            Assert.Equal(wrong, Inspector.Warnings(exact).Any(w => w.Contains("decoded wrong") && w.Contains("--approximate skips them")));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("0.3.0", true)]
    [InlineData("0.2.0", true)]
    [InlineData("0.3.0+7e171e4", true)]
    [InlineData("0.3.1", false)]
    [InlineData("0.4.0-beta", false)]
    [InlineData(null, null)]
    [InlineData("unknown", null)]
    public void DaffyTee_versions_before_0_3_1_decoded_datetimes_wrong(string? version, bool? affected) =>
        Assert.Equal(affected, OldDatetimes.Affects(version));

    [Fact]
    public void A_table_valued_parameter_s_datetime_cells_are_checked_too()
    {
        static JsonElement Request(string cell) => JsonDocument.Parse($$$"""
            {"kind":"rpc","calls":[{"proc":"dbo.Load","params":[{"name":"@rows","type":"dbo.Stamps","value":
                {"tableType":"dbo.Stamps","columns":[{"name":"Id","type":"int"},{"name":"At","type":"datetime"}],
                 "rows":[[1,"2026-10-10T00:00:00"],[2,{{{cell}}}]]}}]}]}
            """).RootElement;

        Assert.True(OldDatetimes.InRequest(Request("\"2026-10-10T00:01:47.1614635\""), affected: true));
        Assert.False(OldDatetimes.InRequest(Request("null"), affected: true));
        Assert.False(OldDatetimes.InRequest(Request("\"2026-10-10T00:01:47.1614635\""), affected: false));
        Assert.True(OldDatetimes.InRequest(Request("\"2026-10-10T00:01:47.1614635\""), affected: null));
        Assert.False(OldDatetimes.InRequest(Request("\"2026-10-10T13:14:15.123\""), affected: null));
    }

    [Fact]
    public void Wildcards_open_every_matching_file_in_name_order()
    {
        string directory = Directory.CreateTempSubdirectory("daffyreplay-files-").FullName;
        try
        {
            File.Copy(Fixture, Path.Combine(directory, "events-20261011.ndjson"));
            File.Copy(Fixture, Path.Combine(directory, "events-20261010.ndjson"));
            using var files = RecordingFiles.Open([Path.Combine(directory, "events-2026101*.ndjson")]);
            Assert.Equal(["events-20261010.ndjson", "events-20261011.ndjson"], files.Paths.Select(Path.GetFileName));
            Assert.Throws<ArgumentException>(() => RecordingFiles.Open([Path.Combine(directory, "none-*.ndjson")]));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
