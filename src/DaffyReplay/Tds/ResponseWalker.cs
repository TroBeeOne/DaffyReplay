using System.Buffers.Binary;
using System.Diagnostics;

namespace DaffyReplay.Tds;

/// <summary>An OUTPUT parameter as it came back in a RETURNVALUE token.</summary>
internal sealed record OutputValue(int Ordinal, string Name, object? Value)
{
    /// <summary>Which call of a batched RPC returned it: the DONEPROC tokens seen before it.</summary>
    public int Call { get; init; }

    /// <summary>The key DaffyTee uses in an event's response.output: the name, or "#ordinal".</summary>
    public string Key => Name.Length > 0 ? Name : $"#{Ordinal}";
}

/// <summary>
/// What one response said, beyond its data - the same things DaffyTee's events record about a
/// response, worked out the same way, so a recording and its replay compare like for like.
/// </summary>
internal sealed class ResponseSummary
{
    public const int MaxErrors = 50;
    public const int MaxMessages = 20;

    public List<ServerMessage> Errors { get; } = [];
    public List<ServerMessage> Messages { get; } = [];
    public int MessageCount { get; set; }

    /// <summary>Every DONE that carried a count, in order: one per statement that counted rows.</summary>
    public List<ulong> RowCounts { get; } = [];

    /// <summary>Rows in each result set returned, in order.</summary>
    public List<long> ResultSets { get; } = [];

    public int? ReturnStatus { get; set; }
    public List<OutputValue> Outputs { get; } = [];

    /// <summary>The database the session moved to, if it changed.</summary>
    public string? Database { get; set; }

    /// <summary>Transaction boundaries the server reported, as DaffyTee writes them: "begin 0x...", "commit", "rollback".</summary>
    public List<string> Transactions { get; } = [];

    /// <summary>The transaction descriptor in force once the response ended; null when the response did not change it.</summary>
    public ulong? TransactionDescriptor { get; set; }

    public bool ResetAcknowledged { get; set; }
    public int? PacketSize { get; set; }

    /// <summary>The last DONE, DONEPROC or DONEINPROC token, and its status: the response's final word.</summary>
    public byte DoneToken { get; set; }
    public ushort DoneStatus { get; set; }
    public ulong DoneRowCount { get; set; }

    /// <summary>A DONE carried the attention bit: the server acknowledged a cancel.</summary>
    public bool AttentionAcknowledged { get; set; }

    public long Bytes { get; set; }
    public int Packets { get; set; }

    /// <summary>Set when the walk stopped early (a token or type it cannot read); the rest was read and dropped.</summary>
    public string? Problem { get; set; }

    /// <summary>
    /// The last column metadata the response carried: for a cursor's open, the shape its later
    /// fetches' rows have, which those responses do not repeat.
    /// </summary>
    internal ResponseWalker.ColumnShape[]? Columns { get; set; }

    /// <summary>As DaffyTee decides it: an ERROR token, or the error bits on the final DONE.</summary>
    public bool HasError =>
        Errors.Count > 0 || (DoneStatus & (TdsConstants.DoneStatus.Error | TdsConstants.DoneStatus.ServerError)) != 0;

    /// <summary>Rows affected or returned by the last statement, when the final DONE carries a count.</summary>
    public ulong? RowCount => (DoneStatus & TdsConstants.DoneStatus.Count) != 0 ? DoneRowCount : null;
}

/// <summary>
/// Walks a response's token stream to its end, keeping none of the data: errors, messages, counts,
/// return values, output parameters and environment changes, as DaffyTee's ResponseReader does -
/// but streaming, over <see cref="TdsResponseStream"/>, so a response of any size is walked whole.
///
/// Rows have no length prefix, so the only way past one is to read each value's length. That is
/// done from the column shapes worked out once per result set, synchronously over the buffer for a
/// row that is all there, and value by value (awaiting more bytes as needed) for one that spans
/// packets. Tokens without a length prefix and with structure - COLMETADATA, RETURNVALUE - are
/// parsed from the buffer, which grows until the whole token is in it.
///
/// A token or type it cannot walk ends the walk with <see cref="ResponseSummary.Problem"/> set;
/// the rest of the message is read and dropped, so the connection stays usable.
/// </summary>
internal static class ResponseWalker
{
    private const ulong PlpNull = 0xFFFFFFFFFFFFFFFF;

