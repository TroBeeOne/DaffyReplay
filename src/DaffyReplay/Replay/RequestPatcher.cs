using System.Buffers.Binary;
using DaffyReplay.Recording;
using DaffyReplay.Tds;

namespace DaffyReplay.Replay;

internal enum HandleKind { Prepared, Cursor }

/// <summary>What patching an RPC found: the handles its calls hand back, and the cursor its first call works on.</summary>
internal sealed record HandleUse(IReadOnlyList<HandleOutput> Outputs, int? Cursor)
{
    public static readonly HandleUse None = new([], null);
}

/// <summary>A handle a call hands back in an OUTPUT parameter, to be learned once the response is in.</summary>
internal sealed record HandleOutput(int Call, int Ordinal, string ArgumentName, HandleKind Kind)
{
    /// <summary>The key DaffyTee records the value under in response.output: the name, or "#ordinal".</summary>
    public string Key => ArgumentName.Length > 0 ? ArgumentName : $"#{Ordinal}";
}

/// <summary>
/// Prepared-statement and cursor handles, recorded value to replay value, for one replayed session.
///
/// sp_prepare, sp_prepexec and sp_cursorprepare hand back a prepared handle; sp_cursoropen,
/// sp_cursorprepexec and sp_cursorexecute a cursor. Later calls - sp_execute, sp_unprepare,
/// sp_cursorfetch, sp_cursorclose and the rest - name them by number. The replay server numbers
/// them itself, so each recorded number is mapped to the one the replay was given. A server
/// numbers handles per connection from the same start, so a session replayed from its beginning
/// usually gets the same numbers anyway; the map makes it certain, and a handle whose recorded
/// value is unknown (its response was too large for DaffyTee to decode) is assumed unchanged.
/// </summary>
internal sealed class HandleMap
{
    private readonly Dictionary<int, int> _prepared = [];
    private readonly Dictionary<int, int> _cursors = [];
    private readonly Dictionary<int, ResponseWalker.ColumnShape[]> _cursorColumns = [];

    /// <summary>The column metadata a replay cursor was opened with, for its fetches' rows.</summary>
    public ResponseWalker.ColumnShape[]? ColumnsOf(int? cursor) =>
        cursor is int handle ? _cursorColumns.GetValueOrDefault(handle) : null;

    /// <summary>Handles rewritten to a different number.</summary>
    public int Rewritten { get; private set; }

    /// <summary>Handles used that were never seen being made, sent as recorded.</summary>
    public int Unknown { get; private set; }

    public int? Lookup(HandleKind kind, int recorded)
    {
        var map = kind == HandleKind.Prepared ? _prepared : _cursors;
        if (map.TryGetValue(recorded, out int replay)) return replay;
        Unknown++;
        return null;
    }

    public void Learn(HandleKind kind, int recorded, int replay)
    {
        (kind == HandleKind.Prepared ? _prepared : _cursors)[recorded] = replay;
    }

    public void Forget(HandleKind kind, int recorded)
    {
        var map = kind == HandleKind.Prepared ? _prepared : _cursors;
        if (kind == HandleKind.Cursor) _cursorColumns.Remove(map.GetValueOrDefault(recorded, recorded));
        map.Remove(recorded);
    }

    public void CountRewrite() => Rewritten++;

    /// <summary>
    /// The handles a request's calls handed back, recorded value to replay value. A recorded value
    /// DaffyTee did not decode (its response was too large) leaves the handle assumed unchanged.
    /// </summary>
    public void Learn(IReadOnlyList<HandleOutput> outputs, RecordedResponse recorded, ResponseSummary replayed)
    {
        if (outputs.Count == 0) return;

        // A cursor opened: its fetches will need the shape of its rows.
        foreach (var output in outputs.Where(o => o.Kind == HandleKind.Cursor))
        {
            if (replayed.Outputs.FirstOrDefault(o => o.Call == output.Call && o.Ordinal == output.Ordinal)?.Value is int cursor
                && replayed.Columns is { Length: > 0 } columns)
                _cursorColumns[cursor] = columns;
        }

        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var output in outputs)
        {
            int occurrence = seen.GetValueOrDefault(output.Key);
            seen[output.Key] = occurrence + 1;

            int? replay = replayed.Outputs.FirstOrDefault(o => o.Call == output.Call && o.Ordinal == output.Ordinal)?.Value as int?;
            int? original = recorded.OutputInt(output.Key, occurrence);
            if (replay is int replayHandle && original is int recordedHandle)
                Learn(output.Kind, recordedHandle, replayHandle);
        }
    }

    /// <summary>A pooled connection reset, or a new connection: the server forgets its handles, so does the map.</summary>
    public void Clear()
    {
        _prepared.Clear();
        _cursors.Clear();
    }
}

/// <summary>
/// Makes a recorded request right for the replay connection, in place: the transaction descriptor
/// it runs under, and the handles it names. Everything else - the SQL, every parameter's type,
/// precision, collation and value - stays exactly as recorded.
/// </summary>
internal static class RequestPatcher
{
    /// <summary>Calls whose first argument names a handle made earlier, and which kind.</summary>
    private static readonly Dictionary<ushort, HandleKind> Consumes = new()
    {
        [TdsConstants.ProcId.Execute] = HandleKind.Prepared,
        [TdsConstants.ProcId.Unprepare] = HandleKind.Prepared,
        [TdsConstants.ProcId.CursorExecute] = HandleKind.Prepared,
        [TdsConstants.ProcId.CursorUnprepare] = HandleKind.Prepared,
        [TdsConstants.ProcId.Cursor] = HandleKind.Cursor,
        [TdsConstants.ProcId.CursorFetch] = HandleKind.Cursor,
        [TdsConstants.ProcId.CursorOption] = HandleKind.Cursor,
        [TdsConstants.ProcId.CursorClose] = HandleKind.Cursor,
    };

