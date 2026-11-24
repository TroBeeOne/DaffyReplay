using System.Diagnostics;
using System.Threading.Channels;
using DaffyReplay.Recording;
using DaffyReplay.Results;
using DaffyReplay.Target;
using DaffyReplay.Tds;

namespace DaffyReplay.Replay;

/// <summary>
/// One recorded session, replayed on a connection of its own - sessions never share one, as the
/// application's never did. Requests run one after another, in recorded order, each patched for
/// this connection (transaction descriptor, handles) and compared with its recording when it ends.
///
/// An error does not end the session: the application may have handled it and carried on. A
/// broken connection does, and the session's remaining requests are reported as not replayed.
/// </summary>
internal sealed class SessionPlayer(ReplayContext context, RecordedSession session)
{
    private readonly HandleMap _handles = new();

    /// <summary>Approximate replay: the declarations each recorded prepared handle was prepared with.</summary>
    private readonly Dictionary<int, string> _preparedDeclarations = [];
    private ReplayConnection? _connection;
    private string? _notReplayed;
    private long _idleSince;
    private bool _opened, _closed;

    /// <summary>
    /// What the dispatcher hands this session: each entry, when it is due (recorded pace), and a
    /// signal to set once it has been dealt with (serial pace waits on it).
    /// </summary>
    public Channel<(IndexEntry Entry, long Due, TaskCompletionSource? Done)> Queue { get; } =
        Channel.CreateUnbounded<(IndexEntry, long, TaskCompletionSource?)>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    public RecordedSession Session => session;

    /// <summary>Works through the queue until the session ends.</summary>
    public async Task RunQueueAsync(SemaphoreSlim? sessionSlots, CancellationToken cancellationToken)
    {
        bool holdingSlot = false;
        try
        {
            await foreach (var (entry, due, done) in Queue.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    switch (entry.Kind)
                    {
                        case EntryKind.SessionStart:
                            if (sessionSlots is not null)
                            {
                                await sessionSlots.WaitAsync(cancellationToken);
                                holdingSlot = true;
                            }
                            await OpenAsync(cancellationToken);
                            break;

                        case EntryKind.Request:
                            await PlayAsync(entry, due == 0 ? null : due, cancellationToken);
                            break;

                        case EntryKind.SessionEnd:
                            await CloseAsync();
                            return;
                    }
                }
                finally
                {
                    done?.TrySetResult();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped: what ran is reported.
        }
        finally
        {
            await CloseAsync();
            if (holdingSlot) sessionSlots!.Release();
        }
    }

    /// <summary>Logs in as the recorded session did: its database, its login's mapping, its application name if asked.</summary>
    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        if (_opened) return;
        _opened = true;

        var endpoint = context.Target.EndpointFor(session.Login, session.TargetLogin, out var mapping);
        var options = new LoginOptions(
            AppName: context.Options.KeepAppName && session.App.Length > 0 ? session.App : "DaffyReplay",
            HostName: context.Options.KeepHostName ? session.Host : "",
            Database: endpoint.Database ?? session.Database,
            PacketSize: context.Options.PacketSize,
            ColumnEncryption: session.BulkColumnEncryption ?? true);

        try
        {
            _connection = await ReplayConnection.OpenAsync(endpoint, options, cancellationToken);
            context.SessionOpened();
            _idleSince = Stopwatch.GetTimestamp();
            Log.Debug("session.open",
                $"{session.Describe()} -> spid {_connection.Spid} as {endpoint.UserName}{(mapping is null ? "" : $" (mapped from {mapping.Recorded})")}",
                session.DaffyTeeId);
        }
        catch (ConnectException ex)
        {
            _notReplayed = $"login failed: {ex.Message}";
            context.LoginFailed();
            Log.Warn("session.login_failed", $"{session.Describe()} could not log in as {endpoint.UserName}: {ex.Message}", session.DaffyTeeId);
        }
    }

