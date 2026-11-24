using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using DaffyReplay.Recording;
using DaffyReplay.Replay;
using DaffyReplay.Tds;

namespace DaffyReplay.Results;

/// <summary>How a replayed request compares with its recording.</summary>
internal static class Outcome
{
    public const string Same = "same";                       // ok both times, or the same errors both times
    public const string NewError = "new_error";              // ok when recorded, failed on replay
    public const string FixedError = "fixed_error";          // failed when recorded, ok on replay
    public const string ChangedError = "changed_error";      // failed both times, with different errors
    public const string Timeout = "timeout";                 // ran past --request-timeout (in serial pace: blocked)
    public const string ConnectionFailed = "failed";         // the connection broke under it
    public const string CancelledWhenRecorded = "cancelled_when_recorded";
    public const string IncompleteWhenRecorded = "incomplete_when_recorded";
    public const string NotReplayed = "not_replayed";        // its session could not log in, or broke earlier

    /// <summary>Errors that mean "the data is not what it was", far more often than "the server behaves differently".</summary>
    public static readonly HashSet<int> DataStateErrors = [2627, 2601, 547, 515, 8152, 2628];
}

/// <summary>One request, recorded and replayed side by side: a line of results.ndjson, and what the report adds up.</summary>
internal sealed class ResultRecord
{
    public required int Session { get; init; }
    public required int SessionKey { get; init; }
    public required long Seq { get; init; }
    public required DateTime RecordedStart { get; init; }
    public required string Fingerprint { get; init; }
    public required string FingerprintText { get; init; }
    public required string Kind { get; init; }
    public string? Proc { get; init; }
    public bool Approximate { get; init; }
    public required string Outcome { get; init; }

    /// <summary>Row counts or result-set sizes differ; null when there was nothing comparable.</summary>
    public bool? RowsDiffer { get; init; }

    public required RecordedResponse Recorded { get; init; }
    public double? RecordedDurationMs { get; init; }

    public Execution? Replay { get; init; }

    /// <summary>For the recorded pace: how late the request started, and why.</summary>
    public double? LagMs { get; init; }
    public double? LagServerMs { get; init; }
    public double? LagHostMs { get; init; }

    /// <summary>Why it was not replayed, for <see cref="Outcome.NotReplayed"/>.</summary>
    public string? Reason { get; init; }

    /// <summary>The replay ended in a different database from the one recorded.</summary>
    public bool DatabaseDiffers { get; init; }

    private static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>One line of results.ndjson: recorded and replay side by side, loadable next to the recording.</summary>
    public ReadOnlyMemory<byte> ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(buffer, Options))
        {
            w.WriteStartObject();
            w.WriteNumber("session", Session);
            w.WriteNumber("seq", Seq);
            w.WriteString("recordedStart", RecordedStart);
            w.WriteString("fingerprint", Fingerprint[..8]);
            w.WriteString("kind", Kind);
            if (Proc is not null) w.WriteString("proc", Proc);
            w.WriteBoolean("approximate", Approximate);
            w.WriteString("outcome", Outcome);
            if (RowsDiffer is { } differ) w.WriteBoolean("rowsDiffer", differ);
            if (DatabaseDiffers) w.WriteBoolean("databaseDiffers", true);
            if (Reason is not null) w.WriteString("reason", Reason);

            w.WriteStartObject("recorded");
            w.WriteString("status", Recorded.Status);
            if (RecordedDurationMs is { } recordedMs) w.WriteNumber("durationMs", recordedMs);
            if (Recorded.RowCount is { } recordedRows) w.WriteNumber("rowCount", recordedRows);
            if (Recorded.RowTotal is { } recordedTotal) w.WriteNumber("rowTotal", recordedTotal);
            WriteErrors(w, Recorded.Errors.Select(e => (e.Number, e.Message)));
            w.WriteEndObject();

            if (Replay is { } replay)
            {
                w.WriteStartObject("replay");
                w.WriteString("status", replay.Status);
                w.WriteNumber("durationMs", Math.Round(replay.DurationMs, 3));
                if (replay.FirstByteMs is { } firstByte) w.WriteNumber("firstByteMs", Math.Round(firstByte, 3));
                if (replay.Response.RowCount is { } rows) w.WriteNumber("rowCount", rows);
                if (Recorded.RowTotal is not null && replay.Response.RowCounts.Count > 0)
                    w.WriteNumber("rowTotal", replay.Response.RowCounts.Aggregate(0UL, (sum, n) => sum + n));
                WriteErrors(w, replay.Response.Errors.Select(e => (e.Number, e.Text)));
                if (replay.Failure is not null) w.WriteString("failure", replay.Failure);
                if (LagMs is { } lag) w.WriteNumber("lagMs", Math.Round(lag, 3));
                w.WriteEndObject();
            }

            w.WriteEndObject();
        }
        return buffer.WrittenMemory;
    }

    private static void WriteErrors(Utf8JsonWriter w, IEnumerable<(int Number, string Message)> errors)
    {
        w.WriteStartArray("errors");
        foreach (var (number, message) in errors)
        {
            w.WriteStartObject();
            w.WriteNumber("number", number);
            w.WriteString("message", message);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }
}