    /// <summary>Calls that hand back new handles in OUTPUT parameters: (argument position, kind).</summary>
    private static readonly Dictionary<ushort, (int Ordinal, HandleKind Kind)[]> Produces = new()
    {
        [TdsConstants.ProcId.Prepare] = [(0, HandleKind.Prepared)],
        [TdsConstants.ProcId.PrepExec] = [(0, HandleKind.Prepared)],
        [TdsConstants.ProcId.CursorPrepare] = [(0, HandleKind.Prepared)],
        [TdsConstants.ProcId.CursorPrepExec] = [(0, HandleKind.Prepared), (1, HandleKind.Cursor)],
        [TdsConstants.ProcId.CursorOpen] = [(0, HandleKind.Cursor)],
        [TdsConstants.ProcId.CursorExecute] = [(1, HandleKind.Cursor)],
    };

    /// <summary>
    /// Writes <paramref name="descriptor"/> into the request's transaction-descriptor header, with
    /// one outstanding request. True when the request has such a header (BULK_LOAD does not).
    /// </summary>
    public static bool PatchDescriptor(Span<byte> payload, ulong descriptor)
    {
        if (AllHeaders.DescriptorOffset(payload, out bool withCount) is not int offset) return false;
        BinaryPrimitives.WriteUInt64LittleEndian(payload[offset..], descriptor);
        if (withCount) BinaryPrimitives.WriteUInt32LittleEndian(payload[(offset + 8)..], 1);
        return true;
    }

    /// <summary>
    /// Rewrites the handles an RPC names to the replay's, and says which handles its calls will hand
    /// back. Only the procedures that deal in handles are decoded; every other RPC passes untouched.
    /// </summary>
    public static HandleUse PatchHandles(byte[] payload, HandleMap handles)
    {
        if (!MightUseHandles(payload)) return HandleUse.None;

        IReadOnlyList<RpcCall> calls;
        try
        {
            calls = RequestParser.ParseRpc(payload);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or TdsUnsupportedException)
        {
            return HandleUse.None;
        }

        var outputs = new List<HandleOutput>();
        int? cursor = null;
        for (int c = 0; c < calls.Count; c++)
        {
            var call = calls[c];
            if (Consumes.TryGetValue(call.ProcId, out var kind) && call.Arguments.Count > 0
                && ReadInt(payload, call.Arguments[0]) is int recorded)
            {
                int used = recorded;
                if (handles.Lookup(kind, recorded) is int replay)
                {
                    used = replay;
                    if (replay != recorded)
                    {
                        WriteInt(payload, call.Arguments[0], replay);
                        handles.CountRewrite();
                    }
                }
                if (kind == HandleKind.Cursor && c == 0) cursor = used;
                if (call.ProcId is TdsConstants.ProcId.Unprepare or TdsConstants.ProcId.CursorUnprepare)
                    handles.Forget(HandleKind.Prepared, recorded);
                if (call.ProcId == TdsConstants.ProcId.CursorClose)
                    handles.Forget(HandleKind.Cursor, recorded);
            }

            if (Produces.TryGetValue(call.ProcId, out var produced))
            {
                foreach (var (ordinal, producedKind) in produced)
                {
                    if (ordinal < call.Arguments.Count && call.Arguments[ordinal].IsOutput)
                        outputs.Add(new HandleOutput(c, ordinal, call.Arguments[ordinal].Name, producedKind));
                }
            }
        }

        return new HandleUse(outputs, cursor);
    }

    /// <summary>The first call's procedure, read without decoding its parameters.</summary>
    private static bool MightUseHandles(ReadOnlySpan<byte> payload)
    {
        int offset = AllHeaders.BodyOffset(payload);
        if (payload.Length < offset + 4) return false;

        ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]);
        if (nameLength == TdsConstants.RpcProcIdSwitch)
        {
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(payload[(offset + 2)..]);
            return id is >= TdsConstants.ProcId.Cursor and <= TdsConstants.ProcId.CursorClose
                or TdsConstants.ProcId.Prepare or TdsConstants.ProcId.Execute
                or TdsConstants.ProcId.PrepExec or TdsConstants.ProcId.Unprepare;
        }

        if (payload.Length < offset + 2 + nameLength * 2) return false;
        string name = System.Text.Encoding.Unicode.GetString(payload.Slice(offset + 2, nameLength * 2)).ToLowerInvariant();
        return name.Contains("sp_prep") || name.Contains("sp_execute") && !name.Contains("sp_executesql")
            || name.Contains("sp_unprepare") || name.Contains("sp_cursor");
    }

    /// <summary>An int argument's value: INT4, or INTN of length 4. Null for NULL or any other type.</summary>
    private static int? ReadInt(ReadOnlySpan<byte> payload, RpcParameter argument)
    {
        int at = argument.ValueOffset;
        if (at < 0) return null;
        return argument.Type.Type switch
        {
            0x38 when payload.Length >= at + 4 => BinaryPrimitives.ReadInt32LittleEndian(payload[at..]),
            0x26 when payload.Length >= at + 5 && payload[at] == 4 => BinaryPrimitives.ReadInt32LittleEndian(payload[(at + 1)..]),
            _ => null,
        };
    }

    private static void WriteInt(Span<byte> payload, RpcParameter argument, int value)
    {
        int at = argument.ValueOffset;
        if (argument.Type.Type == 0x38) BinaryPrimitives.WriteInt32LittleEndian(payload[at..], value);
        else BinaryPrimitives.WriteInt32LittleEndian(payload[(at + 1)..], value);
    }
}
