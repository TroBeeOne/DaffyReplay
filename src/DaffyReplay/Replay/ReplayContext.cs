using System.Diagnostics;
using DaffyReplay.IO;
using DaffyReplay.Recording;
using DaffyReplay.Results;
using DaffyReplay.Security;

namespace DaffyReplay.Replay;

internal enum Pace { Recorded, Fast, Serial }

/// <summary>How a run replays, as the command line set it.</summary>
internal sealed record ReplayOptions(
    Pace Pace = Pace.Recorded,
    double Speed = 1,
    int? MaxSessions = null,
    TimeSpan? RequestTimeout = null,
    bool KeepAppName = false,
    bool KeepHostName = false,
    int PacketSize = Target.LoginOptions.DefaultPacketSize,
    int RecordedResponseKb = 64,
    bool AllowApproximate = false)
{
    /// <summary>Serial pace: how long a request may run before the next one starts anyway.</summary>
    public TimeSpan SerialWait { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Off only for the mutation checks (hidden --debug-no-descriptors, --debug-no-handles), which
    /// prove the verification would notice if this patching were broken.
    /// </summary>
    public bool PatchDescriptors { get; init; } = true;
    public bool MapHandles { get; init; } = true;

    /// <summary>A request recorded as cancelled is cancelled again, after its recorded duration.</summary>
    public bool ReplayCancels { get; init; }
}

/// <summary>What every session of a run shares: the recording, the target, the options, and where results go.</summary>
internal sealed class ReplayContext
{
    public required RecordingIndex Index { get; init; }
    public required TargetRecord Target { get; init; }
    public required ReplayOptions Options { get; init; }
    public required ReportBuilder Report { get; init; }
    public RollingFileWriter? Results { get; init; }

    public long RunStartTimestamp { get; set; } = Stopwatch.GetTimestamp();
    public DateTime RunStartUtc { get; set; } = DateTime.UtcNow;

    /// <summary>When the first replayed thing was recorded: time zero of the recorded pace.</summary>
    public long RecordingStartTicks { get; set; }

    // Live counters, for the progress line.
    private int _activeSessions;
    private long _requests, _newErrors, _failures, _handlesRewritten, _handlesUnknown;
    private int _loginFailures, _broken;
    private long _overlapped;
    private long _recordedPositionTicks;
    private double _lastLagMs;

    public int ActiveSessions => Volatile.Read(ref _activeSessions);
    public long Requests => Interlocked.Read(ref _requests);
    public long NewErrors => Interlocked.Read(ref _newErrors);
    public long Failures => Interlocked.Read(ref _failures);
    public int LoginFailures => Volatile.Read(ref _loginFailures);
    public int BrokenSessions => Volatile.Read(ref _broken);
    public long HandlesRewritten => Interlocked.Read(ref _handlesRewritten);
    public long HandlesUnknown => Interlocked.Read(ref _handlesUnknown);
    public double LastLagMs => Volatile.Read(ref _lastLagMs);
    public TimeSpan RecordedPosition => TimeSpan.FromTicks(Math.Max(0, Interlocked.Read(ref _recordedPositionTicks) - RecordingStartTicks));

    public void SessionOpened() => Interlocked.Increment(ref _activeSessions);
    public void SessionClosed() => Interlocked.Decrement(ref _activeSessions);
    public void LoginFailed() => Interlocked.Increment(ref _loginFailures);
    public void SessionBroke() => Interlocked.Increment(ref _broken);
    public void Overlapped() => Interlocked.Increment(ref _overlapped);

    /// <summary>Serial pace: requests the replay carried on past while they were still running.</summary>
    public long OverlappedRequests => Interlocked.Read(ref _overlapped);

    public void CountHandles(HandleMap handles)
    {
        Interlocked.Add(ref _handlesRewritten, handles.Rewritten);
        Interlocked.Add(ref _handlesUnknown, handles.Unknown);
    }

    public void Advance(long recordedTicks)
    {
        long current;
        do { current = Interlocked.Read(ref _recordedPositionTicks); }
        while (recordedTicks > current && Interlocked.CompareExchange(ref _recordedPositionTicks, recordedTicks, current) != current);
    }

    /// <summary>A request's result: into the report, and into results.ndjson when asked for.</summary>
    public void Emit(ResultRecord result, long startTimestamp)
    {
        Interlocked.Increment(ref _requests);
        if (result.Outcome == Outcome.NewError) Interlocked.Increment(ref _newErrors);
        if (result.Outcome is Outcome.ConnectionFailed or Outcome.Timeout) Interlocked.Increment(ref _failures);
        if (result.LagMs is { } lag) Volatile.Write(ref _lastLagMs, lag);

        double offset = startTimestamp == 0 ? 0 : Stopwatch.GetElapsedTime(RunStartTimestamp, startTimestamp).TotalSeconds;
        Report.Add(result, offset);
        Results?.WriteLines([result.ToJson()]);
    }
}