/// <summary>Decides how a replayed request compares with its recording.</summary>
internal static class Comparison
{
    /// <summary>
    /// The outcome, and whether rows differ. Per-statement row counts and result-set sizes are
    /// compared when DaffyTee decoded the recorded response (it does for small ones, up to
    /// --response-kb); for a larger one only the final row count is.
    /// </summary>
    /// <remarks>
    /// An approximate replay - a procedure call rendered as a T-SQL batch - is compared on its result
    /// sets and errors only: the batch adds statements of its own (filling a table-valued parameter,
    /// say), whose counts the recording never had.
    /// </remarks>
    public static (string Outcome, bool? RowsDiffer) Compare(RecordedResponse recorded, Execution replay, int recordedResponseKb, bool approximate = false)
    {
        string outcome = recorded.Status switch
        {
            "cancelled" => Outcome.CancelledWhenRecorded,
            "incomplete" => Outcome.IncompleteWhenRecorded,
            _ => replay.Status switch
            {
                "timeout" => Outcome.Timeout,
                "failed" => Outcome.ConnectionFailed,
                "error" when recorded.Status == "ok" => Outcome.NewError,
                "ok" when recorded.Status == "error" => Outcome.FixedError,
                "error" => SameErrors(recorded, replay) ? Outcome.Same : Outcome.ChangedError,
                _ => Outcome.Same,
            },
        };

        if (replay.Status is "timeout" or "failed" || recorded.Status is "cancelled" or "incomplete") return (outcome, null);
        return (outcome, RowsDiffer(recorded, replay, recordedResponseKb, approximate));
    }

    private static bool SameErrors(RecordedResponse recorded, Execution replay)
    {
        // A large recorded response has no decoded errors to compare: an error both times is the same.
        if (recorded.Errors.Count == 0) return true;
        var a = recorded.Errors.Select(e => e.Number).Order().ToList();
        var b = replay.Response.Errors.Select(e => e.Number).Order().ToList();
        return a.SequenceEqual(b);
    }

    private static bool? RowsDiffer(RecordedResponse recorded, Execution replay, int recordedResponseKb, bool approximate)
    {
        var response = replay.Response;
        if (recorded.ImportedFrom is not null) return ImportedRowsDiffer(recorded, response);

        bool decoded = recorded.Bytes <= recordedResponseKb * 1024L && recorded.DetailProblem is null;
        if (approximate)
            return decoded && response.Problem is null ? !(recorded.ResultSets ?? []).SequenceEqual(response.ResultSets) : null;

        if (decoded && response.Problem is null)
        {
            var recordedCounts = recorded.RowCounts ?? [];
            var recordedSets = recorded.ResultSets ?? [];
            return !recordedCounts.SequenceEqual(response.RowCounts) || !recordedSets.SequenceEqual(response.ResultSets);
        }

        if (recorded.RowCount is { } count && response.RowCount is { } replayed) return count != replayed;
        return null;
    }

    /// <summary>
    /// An imported recording (sqlsim's XML, from Extended Events) knows a batch's rows added up over
    /// its statements, and nothing per statement. That total is compared with the replay's, when the
    /// replay counted any (SET NOCOUNT ON leaves none to add). A procedure call imported as text is
    /// not compared: XEvents' text adds statements of its own (a table-valued parameter filled, an
    /// OUTPUT value selected), whose rows the call never had.
    /// </summary>
    private static bool? ImportedRowsDiffer(RecordedResponse recorded, ResponseSummary response)
    {
        if (recorded.RowTotal is not { } total || response.Problem is not null || response.RowCounts.Count == 0) return null;
        ulong replayed = 0;
        foreach (ulong count in response.RowCounts) replayed += count;
        return total != replayed;
    }
}
