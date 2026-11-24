using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using DaffyReplay.Target;
using DaffyReplay.Tds;

namespace DaffyReplay.Replay;

/// <summary>A request ready for the wire: its packet type, its payload, and whether it resets a pooled connection.</summary>
internal sealed record RequestMessage(byte Type, byte[] Payload, bool ResetConnection = false);

/// <summary>How one request went on the replay target.</summary>
internal sealed class Execution
{
    /// <summary>ok, error, timeout, or failed (the connection broke).</summary>
    public string Status { get; set; } = "ok";

    public ResponseSummary Response { get; } = new();

    /// <summary>From the request's first byte on the wire to the response's last.</summary>
    public double DurationMs { get; set; }

    /// <summary>From the request's last byte to the response's first: roughly, the server's think time.</summary>
    public double? FirstByteMs { get; set; }

    /// <summary>Stopwatch timestamp of the request's first byte.</summary>
    public long StartTimestamp { get; set; }

    /// <summary>Why the connection broke, when it did.</summary>
    public string? Failure { get; set; }
}

/// <summary>
/// One replayed session's connection to the target: sends a request, walks its response to the end,
/// and keeps the state a replay has to carry from one request to the next.
///
/// The transaction descriptor is the important part. Every request names, in its ALL_HEADERS, the
/// transaction it runs in, by the 8-byte id the server gave out when the transaction began. A
/// replay server gives out different ids, so this follows the transaction ENVCHANGE tokens in each
/// response and <see cref="RequestPatcher"/> writes the current id into each request before it is
/// sent - exactly what SqlClient does for the application.
///
/// A request that runs past its timeout is cancelled the way a client cancels: an ATTENTION packet,
/// then reading on until the server acknowledges it with a DONE carrying the attention bit. The
/// connection is then still usable, as the application's would be.
/// </summary>
internal sealed class ReplayConnection : IAsyncDisposable
{
    private readonly TargetConnection _connection;
    private readonly TdsResponseStream _responses;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _packetSize;
    private bool _broken;

    private ReplayConnection(TargetConnection connection)
    {
        _connection = connection;
        _responses = new TdsResponseStream(connection.Stream);
        _packetSize = connection.PacketSize;
        Database = connection.Login.Database;
    }

    public static async Task<ReplayConnection> OpenAsync(TargetEndpoint endpoint, LoginOptions options, CancellationToken cancellationToken)
    {
        var connection = await TargetConnector.ConnectAsync(endpoint, TargetConnector.CreateLogin(endpoint, options),
                                                           mars: false, cancellationToken);
        return new ReplayConnection(connection);
    }

    public TargetConnection Target => _connection;

    /// <summary>The transaction the next request runs in; zero outside a transaction.</summary>
    public ulong TransactionDescriptor { get; private set; }

    /// <summary>The session's current database, as the server last reported it.</summary>
    public string? Database { get; private set; }

    /// <summary>
    /// The server acknowledged column encryption at login: result metadata then carries a CEK table,
    /// and so must a bulk load's.
    /// </summary>
    public bool ColumnEncryption => _connection.Login.AcknowledgedFeatures.Contains(TdsConstants.FeatureId.ColumnEncryption);

    /// <summary>The connection broke; nothing more can be sent on it.</summary>
    public bool IsBroken => _broken;

    public ushort Spid => _connection.Spid;