    public async Task CloseAsync()
    {
        if (_closed) return;
        _closed = true;
        if (_connection is null) return;

        context.CountHandles(_handles);
        context.SessionClosed();
        await _connection.DisposeAsync();
        _connection = null;
    }

    /// <summary>
    /// Replays one request. <paramref name="due"/> is when the recorded pace wants it started
    /// (a Stopwatch timestamp): lateness behind it is split into waiting on the server (this
    /// session's previous request was still running) and the replay host falling behind.
    /// </summary>
    public async Task PlayAsync(IndexEntry entry, long? due, CancellationToken cancellationToken)
    {
        context.Advance(entry.Ticks);

        RecordedRequest recorded;
        try
        {
            recorded = context.Index.Load(entry);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or FormatException or InvalidOperationException)
        {
            Log.Warn("request.unreadable", $"the event at {context.Index.Files.Paths[entry.File]}:{entry.Offset} could not be read again: {ex.Message}", session.DaffyTeeId);
            return;
        }

        var (fingerprint, text) = Fingerprint.Of(recorded.Kind, recorded.Sql, recorded.Calls.Select(c => (c.Proc, c.Sql)).ToList(),
                                                 recorded.Transaction?.Op, recorded.BulkTable, recorded.GatewayCommand);

        if (!_opened) await OpenAsync(cancellationToken);
        if (_notReplayed is not null || _connection is null || _connection.IsBroken)
        {
            context.Emit(NotReplayed(recorded, fingerprint, text, _notReplayed ?? "the session's connection was closed"), 0);
            return;
        }

        var message = Build(recorded, out bool approximate, out string? reason);
        if (message is null)
        {
            context.Emit(NotReplayed(recorded, fingerprint, text, reason ?? "could not be rebuilt"), 0);
            return;
        }

        if (message.Type != TdsConstants.PacketType.BulkLoad && context.Options.PatchDescriptors)
            RequestPatcher.PatchDescriptor(message.Payload, _connection.TransactionDescriptor);
        var handleUse = message.Type == TdsConstants.PacketType.Rpc && context.Options.MapHandles
            ? RequestPatcher.PatchHandles(message.Payload, _handles)
            : HandleUse.None;

        // A cancel replayed as a cancel: an attention after as long as the application waited.
        var timeout = context.Options.ReplayCancels && recorded.Response.Status == "cancelled" && recorded.DurationMs is double waited
            ? TimeSpan.FromMilliseconds(Math.Max(1, waited))
            : context.Options.RequestTimeout;
        var execution = await _connection.ExecuteAsync(message, timeout, cancellationToken, _handles.ColumnsOf(handleUse.Cursor));
        _handles.Learn(handleUse.Outputs, recorded.Response, execution.Response);

        // Lateness behind the recorded schedule, split by cause.
        double? lag = null, lagServer = null, lagHost = null;
        if (due is { } dueAt)
        {
            double total = Math.Max(0, Ms(dueAt, execution.StartTimestamp));
            double waitingOnServer = Math.Clamp(Ms(dueAt, _idleSince), 0, total);
            (lag, lagServer, lagHost) = (total, waitingOnServer, total - waitingOnServer);
        }
        _idleSince = Stopwatch.GetTimestamp();

        var (outcome, rowsDiffer) = Comparison.Compare(recorded.Response, execution, context.Options.RecordedResponseKb, approximate);
        bool databaseDiffers = recorded.Session.Database is { } database && _connection.Database is { } current
                               && !string.Equals(database, current, StringComparison.OrdinalIgnoreCase);

        context.Emit(new ResultRecord
        {
            Session = session.DaffyTeeId,
            SessionKey = session.Key,
            Seq = recorded.Seq,
            RecordedStart = recorded.StartUtc,
            Fingerprint = fingerprint,
            FingerprintText = text,
            Kind = recorded.Kind,
            Proc = recorded.Calls.Count > 0 ? recorded.Calls[0].Proc : null,
            Approximate = approximate,
            Outcome = outcome,
            RowsDiffer = rowsDiffer,
            Recorded = recorded.Response,
            RecordedDurationMs = recorded.DurationMs,
            Replay = execution,
            LagMs = lag,
            LagServerMs = lagServer,
            LagHostMs = lagHost,
            DatabaseDiffers = databaseDiffers,
        }, execution.StartTimestamp);

        if (execution.Status == "failed")
        {
            _notReplayed = $"its connection broke earlier: {execution.Failure}";
            context.SessionBroke();
            Log.Warn("session.broken", $"{session.Describe()}: the connection broke at seq {recorded.Seq} ({execution.Failure}); " +
                                       "its remaining requests are not replayed", session.DaffyTeeId);
        }
    }