    /// <param name="inherited">
    /// The metadata rows take when the response says "no new metadata" (COLMETADATA 0xFFFF) before
    /// any of its own: a server cursor's fetches, whose shape came with the cursor's open. Without
    /// it such a result set counts, as in DaffyTee, but its rows cannot be walked.
    /// </param>
    public static async Task WalkAsync(TdsResponseStream stream, ResponseSummary summary, ulong descriptor, bool columnEncryption,
                                       CancellationToken cancellationToken, ColumnShape[]? inherited = null)
    {
        var state = new WalkState();
        ColumnShape[]? ParseColumns(ReadOnlySpan<byte> span, out int consumed)
        {
            var cursor = new TdsCursor(span);
            var columns = ReadColumnMetadata(ref cursor, columnEncryption);
            consumed = cursor.Position;
            return columns;
        }
        try
        {
            while (await stream.EnsureAsync(1, cancellationToken))
            {
                byte token = stream.Span[0];
                stream.Advance(1);

                switch (token)
                {
                    case TdsConstants.Token.ColMetadata:
                    {
                        var columns = await ParseAsync(stream, ParseColumns, cancellationToken);
                        if (state.Columns is not null) summary.ResultSets.Add(state.Rows);
                        // 0xFFFF columns means "same as before", sent with NoMetaData: keep the old shape,
                        // or take the one inherited from the cursor's open.
                        if (columns is not null) summary.Columns = columns;
                        state.SetColumns(columns ?? state.LastColumns ?? inherited ?? []);
                        state.Rows = 0;
                        break;
                    }

                    case TdsConstants.Token.Row:
                    case TdsConstants.Token.NbcRow:
                        if (state.Columns is null) throw new InvalidDataException("a row arrived before its column metadata");
                        if (state.Columns.Length == 0) throw new InvalidDataException("a row arrived with no column metadata to read it by (a cursor fetch whose open was not seen)");
                        await SkipRowAsync(stream, state, token == TdsConstants.Token.NbcRow, cancellationToken);
                        state.Rows++;
                        break;

                    case TdsConstants.Token.Done:
                    case TdsConstants.Token.DoneProc:
                    case TdsConstants.Token.DoneInProc:
                    {
                        await Require(stream, 12, cancellationToken);
                        var span = stream.Span;
                        ushort status = BinaryPrimitives.ReadUInt16LittleEndian(span);
                        ulong count = BinaryPrimitives.ReadUInt64LittleEndian(span[4..]);
                        stream.Advance(12);

                        if ((status & TdsConstants.DoneStatus.Count) != 0) summary.RowCounts.Add(count);
                        if (token == TdsConstants.Token.DoneProc) state.Calls++;
                        if ((status & TdsConstants.DoneStatus.Attention) != 0) summary.AttentionAcknowledged = true;
                        if (state.Columns is not null && token != TdsConstants.Token.DoneProc)
                        {
                            summary.ResultSets.Add(state.Rows);
                            state.ClearColumns();
                            state.Rows = 0;
                        }

                        summary.DoneToken = token;
                        summary.DoneStatus = status;
                        summary.DoneRowCount = count;
                        break;
                    }

                    case TdsConstants.Token.Error:
                    case TdsConstants.Token.Info:
                    {
                        var message = await ParseLengthPrefixedAsync(stream, ReadMessage, cancellationToken);
                        if (token == TdsConstants.Token.Error)
                        {
                            if (summary.Errors.Count < ResponseSummary.MaxErrors) summary.Errors.Add(message);
                        }
                        else
                        {
                            summary.MessageCount++;
                            if (summary.Messages.Count < ResponseSummary.MaxMessages) summary.Messages.Add(message);
                        }
                        break;
                    }

                    case TdsConstants.Token.EnvChange:
                        descriptor = await ParseLengthPrefixedAsync(stream,
                            (ReadOnlySpan<byte> body) => ReadEnvChange(body, summary, descriptor), cancellationToken);
                        break;

                    case TdsConstants.Token.ReturnStatus:
                        await Require(stream, 4, cancellationToken);
                        summary.ReturnStatus = BinaryPrimitives.ReadInt32LittleEndian(stream.Span);
                        stream.Advance(4);
                        break;

                    case TdsConstants.Token.ReturnValue:
                        summary.Outputs.Add(await ParseAsync(stream, ParseReturnValue, cancellationToken) with { Call = state.Calls });
                        break;

                    case TdsConstants.Token.Order:
                    case TdsConstants.Token.TabName:
                    case TdsConstants.Token.ColInfo:
                    case TdsConstants.Token.LoginAck:
                    {
                        await Require(stream, 2, cancellationToken);
                        int length = BinaryPrimitives.ReadUInt16LittleEndian(stream.Span);
                        stream.Advance(2);
                        await SkipRequired(stream, length, cancellationToken);
                        break;
                    }

                    case TdsConstants.Token.SessionState:
                    {
                        await Require(stream, 4, cancellationToken);
                        long length = BinaryPrimitives.ReadUInt32LittleEndian(stream.Span);
                        stream.Advance(4);
                        await SkipRequired(stream, length, cancellationToken);
                        break;
                    }

                    case TdsConstants.Token.Offset:
                        await SkipRequired(stream, 4, cancellationToken);
                        break;

                    default:
                        summary.Problem = $"stopped at token 0x{token:X2} ({TdsConstants.TokenName(token)})";
                        await stream.SkipToEndAsync(cancellationToken);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is TdsUnsupportedException or InvalidDataException)
        {
            summary.Problem = $"stopped part-way: {ex.Message}";
            await stream.SkipToEndAsync(cancellationToken);
        }
        finally
        {
            summary.Bytes += stream.Bytes;
            summary.Packets += stream.Packets;
        }

        if (state.Columns is not null) summary.ResultSets.Add(state.Rows);
        summary.TransactionDescriptor = descriptor;
    }

    // ---- rows ----------------------------------------------------------------------------------

    /// <summary>How a column's value is laid out in a ROW, which is all a walker that skips it needs.</summary>
    internal enum Shape : byte { Fixed, ByteLength, UShortLength, Plp, TextPointer, Variant }

    internal readonly record struct ColumnShape(Shape Shape, int Length);

    private sealed class WalkState
    {
        public ColumnShape[]? Columns;
        public long Rows;
        public int Calls;
        public byte[] Bitmap = new byte[16];

        /// <summary>The last real metadata in this response, for a later NoMetaData in the same one.</summary>
        public ColumnShape[]? LastColumns;

        public void SetColumns(ColumnShape[] columns)
        {
            Columns = columns;
            if (columns.Length > 0) LastColumns = columns;
            int bitmapBytes = (columns.Length + 7) / 8;
            if (Bitmap.Length < bitmapBytes) Bitmap = new byte[bitmapBytes];
        }

        public void ClearColumns() => Columns = null;
    }

    internal static ColumnShape ShapeOf(TypeInfo type) => type.Type switch
    {
        0x1F => new(Shape.Fixed, 0),
        0x30 or 0x32 => new(Shape.Fixed, 1),
        0x34 => new(Shape.Fixed, 2),
        0x38 or 0x3A or 0x3B or 0x7A => new(Shape.Fixed, 4),
        0x3C or 0x3D or 0x3E or 0x7F => new(Shape.Fixed, 8),
        0x24 or 0x26 or 0x68 or 0x6D or 0x6E or 0x6F or 0x6A or 0x6C or 0x37 or 0x3F
            or 0x28 or 0x29 or 0x2A or 0x2B or 0x2F or 0x27 or 0x2D or 0x25 => new(Shape.ByteLength, 0),
        0xA7 or 0xAF or 0xE7 or 0xEF or 0xA5 or 0xAD => type.IsPlp ? new(Shape.Plp, 0) : new(Shape.UShortLength, 0),
        0xF1 or 0xF0 => new(Shape.Plp, 0),
        0x23 or 0x63 or 0x22 => new(Shape.TextPointer, 0),
        0x62 => new(Shape.Variant, 0),
        _ => throw new TdsUnsupportedException($"column type 0x{type.Type:X2}"),
    };

    private static async ValueTask SkipRowAsync(TdsResponseStream stream, WalkState state, bool nullBitmap, CancellationToken cancellationToken)
    {
        var columns = state.Columns!;

        // Most rows are small and already buffered whole: skip them without a single await.
        if (TrySkipRow(stream.Span, columns, nullBitmap, out int consumed))
        {
            stream.Advance(consumed);
            return;
        }

        int bitmapBytes = nullBitmap ? (columns.Length + 7) / 8 : 0;
        if (nullBitmap)
        {
            await Require(stream, bitmapBytes, cancellationToken);
            stream.Span[..bitmapBytes].CopyTo(state.Bitmap);
            stream.Advance(bitmapBytes);
        }

        for (int i = 0; i < columns.Length; i++)
        {
            if (nullBitmap && (state.Bitmap[i / 8] & (1 << (i % 8))) != 0) continue;
            await SkipValueAsync(stream, columns[i], cancellationToken);
        }
    }

    private static bool TrySkipRow(ReadOnlySpan<byte> span, ColumnShape[] columns, bool nullBitmap, out int consumed)
    {
        consumed = 0;
        int position = 0;
        ReadOnlySpan<byte> bitmap = default;
        if (nullBitmap)
        {
            int bitmapBytes = (columns.Length + 7) / 8;
            if (span.Length < bitmapBytes) return false;
            bitmap = span[..bitmapBytes];
            position = bitmapBytes;
        }

        for (int i = 0; i < columns.Length; i++)
        {
            if (nullBitmap && (bitmap[i / 8] & (1 << (i % 8))) != 0) continue;
            if (!TrySkipValue(span, ref position, columns[i])) return false;
        }

        consumed = position;
        return true;
    }

    private static bool TrySkipValue(ReadOnlySpan<byte> span, ref int position, ColumnShape column)
    {
        int remaining = span.Length - position;
        switch (column.Shape)
        {
            case Shape.Fixed:
                if (remaining < column.Length) return false;
                position += column.Length;
                return true;

            case Shape.ByteLength:
            {
                if (remaining < 1) return false;
                int length = span[position];
                if (remaining < 1 + length) return false;
                position += 1 + length;
                return true;
            }

            case Shape.UShortLength:
            {
                if (remaining < 2) return false;
                int length = BinaryPrimitives.ReadUInt16LittleEndian(span[position..]);
                if (length == 0xFFFF) { position += 2; return true; }
                if (remaining < 2 + length) return false;
                position += 2 + length;
                return true;
            }

            case Shape.Variant:
            {
                if (remaining < 4) return false;
                long length = BinaryPrimitives.ReadUInt32LittleEndian(span[position..]);
                if (remaining < 4 + length) return false;
                position += 4 + (int)length;
                return true;
            }

            case Shape.TextPointer:
            {
                if (remaining < 1) return false;
                int pointer = span[position];
                if (pointer == 0) { position += 1; return true; }
                int at = position + 1 + pointer + 8;
                if (span.Length < at + 4) return false;
                long length = BinaryPrimitives.ReadUInt32LittleEndian(span[at..]);
                if (span.Length < at + 4 + length) return false;
                position = at + 4 + (int)length;
                return true;
            }

            case Shape.Plp:
            {
                if (remaining < 8) return false;
                ulong total = BinaryPrimitives.ReadUInt64LittleEndian(span[position..]);
                int at = position + 8;
                if (total != PlpNull)
                {
                    while (true)
                    {
                        if (span.Length < at + 4) return false;
                        long chunk = BinaryPrimitives.ReadUInt32LittleEndian(span[at..]);
                        at += 4;
                        if (chunk == 0) break;
                        if (span.Length < at + chunk) return false;
                        at += (int)chunk;
                    }
                }
                position = at;
                return true;
            }
        }
        return false;
    }

    private static async ValueTask SkipValueAsync(TdsResponseStream stream, ColumnShape column, CancellationToken cancellationToken)
    {
        switch (column.Shape)
        {
            case Shape.Fixed:
                await SkipRequired(stream, column.Length, cancellationToken);
                return;

            case Shape.ByteLength:
            {
                await Require(stream, 1, cancellationToken);
                int length = stream.Span[0];
                stream.Advance(1);
                await SkipRequired(stream, length, cancellationToken);
                return;
            }

            case Shape.UShortLength:
            {
                await Require(stream, 2, cancellationToken);
                int length = BinaryPrimitives.ReadUInt16LittleEndian(stream.Span);
                stream.Advance(2);
                if (length != 0xFFFF) await SkipRequired(stream, length, cancellationToken);
                return;
            }

            case Shape.Variant:
            {
                await Require(stream, 4, cancellationToken);
                long length = BinaryPrimitives.ReadUInt32LittleEndian(stream.Span);
                stream.Advance(4);
                await SkipRequired(stream, length, cancellationToken);
                return;
            }

            case Shape.TextPointer:
            {
                await Require(stream, 1, cancellationToken);
                int pointer = stream.Span[0];
                stream.Advance(1);
                if (pointer == 0) return;
                await SkipRequired(stream, pointer + 8, cancellationToken);   // text pointer, timestamp
                await Require(stream, 4, cancellationToken);
                long length = BinaryPrimitives.ReadUInt32LittleEndian(stream.Span);
                stream.Advance(4);
                await SkipRequired(stream, length, cancellationToken);
                return;
            }

            case Shape.Plp:
            {
                await Require(stream, 8, cancellationToken);
                ulong total = BinaryPrimitives.ReadUInt64LittleEndian(stream.Span);
                stream.Advance(8);
                if (total == PlpNull) return;
                while (true)
                {
                    await Require(stream, 4, cancellationToken);
                    long chunk = BinaryPrimitives.ReadUInt32LittleEndian(stream.Span);
                    stream.Advance(4);
                    if (chunk == 0) return;
                    await SkipRequired(stream, chunk, cancellationToken);
                }
            }
        }
    }

    // ---- structured tokens ---------------------------------------------------------------------

    private delegate T SpanParser<T>(ReadOnlySpan<byte> span, out int consumed);
    private delegate T BodyParser<T>(ReadOnlySpan<byte> body);

    /// <summary>
    /// Parses a token with no length prefix from the buffer, growing it until the whole token is
    /// there. A read past the buffered bytes means "not all here yet", not a malformed token.
    /// </summary>
    private static async ValueTask<T> ParseAsync<T>(TdsResponseStream stream, SpanParser<T> parser, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                var value = parser(stream.Span, out int consumed);
                stream.Advance(consumed);
                return value;
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                int before = stream.Available;
                await stream.EnsureAsync(Math.Max(before * 2, before + 4096), cancellationToken);
                if (stream.Available == before) throw new InvalidDataException("the response ended part-way through a token");
            }
        }
    }