    public async Task<Execution> ExecuteAsync(RequestMessage request, TimeSpan? timeout, CancellationToken cancellationToken,
                                              ResponseWalker.ColumnShape[]? inheritedColumns = null)
    {
        var execution = new Execution();
        if (_broken)
        {
            execution.Status = "failed";
            execution.Failure = "the connection had already broken";
            return execution;
        }

        // 0 running, 1 attention sent, 2 finished: whoever moves it first decides.
        int state = 0;
        Timer? timer = null;
        bool timedOut = false;

        try
        {
            _responses.BeginMessage();
            execution.StartTimestamp = Stopwatch.GetTimestamp();
            await WriteMessageAsync(request.Type, request.Payload, request.ResetConnection, cancellationToken);
            long requestEnd = Stopwatch.GetTimestamp();

            if (timeout is { } limit)
            {
                timer = new Timer(_ =>
                {
                    if (Interlocked.CompareExchange(ref state, 1, 0) == 0) _ = SendAttentionAsync();
                }, null, limit, Timeout.InfiniteTimeSpan);
            }

            await ResponseWalker.WalkAsync(_responses, execution.Response, TransactionDescriptor, ColumnEncryption, cancellationToken, inheritedColumns);
            long responseEnd = Stopwatch.GetTimestamp();

            if (timer is not null) await timer.DisposeAsync();
            timer = null;

            // An attention sent at any point must be acknowledged before the connection is used again.
            // The acknowledgement can end this response, or come as a message of its own after it.
            if (Interlocked.CompareExchange(ref state, 2, 0) == 1)
            {
                timedOut = true;
                while (!execution.Response.AttentionAcknowledged)
                {
                    _responses.BeginMessage();
                    await ResponseWalker.WalkAsync(_responses, execution.Response,
                        execution.Response.TransactionDescriptor ?? TransactionDescriptor, ColumnEncryption, cancellationToken);
                }
                responseEnd = Stopwatch.GetTimestamp();
            }

            Apply(execution.Response);

            execution.DurationMs = Stopwatch.GetElapsedTime(execution.StartTimestamp, responseEnd).TotalMilliseconds;
            if (_responses.FirstByteTimestamp >= requestEnd)
                execution.FirstByteMs = Stopwatch.GetElapsedTime(requestEnd, _responses.FirstByteTimestamp).TotalMilliseconds;

            execution.Status = timedOut ? "timeout" : execution.Response.HasError ? "error" : "ok";

            // Severity 20 and up ends the session on the server: the connection is gone.
            if (execution.Response.Errors.Any(e => e.Severity >= 20))
            {
                _broken = true;
                execution.Failure = "the server ended the session (an error of severity 20 or higher)";
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException
                                   or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException)
        {
            _broken = true;
            execution.Status = "failed";
            execution.Failure = ex.Message;
            execution.DurationMs = Stopwatch.GetElapsedTime(execution.StartTimestamp).TotalMilliseconds;
        }
        finally
        {
            if (timer is not null) await timer.DisposeAsync();
        }

        return execution;
    }

    private void Apply(ResponseSummary response)
    {
        if (response.TransactionDescriptor is { } descriptor) TransactionDescriptor = descriptor;

        // A reset puts the session back in its login database without an ENVCHANGE saying so, as
        // SqlClient knows; a USE in the same request is announced after it, and wins.
        if (response.ResetAcknowledged) Database = _connection.Login.Database;
        if (response.Database is { } database) Database = database;
        if (response.PacketSize is { } size and >= 512) _packetSize = size;
    }

    /// <summary>
    /// Frames a message at the negotiated packet size and writes it packet by packet. Each packet
    /// goes to the stream in a write of its own, so under TLS each travels in a TLS record of its
    /// own: SQL Server drops a connection whose records split packets, and SqlClient never sends one.
    /// The reset flag goes on the first packet only, as SqlClient sets it; the SPID field is zero.
    /// </summary>
    private async Task WriteMessageAsync(byte type, byte[] payload, bool reset, CancellationToken cancellationToken)
    {
        int maxPayload = _packetSize - TdsConstants.HeaderLength;
        int packets = Math.Max(1, (payload.Length + maxPayload - 1) / maxPayload);
        byte[] packet = ArrayPool<byte>.Shared.Rent(_packetSize);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            int offset = 0;
            for (int i = 0; i < packets; i++)
            {
                int chunk = Math.Min(maxPayload, payload.Length - offset);
                int length = TdsConstants.HeaderLength + chunk;
                byte status = i == packets - 1 ? TdsConstants.PacketStatus.EndOfMessage : TdsConstants.PacketStatus.Normal;
                if (reset && i == 0) status |= TdsConstants.PacketStatus.ResetConnection;

                packet[0] = type;
                packet[1] = status;
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)length);
                packet[4] = 0;
                packet[5] = 0;
                packet[6] = unchecked((byte)(i + 1));
                packet[7] = 0;
                payload.AsSpan(offset, chunk).CopyTo(packet.AsSpan(TdsConstants.HeaderLength));
                offset += chunk;

                await _connection.Stream.WriteAsync(packet.AsMemory(0, length), cancellationToken);
            }
            await _connection.Stream.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
            ArrayPool<byte>.Shared.Return(packet);
        }
    }

    /// <summary>An ATTENTION: a header with no payload. Sent while a response is being read, so writes take a lock.</summary>
    private async Task SendAttentionAsync()
    {
        byte[] attention = [TdsConstants.PacketType.Attention, TdsConstants.PacketStatus.EndOfMessage, 0, 8, 0, 0, 1, 0];
        try
        {
            await _writeLock.WaitAsync();
            try
            {
                await _connection.Stream.WriteAsync(attention);
                await _connection.Stream.FlushAsync();
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The read side will find out.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _responses.Dispose();
        await _connection.DisposeAsync();
        _writeLock.Dispose();
    }
}