    /// <summary>
    /// The request for the wire. Raw bytes are used as recorded; without them a batch is rebuilt
    /// from its text and a transaction-manager request from its fields, both exactly.
    /// </summary>
    private RequestMessage? Build(RecordedRequest recorded, out bool approximate, out string? reason)
    {
        approximate = false;
        reason = null;

        byte type = recorded.Kind switch
        {
            "batch" => TdsConstants.PacketType.SqlBatch,
            "rpc" => TdsConstants.PacketType.Rpc,
            "txn" => TdsConstants.PacketType.TransactionManager,
            "bulk" => TdsConstants.PacketType.BulkLoad,
            _ => 0,
        };
        if (type == 0)
        {
            reason = $"a {recorded.Kind} request cannot be replayed";
            return null;
        }

        if (recorded.Raw is { } raw) return new RequestMessage(type, raw, recorded.ResetConnection);

        switch (recorded.Kind)
        {
            case "batch" when recorded.Sql is not null:
                return new RequestMessage(type, RequestBuilder.Batch(recorded.Sql), recorded.ResetConnection);

            case "txn" when recorded.Transaction is { } t
                            && RequestBuilder.Transaction(t.Op, t.Name, t.Isolation, t.BeginsNew) is { } payload:
                return new RequestMessage(type, payload, recorded.ResetConnection);

            case "rpc" when context.Options.AllowApproximate && SqlText.Render(recorded, h => _preparedDeclarations.GetValueOrDefault(h)) is { } sql:
                RememberPrepared(recorded);
                approximate = true;
                return new RequestMessage(TdsConstants.PacketType.SqlBatch, RequestBuilder.Batch(sql), recorded.ResetConnection);

            default:
                reason = recorded.Kind != "rpc" ? "the request could not be rebuilt"
                    : context.Options.AllowApproximate ? "a procedure call without raw bytes that cannot be rendered as T-SQL (a cursor call, or a statement prepared before the recording)"
                    : "a procedure call without raw bytes";
                return null;
        }
    }

    /// <summary>A prepare's declarations, under the handle the recording says it was given.</summary>
    private void RememberPrepared(RecordedRequest recorded)
    {
        foreach (var (handle, declarations) in SqlText.Prepared(recorded)) _preparedDeclarations[handle] = declarations;
    }

    private ResultRecord NotReplayed(RecordedRequest recorded, string fingerprint, string text, string reason) => new()
    {
        Session = session.DaffyTeeId,
        SessionKey = session.Key,
        Seq = recorded.Seq,
        RecordedStart = recorded.StartUtc,
        Fingerprint = fingerprint,
        FingerprintText = text,
        Kind = recorded.Kind,
        Proc = recorded.Calls.Count > 0 ? recorded.Calls[0].Proc : null,
        Outcome = Outcome.NotReplayed,
        Recorded = recorded.Response,
        RecordedDurationMs = recorded.DurationMs,
        Reason = Simplify(reason),
    };

    /// <summary>Reasons are grouped in the report, so the variable part (a server's message) goes.</summary>
    private static string Simplify(string reason) => reason.StartsWith("login failed", StringComparison.Ordinal) ? "login failed"
        : reason.StartsWith("its connection broke", StringComparison.Ordinal) ? "the session's connection broke earlier"
        : reason;

    private static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;
}