    /// <summary>A token whose USHORT length comes first: the whole body is buffered, then parsed.</summary>
    private static async ValueTask<T> ParseLengthPrefixedAsync<T>(TdsResponseStream stream, BodyParser<T> parser, CancellationToken cancellationToken)
    {
        await Require(stream, 2, cancellationToken);
        int length = BinaryPrimitives.ReadUInt16LittleEndian(stream.Span);
        await Require(stream, 2 + length, cancellationToken);
        var value = parser(stream.Span.Slice(2, length));
        stream.Advance(2 + length);
        return value;
    }

    private static async ValueTask Require(TdsResponseStream stream, int count, CancellationToken cancellationToken)
    {
        if (!await stream.EnsureAsync(count, cancellationToken))
            throw new InvalidDataException("the response ended part-way through a token");
    }

    private static async ValueTask SkipRequired(TdsResponseStream stream, long count, CancellationToken cancellationToken)
    {
        if (!await stream.SkipAsync(count, cancellationToken))
            throw new InvalidDataException("the response ended part-way through a value");
    }

    private const ushort EncryptedColumnFlag = 0x0800;

    /// <summary>
    /// COLMETADATA after its token byte. Null for 0xFFFF, "no new metadata". When the session
    /// negotiated column encryption (Always Encrypted), a CEK table follows the count - usually
    /// empty - and each encrypted column carries its crypto metadata after its TYPE_INFO; its values
    /// are then varbinary ciphertext, which skips like any varbinary. A client's bulk load has the
    /// same layout, which is why <see cref="HasCekTable"/> can tell how a recorded session logged in.
    /// </summary>
    internal static ColumnShape[]? ReadColumnMetadata(ref TdsCursor cursor, bool columnEncryption)
    {
        ushort count = cursor.ReadUInt16();
        if (count == 0xFFFF) return null;

        if (columnEncryption) SkipCekTable(ref cursor);

        var columns = new ColumnShape[count];
        for (int i = 0; i < count; i++)
        {
            cursor.ReadUInt32();                               // UserType
            ushort flags = cursor.ReadUInt16();
            var type = ReadColumnType(ref cursor);
            if (columnEncryption && (flags & EncryptedColumnFlag) != 0) SkipCryptoMetadata(ref cursor);
            cursor.ReadBVarChar();                             // column name
            columns[i] = ShapeOf(type);
        }
        return columns;
    }

