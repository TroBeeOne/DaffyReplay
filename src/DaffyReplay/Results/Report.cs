using System.Text.Json;
using System.Text.Json.Serialization;

namespace DaffyReplay.Results;

/// <summary>Count, mean and percentiles of a set of durations, in milliseconds.</summary>
internal sealed record Distribution(long Count, double Mean, double P50, double P95, double P99, double Max)
{
    public static readonly Distribution Empty = new(0, 0, 0, 0, 0, 0);
}

internal sealed record TargetInfo(string Name, string Server, string? ServerVersion, string? Security);

internal sealed record RecordingInfo(
    IReadOnlyList<string> Files, DateTime? FirstRequest, DateTime? LastRequest, double Seconds,
    string? DaffyTeeVersion, string? Source, string? Settings, IReadOnlyList<string> RecordedServers, IReadOnlyList<string> ServerVersions,
    string? ImportedFrom = null, string? ImportedFile = null);

internal sealed record RunInfo(
    string Tool, DateTime Started, DateTime Ended, double WallSeconds, string Pace, double Speed,
    TargetInfo Target, RecordingInfo Recording, IReadOnlyDictionary<string, string> Filters,
    IReadOnlyDictionary<string, string> Options, bool Interrupted);

internal sealed record CoverageInfo(
    long Events, long Requests, long Replayed, long Exact, long Approximate, long Filtered,
    IReadOnlyDictionary<string, long> Skipped, IReadOnlyDictionary<string, long> NotReplayed,
    int Sessions, int SessionsBeforeRecording, int SessionsLoginFailed, int SessionsBroken, int MarsSessions);

/// <summary>One error, as a group: how often, from which statement, and what it said.</summary>
internal sealed record ErrorGroup(int Number, string Message, string Fingerprint, string Statement, long Count, bool LikelyDataState);

internal sealed record StatementGroup(string Fingerprint, string Statement, long Count);

internal sealed record OutcomeInfo(
    IReadOnlyDictionary<string, long> Counts, long RowsDiffer, long DatabaseDiffers,
    IReadOnlyList<ErrorGroup> NewErrors, IReadOnlyList<ErrorGroup> FixedErrors,
    IReadOnlyList<ErrorGroup> ChangedErrors, IReadOnlyList<ErrorGroup> UnchangedErrors,
    IReadOnlyList<StatementGroup> RowMismatches, IReadOnlyList<StatementGroup> Timeouts);

internal sealed record StatementTiming(
    string Fingerprint, string Statement, string Kind, long Count,
    Distribution Recorded, Distribution Replay, double RecordedTotalMs, double ReplayTotalMs)
{
    [JsonIgnore] public double DeltaMs => ReplayTotalMs - RecordedTotalMs;
    [JsonIgnore] public double P95Ratio => Recorded.P95 > 0 ? Replay.P95 / Recorded.P95 : 0;
}

internal sealed record TimingInfo(
    Distribution Recorded, Distribution Replay, double RecordedTotalMs, double ReplayTotalMs,
    IReadOnlyList<StatementTiming> Statements,
    IReadOnlyList<StatementTiming> RegressionsByTotal, IReadOnlyList<StatementTiming> ImprovementsByTotal,
    IReadOnlyList<StatementTiming> RegressionsByP95);

internal sealed record ThroughputBucket(double Offset, long Recorded, long Replay);

internal sealed record PaceInfo(
    Distribution Lag, Distribution LagServer, Distribution LagHost, Distribution SchedulerError,
    double BucketSeconds, IReadOnlyList<ThroughputBucket> Throughput)
{
    /// <summary>Serial pace: requests still running (usually blocked) when the next one was started anyway.</summary>
    public long Overlapped { get; init; }
}

internal sealed record HandleInfo(long Rewritten, long Unknown);

/// <summary>Everything a run found out: report.json, and the source of report.html and the console summary.</summary>
internal sealed record Report(
    int Version, RunInfo Run, CoverageInfo Coverage, OutcomeInfo Outcomes, TimingInfo Timing, PaceInfo Pace,
    HandleInfo Handles, IReadOnlyList<string> Warnings)
{
    public const int SchemaVersion = 1;

    public static Report Load(string path) =>
        JsonSerializer.Deserialize(File.ReadAllBytes(path), ReportJsonContext.Default.Report)
        ?? throw new InvalidDataException($"{path} is not a DaffyReplay report");

    public void Save(string path) =>
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(this, ReportJsonContext.Default.Report));
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Report))]
internal sealed partial class ReportJsonContext : JsonSerializerContext;
