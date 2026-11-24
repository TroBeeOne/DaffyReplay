using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;

namespace DaffyReplay.Tds;

/// <summary>
/// One response message, read as a continuous token stream across its packets, without ever holding
/// the whole of it: a result set of a million rows passes through a buffer of a few kilobytes.
///
/// Packet headers are stripped as they arrive; the message ends with the packet whose status has
/// the EOM bit. A reader asks for what it needs next with <see cref="EnsureAsync"/> (bytes it will
/// look at, contiguous in <see cref="Span"/>) or <see cref="SkipAsync"/> (bytes it will not, which
/// are read and dropped however many there are). Both complete synchronously while the buffer
/// holds enough, which is nearly always, so walking rows column by column costs little.
///
/// This is the piece DaffyTee does not have: DaffyTee's ResponseReader decodes a response kept
/// whole because it was small, and gives up on anything larger. A replay must walk every response
/// to its end - a transaction begun in the first statement of a batch that returns a large result
/// is announced before the rows, and missing it would break every request that follows.
/// </summary>
internal sealed class TdsResponseStream(Stream stream) : IDisposable
{
    private const int InitialBuffer = 16 * 1024;

    private readonly byte[] _header = new byte[TdsConstants.HeaderLength];
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(InitialBuffer);
    private int _start, _end;
    private int _packetRemaining;
    private bool _lastPacket, _inMessage;

    public Stream Stream { get; set; } = stream;

    /// <summary>Bytes of payload read for the current message, headers excluded.</summary>
    public long Bytes { get; private set; }

    public int Packets { get; private set; }

    /// <summary>Stopwatch timestamp when the first packet header of the message arrived.</summary>
    public long FirstByteTimestamp { get; private set; }

    /// <summary>The packet type of the message (0x04, TABULAR_RESULT, for any response).</summary>
    public byte MessageType { get; private set; }

    /// <summary>The bytes buffered and not yet consumed.</summary>
    public ReadOnlySpan<byte> Span => _buffer.AsSpan(_start, _end - _start);

    public int Available => _end - _start;

    /// <summary>The whole message has been read and consumed.</summary>
    public bool AtEnd => _inMessage && _lastPacket && _packetRemaining == 0 && _start == _end;

    /// <summary>Starts reading the next message. Anything left of the previous one must have been consumed.</summary>
    public void BeginMessage()
    {
        if (_inMessage && !AtEnd) throw new InvalidOperationException("the previous response was not read to its end");
        _start = _end = 0;
        _packetRemaining = 0;
        _lastPacket = false;
        _inMessage = true;
        Bytes = 0;
        Packets = 0;
        FirstByteTimestamp = 0;
    }

    public void Advance(int count)
    {
        if (count > _end - _start) throw new ArgumentOutOfRangeException(nameof(count));
        _start += count;
    }

    /// <summary>
    /// Makes at least <paramref name="count"/> bytes available in <see cref="Span"/>. False means the
    /// message ended first - for a well-formed response, a token cut short.
    /// </summary>
    public ValueTask<bool> EnsureAsync(int count, CancellationToken cancellationToken)
    {
        if (_end - _start >= count) return ValueTask.FromResult(true);
        return EnsureSlowAsync(count, cancellationToken);
    }

    private async ValueTask<bool> EnsureSlowAsync(int count, CancellationToken cancellationToken)
    {
        MakeRoom(count);
        while (_end - _start < count)
        {
            if (!await FillAsync(cancellationToken)) return false;
        }
        return true;
    }

    /// <summary>Consumes <paramref name="count"/> bytes without keeping them. False means the message ended first.</summary>
    public ValueTask<bool> SkipAsync(long count, CancellationToken cancellationToken)
    {
        if (_end - _start >= count)
        {
            _start += (int)count;
            return ValueTask.FromResult(true);
        }
        return SkipSlowAsync(count, cancellationToken);
    }

    private async ValueTask<bool> SkipSlowAsync(long count, CancellationToken cancellationToken)
    {
        long remaining = count - (_end - _start);
        _start = _end = 0;
        while (remaining > 0)
        {
            if (!await FillAsync(cancellationToken)) return false;
            int take = (int)Math.Min(remaining, _end - _start);
            _start += take;
            remaining -= take;
            if (_start == _end) _start = _end = 0;
        }
        return true;
    }

    /// <summary>Reads and drops whatever is left of the message - after a token the walker cannot read, say.</summary>
    public async ValueTask SkipToEndAsync(CancellationToken cancellationToken)
    {
        _start = _end = 0;
        while (await FillAsync(cancellationToken)) _start = _end = 0;
    }

    /// <summary>Room for <paramref name="count"/> contiguous bytes from <see cref="_start"/>: compact, then grow.</summary>
    private void MakeRoom(int count)
    {
        if (_start > 0 && _buffer.Length - _start < count)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }

        if (_buffer.Length < count)
        {
            var bigger = ArrayPool<byte>.Shared.Rent(Math.Max(count, _buffer.Length * 2));
            Buffer.BlockCopy(_buffer, _start, bigger, 0, _end - _start);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = bigger;
            _end -= _start;
            _start = 0;
        }
    }

    /// <summary>Reads more of the message into the buffer. False once the message has been read whole.</summary>
    private async ValueTask<bool> FillAsync(CancellationToken cancellationToken)
    {
        while (_packetRemaining == 0)
        {
            if (_lastPacket) return false;
            await ReadHeaderAsync(cancellationToken);
        }

        if (_end == _buffer.Length)
        {
            if (_start == 0) MakeRoom(_buffer.Length * 2);
            else MakeRoom(_end - _start + 1);
        }

        int want = Math.Min(_packetRemaining, _buffer.Length - _end);
        int read = await Stream.ReadAsync(_buffer.AsMemory(_end, want), cancellationToken);
        if (read == 0) throw new IOException("the target closed the connection in the middle of a response");

        _end += read;
        _packetRemaining -= read;
        Bytes += read;
        return true;
    }

    private async ValueTask ReadHeaderAsync(CancellationToken cancellationToken)
    {
        int filled = 0;
        while (filled < TdsConstants.HeaderLength)
        {
            int read = await Stream.ReadAsync(_header.AsMemory(filled), cancellationToken);
            if (read == 0)
                throw new IOException(Packets == 0
                    ? "the target closed the connection"
                    : "the target closed the connection in the middle of a response");
            filled += read;
            if (FirstByteTimestamp == 0) FirstByteTimestamp = Stopwatch.GetTimestamp();
        }

        int length = BinaryPrimitives.ReadUInt16BigEndian(_header.AsSpan(2));
        if (length < TdsConstants.HeaderLength)
            throw new InvalidDataException($"the target sent a packet that claims {length} bytes, shorter than its header");

        if (Packets == 0) MessageType = _header[0];
        Packets++;
        _packetRemaining = length - TdsConstants.HeaderLength;
        _lastPacket = (_header[1] & TdsConstants.PacketStatus.EndOfMessage) != 0;
    }

    public void Dispose()
    {
        if (_buffer.Length > 0) ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
    }
}