    /// <summary>CEK_TABLE: a count, then per key its ids, versions and encrypted values.</summary>
    private static void SkipCekTable(ref TdsCursor cursor)
    {
        ushort keys = cursor.ReadUInt16();
        for (int k = 0; k < keys; k++)
        {
            cursor.Skip(4 + 4 + 4 + 8);                        // database id, CEK id, version, metadata version
            int values = cursor.ReadByte();
            for (int v = 0; v < values; v++)
            {
                cursor.Skip(cursor.ReadUInt16());              // the encrypted key
                cursor.ReadBVarChar();                         // key store
                cursor.ReadUsVarChar();                        // key path
                cursor.ReadBVarChar();                         // algorithm
            }
        }
    }

    /// <summary>CryptoMetaData: CEK ordinal, the plaintext's user type and TYPE_INFO, the algorithm.</summary>
    private static void SkipCryptoMetadata(ref TdsCursor cursor)
    {
        cursor.ReadUInt16();                                   // ordinal into the CEK table
        cursor.ReadUInt32();                                   // user type
        TypeInfoReader.ReadTypeInfo(ref cursor);               // the plaintext's type
        if (cursor.ReadByte() == 0) cursor.ReadBVarChar();     // algorithm, by name when custom
        cursor.ReadByte();                                     // encryption type
        cursor.ReadByte();                                     // normalisation version
    }

