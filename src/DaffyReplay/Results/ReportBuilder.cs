namespace DaffyReplay.Results;

/// <summary>
/// Durations kept for percentiles: every count, sum and maximum exactly, and a uniform random
/// sample of the values themselves (reservoir sampling) beyond a fixed number - so the memory a
/// replay of any length uses for its timings stays bounded.
/// </summary>
internal sealed class Reservoir(int capacity)
{
    private readonly List<double> _samples = [];
    private readonly Random _random = new(17);

    public long Count { get; private set; }
    public double Sum { get; private set; }
    public double Max { get; private set; }

    public void Add(double value)
    {
        Count++;
        Sum += value;
        if (value > Max) Max = value;

        if (_samples.Count < capacity) _samples.Add(value);
        else if (_random.NextInt64(Count) is var slot && slot < capacity) _samples[(int)slot] = value;
    }

    public Distribution Summarize()
    {
        if (Count == 0) return Distribution.Empty;
        var sorted = _samples.Order().ToArray();
        return new Distribution(Count, Round(Sum / Count), Round(At(sorted, 0.50)), Round(At(sorted, 0.95)), Round(At(sorted, 0.99)), Round(Max));
    }

    private static double At(double[] sorted, double quantile) =>
        sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(quantile * sorted.Length) - 1)];

    private static double Round(double value) => Math.Round(value, 3);
}

/// <summary>
/// Adds results up as the replay produces them - from any number of sessions at once - into what
/// the report says: outcomes, errors grouped, timings per statement, lag and throughput.
/// </summary>
internal sealed class ReportBuilder(DateTime recordingStart, double bucketSeconds)
{
    private const int StatementSamples = 5_000;
    private const int OverallSamples = 200_000;
    private const int ListLimit = 50;

    private readonly object _gate = new();
    private readonly Dictionary<string, long> _outcomes = [];
    private readonly Dictionary<string, long> _notReplayed = [];
    private readonly Dictionary<(string Kind, int Number, string Fingerprint), (string Message, string Statement, long Count)> _errors = [];
    private readonly Dictionary<string, (string Statement, long Count)> _rowMismatches = [];
    private readonly Dictionary<string, (string Statement, long Count)> _timeouts = [];
    private readonly Dictionary<string, StatementAccumulator> _statements = [];
    private readonly Reservoir _recorded = new(OverallSamples), _replay = new(OverallSamples);
    private readonly Reservoir _lag = new(OverallSamples), _lagServer = new(OverallSamples), _lagHost = new(OverallSamples);
    private readonly Reservoir _scheduler = new(OverallSamples);
    private readonly Dictionary<long, (long Recorded, long Replay)> _throughput = [];
    private long _rowsDiffer, _databaseDiffers, _replayed;

    public double BucketSeconds => bucketSeconds;

    private sealed class StatementAccumulator(string statement, string kind)
    {
        public string Statement { get; } = statement;
        public string Kind { get; } = kind;
        public Reservoir Recorded { get; } = new(StatementSamples);
        public Reservoir Replay { get; } = new(StatementSamples);
    }

    public long Replayed { get { lock (_gate) return _replayed; } }

    public long Count(string outcome)
    {
        lock (_gate) return _outcomes.GetValueOrDefault(outcome);
    }

    /// <summary>A replayed (or not replayed) request. <paramref name="replayOffsetSeconds"/> is when it started, from the run's start.</summary>
    public void Add(ResultRecord result, double replayOffsetSeconds)
    {
        lock (_gate)
        {
            _outcomes[result.Outcome] = _outcomes.GetValueOrDefault(result.Outcome) + 1;
            if (result.Outcome == Outcome.NotReplayed)
            {
                string reason = result.Reason ?? "not replayed";
                _notReplayed[reason] = _notReplayed.GetValueOrDefault(reason) + 1;
                return;
            }

            _replayed++;
            if (result.RowsDiffer == true)
            {
                _rowsDiffer++;
                Bump(_rowMismatches, result.Fingerprint, result.FingerprintText);
            }
            if (result.DatabaseDiffers) _databaseDiffers++;
            if (result.Outcome == Outcome.Timeout) Bump(_timeouts, result.Fingerprint, result.FingerprintText);

            switch (result.Outcome)
            {
                case Outcome.NewError:
                    foreach (var e in result.Replay!.Response.Errors.DistinctBy(e => e.Number)) AddError("new", e.Number, e.Text, result);
                    break;
                case Outcome.FixedError:
                    foreach (var e in result.Recorded.Errors.DistinctBy(e => e.Number)) AddError("fixed", e.Number, e.Message, result);
                    if (result.Recorded.Errors.Count == 0) AddError("fixed", 0, "(error not decoded in the recording)", result);
                    break;
                case Outcome.ChangedError:
                    foreach (var e in result.Replay!.Response.Errors.DistinctBy(e => e.Number)) AddError("changed", e.Number, e.Text, result);
                    break;
                case Outcome.Same when result.Replay!.Status == "error":
                    foreach (var e in result.Replay.Response.Errors.DistinctBy(e => e.Number)) AddError("unchanged", e.Number, e.Text, result);
                    break;
            }

            // Timings compare like with like: requests that completed both times.
            if (result.Replay is { Status: "ok" or "error" } replay && result.RecordedDurationMs is { } recordedMs
                && result.Recorded.Status is "ok" or "error")
            {
                _recorded.Add(recordedMs);
                _replay.Add(replay.DurationMs);
                if (!_statements.TryGetValue(result.Fingerprint, out var statement))
                    _statements[result.Fingerprint] = statement = new StatementAccumulator(result.FingerprintText, result.Kind);
                statement.Recorded.Add(recordedMs);
                statement.Replay.Add(replay.DurationMs);
            }

            if (result.LagMs is { } lag)
            {
                _lag.Add(lag);
                _lagServer.Add(result.LagServerMs ?? 0);
                _lagHost.Add(result.LagHostMs ?? 0);
            }

            long recordedBucket = (long)Math.Floor((result.RecordedStart - recordingStart).TotalSeconds / bucketSeconds);
            long replayBucket = (long)Math.Floor(replayOffsetSeconds / bucketSeconds);
            var r = _throughput.GetValueOrDefault(recordedBucket);
            _throughput[recordedBucket] = (r.Recorded + 1, r.Replay);
            var p = _throughput.GetValueOrDefault(replayBucket);
            _throughput[replayBucket] = (p.Recorded, p.Replay + 1);
        }
    }

