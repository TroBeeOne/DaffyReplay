using System.Buffers.Binary;

namespace DaffyReplay.Tds;

/// <summary>
/// Every TDS 7.2+ SQL_BATCH, RPC and TRANSACTION_MANAGER request begins with an ALL_HEADERS block:
///
///   TotalLength : DWORD   (includes itself)
///   Header*     : DWORD HeaderLength (includes itself), USHORT HeaderType, then data
///
/// SqlClient always sends one - the Transaction Descriptor (type 0x0002, an 8-byte descriptor plus
/// a 4-byte outstanding-request count), so 22 bytes in total. The SQL text or RPC body starts
/// after it. (From DaffyTee, which reads it; DaffyReplay also writes and patches it.)
/// </summary>
internal static class AllHeaders
{
    private const int MaxPlausibleLength = 1024;

    /// <summary>The block SqlClient sends: total length, one transaction-descriptor header.</summary>
    public const int StandardLength = 22;

    /// <summary>Returns the offset at which the actual request body begins.</summary>
    public static int BodyOffset(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return 0;

        uint totalLength = BinaryPrimitives.ReadUInt32LittleEndian(payload);

        // A pre-7.2 client sends no ALL_HEADERS at all. UTF-16 text would start with a low ASCII
        // byte followed by 0x00, which cannot be a plausible TotalLength.
        if (totalLength < 4 || totalLength > MaxPlausibleLength || totalLength > payload.Length) return 0;

        return (int)totalLength;
    }

    /// <summary>
    /// The 8-byte transaction descriptor the request ran under - zero outside a transaction. The
    /// server hands it out when a transaction begins (ENVCHANGE) and the client echoes it on every
    /// request until it ends, so statements that share one belong to the same transaction.
    /// </summary>
    public static ulong? TransactionDescriptor(ReadOnlySpan<byte> payload) =>
        DescriptorOffset(payload) is int offset ? BinaryPrimitives.ReadUInt64LittleEndian(payload[offset..]) : null;

    /// <summary>
    /// Where the transaction descriptor sits in the payload, or null when the request carries no
    /// transaction-descriptor header. <paramref name="withCount"/> says whether the 4-byte
    /// outstanding-request count follows it, as it always does from TDS 7.2 on.
    /// </summary>
    public static int? DescriptorOffset(ReadOnlySpan<byte> payload) => DescriptorOffset(payload, out _);

    public static int? DescriptorOffset(ReadOnlySpan<byte> payload, out bool withCount)
    {
        withCount = false;
        int bodyOffset = BodyOffset(payload);
        int position = 4;
        while (bodyOffset > 0 && position + 6 <= bodyOffset)
        {
            uint headerLength = BinaryPrimitives.ReadUInt32LittleEndian(payload[position..]);
            if (headerLength < 6 || position + headerLength > bodyOffset) break;

            ushort headerType = BinaryPrimitives.ReadUInt16LittleEndian(payload[(position + 4)..]);
            if (headerType == TdsConstants.HeaderType.TransactionDescriptor && headerLength >= 14)
            {
                withCount = headerLength >= 18;
                return position + 6;
            }

            position += (int)headerLength;
        }
        return null;
    }

    /// <summary>The block SqlClient writes: one transaction-descriptor header, one outstanding request.</summary>
    public static void Write(TdsWriter writer, ulong descriptor)
    {
        writer.WriteUInt32(StandardLength);
        writer.WriteUInt32(18);                       // this header's length
        writer.WriteUInt16(TdsConstants.HeaderType.TransactionDescriptor);
        writer.WriteUInt64(descriptor);
        writer.WriteUInt32(1);                        // outstanding requests
    }
}
