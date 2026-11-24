using System.Diagnostics;
using DaffyReplay.Recording;

namespace DaffyReplay.Replay;

/// <summary>
/// Hands the recording to its sessions at the chosen pace.
///
///   recorded  each request at its recorded offset from the start (divided by --speed); a session
///             stays sequential, as the application was, so a request whose predecessor is still
///             running waits, and that wait is reported as lag
///   fast      no waiting: every session sends its requests back to back, sessions concurrently
///   serial    one request at a time across all sessions, in recorded start order
/// </summary>
internal static class Scheduler
{
    public static async Task RunAsync(ReplayContext context, CancellationToken cancellationToken)
    {
        context.RunStartTimestamp = Stopwatch.GetTimestamp();
        context.RunStartUtc = DateTime.UtcNow;

        if (context.Options.Pace == Pace.Serial) await SerialAsync(context, cancellationToken);
        else await DispatchAsync(context, cancellationToken);
    }

    /// <summary>
    /// One request at a time: each is handed to its session and waited for before the next. A
    /// request still waiting after <see cref="ReplayOptions.SerialWait"/> is almost always blocked on
    /// a lock that another session releases only later in the sequence - in the recording, that
    /// session simply carried on - so the replay carries on too, and the blocked request finishes
    /// when the lock is released, as it did then. Each such overlap is counted.
    /// </summary>
    private static async Task SerialAsync(ReplayContext context, CancellationToken cancellationToken)
    {
        var players = new Dictionary<int, SessionPlayer>();
        var running = new List<Task>();
        var wait = context.Options.SerialWait;
        try
        {
            foreach (var entry in context.Index.Entries.InOrder())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Kind == EntryKind.SessionStart)
                {
                    var player = new SessionPlayer(context, context.Index.Sessions[entry.Session]);
                    players[entry.Session] = player;
                    running.Add(Task.Run(() => player.RunQueueAsync(null, cancellationToken)));
                }

                if (!players.TryGetValue(entry.Session, out var target)) continue;

                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                target.Queue.Writer.TryWrite((entry, 0, done));

                if (entry.Kind == EntryKind.SessionEnd)
                {
                    target.Queue.Writer.TryComplete();
                    players.Remove(entry.Session);
                    continue;
                }

                if (await Task.WhenAny(done.Task, Task.Delay(wait, cancellationToken)) != done.Task)
                {
                    context.Overlapped();
                    Log.Debug("serial.overlap", $"{target.Session.Describe()} seq {entry.Seq} still running after {wait.TotalSeconds:0.#} s " +
                                                "(blocked?); carrying on with the next request", target.Session.DaffyTeeId);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped: what ran is reported.
        }
        finally
        {
            foreach (var player in players.Values) player.Queue.Writer.TryComplete();
            await Task.WhenAll(running);
        }
    }

    /// <summary>
    /// The recorded and fast paces: one dispatcher, on a thread of its own so its timing is not at
    /// the mercy of the thread pool, feeding one queue per session.
    /// </summary>
    private static async Task DispatchAsync(ReplayContext context, CancellationToken cancellationToken)
    {
        bool timed = context.Options.Pace == Pace.Recorded;
        double speed = context.Options.Speed;
        long origin = context.RunStartTimestamp;
        long recordingStart = context.RecordingStartTicks;
        var slots = context.Options.MaxSessions is int max ? new SemaphoreSlim(max, max) : null;

        var players = new Dictionary<int, SessionPlayer>();
        var running = new List<Task>();

        var dispatcher = Task.Factory.StartNew(() =>
        {
            try
            {
                foreach (var entry in context.Index.Entries.InOrder())
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    long due = 0;
                    if (timed)
                    {
                        // Recorded ticks are 100 ns; Stopwatch ticks are whatever the platform's are.
                        double offsetSeconds = (entry.Ticks - recordingStart) / (double)TimeSpan.TicksPerSecond / speed;
                        due = origin + (long)(offsetSeconds * Stopwatch.Frequency);
                        if (!PreciseClock.WaitUntil(due, cancellationToken)) break;
                        if (entry.Kind == EntryKind.Request)
                            context.Report.AddSchedulerError((Stopwatch.GetTimestamp() - due) * 1000.0 / Stopwatch.Frequency);
                    }

                    if (entry.Kind == EntryKind.SessionStart)
                    {
                        var player = new SessionPlayer(context, context.Index.Sessions[entry.Session]);
                        players[entry.Session] = player;
                        lock (running)
                        {
                            running.Add(Task.Run(() => player.RunQueueAsync(slots, cancellationToken)));
                            if (running.Count > 256) running.RemoveAll(t => t.IsCompleted);
                        }
                    }

                    if (!players.TryGetValue(entry.Session, out var target)) continue;
                    target.Queue.Writer.TryWrite((entry, due, null));
                    if (entry.Kind == EntryKind.SessionEnd)
                    {
                        target.Queue.Writer.TryComplete();
                        players.Remove(entry.Session);
                    }
                }
            }
            finally
            {
                // Stopped early, or sessions the recording never closed: let their queues drain and end.
                foreach (var player in players.Values) player.Queue.Writer.TryComplete();
            }
        }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        await dispatcher;
        Task[] remaining;
        lock (running) remaining = [.. running];
        await Task.WhenAll(remaining);
    }
}

/// <summary>
/// Waiting until a moment, to within a millisecond or so. Windows' timers tick about every 15.6 ms,
/// so a plain sleep cannot hit the recorded timings; this sleeps coarsely while the moment is far
/// off and spins for the last stretch. The dispatcher thread spends a core doing it, on purpose.
/// </summary>
internal static class PreciseClock
{
    private static readonly long SpinWindow = Stopwatch.Frequency / 50;   // the last 20 ms

    /// <summary>False when cancelled first.</summary>
    public static bool WaitUntil(long timestamp, CancellationToken cancellationToken)
    {
        while (true)
        {
            long remaining = timestamp - Stopwatch.GetTimestamp();
            if (remaining <= 0) return true;
            if (cancellationToken.IsCancellationRequested) return false;

            if (remaining > SpinWindow)
            {
                int sleepMs = (int)((remaining - SpinWindow) * 1000 / Stopwatch.Frequency);
                if (cancellationToken.WaitHandle.WaitOne(Math.Max(1, sleepMs))) return false;
            }
            else
            {
                Thread.SpinWait(20);
            }
        }
    }
}