    /// <summary>
    /// Whether a recorded bulk load's COLMETADATA carries a CEK table: whether the client that sent it
    /// had negotiated column encryption at login. SqlClient always does; other clients only when
    /// Always Encrypted is switched on. The server reads bulk metadata by what was negotiated, so a
    /// replay of the load has to log in the same way. Null when the payload does not say.
    /// </summary>
    public static bool? HasCekTable(ReadOnlySpan<byte> bulk)
    {
        if (bulk.Length < 3 || bulk[0] != TdsConstants.Token.ColMetadata) return null;
        bool with = Fits(bulk, true), without = Fits(bulk, false);
        return with == without ? null : with;

        static bool Fits(ReadOnlySpan<byte> bulk, bool cekTable)
        {
            try
            {
                var cursor = new TdsCursor(bulk[1..]);
                if (ReadColumnMetadata(ref cursor, cekTable) is not { Length: > 0 }) return false;
                if (cursor.Remaining == 0) return false;
                return cursor.Rest[0] is TdsConstants.Token.Row or TdsConstants.Token.NbcRow or TdsConstants.Token.Done;
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or TdsUnsupportedException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// A column's TYPE_INFO, which differs from a parameter's in two places: text, ntext and image
    /// name their table, and a CLR UDT carries its size and assembly name. (From DaffyTee.)
    /// </summary>
    private static TypeInfo ReadColumnType(ref TdsCursor cursor)
    {
        byte type = cursor.Rest[0];
        switch (type)
        {
            case 0x23 or 0x63 or 0x22:
            {
                var info = TypeInfoReader.ReadTypeInfo(ref cursor);
                int parts = cursor.ReadByte();
                for (int i = 0; i < parts; i++) cursor.ReadUsVarChar();
                return info;
            }

            case 0xF0:
            {
                cursor.ReadByte();
                cursor.ReadUInt16();                           // max byte size
                string database = cursor.ReadBVarChar();
                string schema = cursor.ReadBVarChar();
                string name = cursor.ReadBVarChar();
                cursor.ReadUsVarChar();                        // assembly-qualified name
                return new TypeInfo(0xF0, 0xFFFF) { UdtName = $"{database}.{schema}.{name}".Trim('.') };
            }

            default:
                return TypeInfoReader.ReadTypeInfo(ref cursor);
        }
    }

    private static OutputValue ParseReturnValue(ReadOnlySpan<byte> span, out int consumed)
    {
        var cursor = new TdsCursor(span);
        ushort ordinal = cursor.ReadUInt16();
        string name = cursor.ReadBVarChar();
        cursor.ReadByte();                                     // status
        cursor.ReadUInt32();                                   // UserType
        ushort flags = cursor.ReadUInt16();
        var type = ReadColumnType(ref cursor);
        if ((flags & EncryptedColumnFlag) != 0)
            throw new TdsUnsupportedException("an encrypted output parameter (Always Encrypted)");
        object? value = TypeInfoReader.ReadValue(ref cursor, type);
        consumed = cursor.Position;
        return new OutputValue(ordinal, name, value);
    }

    private static ServerMessage ReadMessage(ReadOnlySpan<byte> body)
    {
        var cursor = new TdsCursor(body);
        int number = cursor.ReadInt32();
        byte state = cursor.ReadByte();
        byte severity = cursor.ReadByte();
        string text = cursor.ReadUsVarChar();
        string server = cursor.ReadBVarChar();
        string procedure = cursor.ReadBVarChar();
        int line = cursor.Remaining >= 4 ? cursor.ReadInt32() : 0;
        return new ServerMessage(number, state, severity, text, server, procedure, line);
    }

    /// <summary>Records what an ENVCHANGE says, and returns the transaction descriptor in force after it.</summary>
    private static ulong ReadEnvChange(ReadOnlySpan<byte> body, ResponseSummary summary, ulong descriptor)
    {
        var cursor = new TdsCursor(body);
        byte type = cursor.ReadByte();

        switch (type)
        {
            case TdsConstants.EnvChangeType.Database:
                summary.Database = cursor.ReadBVarChar();
                break;

            case TdsConstants.EnvChangeType.PacketSize:
                if (int.TryParse(cursor.ReadBVarChar(), out int size)) summary.PacketSize = size;
                break;

            case TdsConstants.EnvChangeType.BeginTransaction:
            {
                var value = cursor.ReadBVarByte();
                if (value.Length == 8)
                {
                    descriptor = BinaryPrimitives.ReadUInt64LittleEndian(value);
                    summary.Transactions.Add($"begin 0x{descriptor:X16}");
                }
                else
                {
                    summary.Transactions.Add("begin");
                }
                break;
            }

            case TdsConstants.EnvChangeType.CommitTransaction:
                summary.Transactions.Add("commit");
                descriptor = 0;
                break;

            case TdsConstants.EnvChangeType.RollbackTransaction:
                summary.Transactions.Add("rollback");
                descriptor = 0;
                break;

            case TdsConstants.EnvChangeType.TransactionEnded:
                descriptor = 0;
                break;

            case TdsConstants.EnvChangeType.ResetConnectionAck:
                summary.ResetAcknowledged = true;
                break;
        }

        return descriptor;
    }

    /// <summary>For a caller timing the response: Stopwatch ticks to milliseconds.</summary>
    public static double Milliseconds(long from, long to) => Stopwatch.GetElapsedTime(from, to).TotalMilliseconds;
}