    public void AddSchedulerError(double milliseconds)
    {
        lock (_gate) _scheduler.Add(Math.Abs(milliseconds));
    }

    private void AddError(string kind, int number, string message, ResultRecord result)
    {
        var key = (kind, number, result.Fingerprint);
        var existing = _errors.GetValueOrDefault(key);
        _errors[key] = (existing.Message ?? message, result.FingerprintText, existing.Count + 1);
    }

    private static void Bump(Dictionary<string, (string Statement, long Count)> map, string fingerprint, string statement)
    {
        var existing = map.GetValueOrDefault(fingerprint);
        map[fingerprint] = (statement, existing.Count + 1);
    }

    /// <summary>Not-replayed counts by reason, for the coverage section.</summary>
    public IReadOnlyDictionary<string, long> NotReplayed
    {
        get { lock (_gate) return new Dictionary<string, long>(_notReplayed); }
    }

    public (OutcomeInfo Outcomes, TimingInfo Timing, PaceInfo Pace) Build()
    {
        lock (_gate)
        {
            List<ErrorGroup> Errors(string kind) => _errors
                .Where(e => e.Key.Kind == kind)
                .Select(e => new ErrorGroup(e.Key.Number, e.Value.Message, e.Key.Fingerprint[..8], e.Value.Statement, e.Value.Count,
                                            Outcome.DataStateErrors.Contains(e.Key.Number)))
                .OrderByDescending(e => e.Count).ThenBy(e => e.Number).Take(ListLimit).ToList();

            static List<StatementGroup> Groups(Dictionary<string, (string Statement, long Count)> map) => map
                .Select(m => new StatementGroup(m.Key[..8], m.Value.Statement, m.Value.Count))
                .OrderByDescending(g => g.Count).Take(ListLimit).ToList();

            var outcomes = new OutcomeInfo(new Dictionary<string, long>(_outcomes), _rowsDiffer, _databaseDiffers,
                Errors("new"), Errors("fixed"), Errors("changed"), Errors("unchanged"), Groups(_rowMismatches), Groups(_timeouts));

            var statements = _statements
                .Select(s => new StatementTiming(s.Key[..8], s.Value.Statement, s.Value.Kind, s.Value.Recorded.Count,
                    s.Value.Recorded.Summarize(), s.Value.Replay.Summarize(),
                    Math.Round(s.Value.Recorded.Sum, 3), Math.Round(s.Value.Replay.Sum, 3)))
                .OrderByDescending(s => s.ReplayTotalMs).ToList();

            var timing = new TimingInfo(_recorded.Summarize(), _replay.Summarize(), Math.Round(_recorded.Sum, 3), Math.Round(_replay.Sum, 3),
                statements.Take(200).ToList(),
                statements.Where(s => s.DeltaMs > 0).OrderByDescending(s => s.DeltaMs).Take(10).ToList(),
                statements.Where(s => s.DeltaMs < 0).OrderBy(s => s.DeltaMs).Take(10).ToList(),
                statements.Where(s => s.Count >= 5 && s.Recorded.P95 > 0.05).OrderByDescending(s => s.P95Ratio).Take(10).Where(s => s.P95Ratio > 1).ToList());

            var throughput = _throughput.OrderBy(t => t.Key)
                .Select(t => new ThroughputBucket(t.Key * bucketSeconds, t.Value.Recorded, t.Value.Replay)).ToList();
            var pace = new PaceInfo(_lag.Summarize(), _lagServer.Summarize(), _lagHost.Summarize(), _scheduler.Summarize(), bucketSeconds, throughput);

            return (outcomes, timing, pace);
        }
    }
}
