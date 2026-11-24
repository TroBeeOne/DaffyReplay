using System.Reflection;

namespace DaffyReplay.Tds;

/// <summary>
/// PRELOGIN, as a client sends it and a server answers it. (From DaffyTee, which does both.)
///
/// It is an offset/length option table followed by the option payloads, and unlike the rest of TDS
/// its offsets and lengths are big-endian. A server replies in a TABULAR_RESULT packet (0x04), not
/// another PRELOGIN.
/// </summary>
internal static class PreLogin
{
    /// <summary>Reads the option table into (option, value) pairs, in the order they were sent.</summary>
    public static List<(byte Option, byte[] Data)> Parse(ReadOnlySpan<byte> payload)
    {
        var options = new List<(byte, byte[])>();
        var cursor = new TdsCursor(payload);

        while (cursor.Remaining >= 1)
        {
            byte option = cursor.ReadByte();
            if (option == TdsConstants.PreLoginOption.Terminator) break;
            if (cursor.Remaining < 4) break;

            ushort offset = cursor.ReadUInt16BigEndian();
            ushort length = cursor.ReadUInt16BigEndian();
            if (offset + length > payload.Length) break;

            options.Add((option, payload.Slice(offset, length).ToArray()));
        }

        return options;
    }

    public static byte[] Build(IReadOnlyList<(byte Option, byte[] Data)> options)
    {
        var writer = new TdsWriter();
        int dataOffset = options.Count * 5 + 1;   // 5 bytes per entry plus the terminator

        foreach (var (option, data) in options)
        {
            writer.WriteByte(option);
            writer.WriteUInt16BigEndian((ushort)dataOffset);
            writer.WriteUInt16BigEndian((ushort)data.Length);
            dataOffset += data.Length;
        }
        writer.WriteByte(TdsConstants.PreLoginOption.Terminator);

        foreach (var (_, data) in options) writer.WriteBytes(data);

        return writer.ToArray();
    }

    public static byte? Value(IReadOnlyList<(byte Option, byte[] Data)> options, byte option)
    {
        foreach (var (id, data) in options)
            if (id == option) return data.Length > 0 ? data[0] : null;
        return null;
    }

    public static bool Has(IReadOnlyList<(byte Option, byte[] Data)> options, byte option) =>
        options.Any(o => o.Option == option);

    /// <summary>
    /// The PRELOGIN DaffyReplay sends, shaped like Microsoft.Data.SqlClient's: VERSION,
    /// ENCRYPTION, INSTOPT, THREADID, MARS, and FEDAUTHREQUIRED when it will log in with an Entra
    /// ID token.
    /// </summary>
    public static byte[] BuildClientRequest(byte encryption, bool mars, bool fedAuth = false)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0);

        var options = new List<(byte Option, byte[] Data)>
        {
            (TdsConstants.PreLoginOption.Version,
                [(byte)version.Major, (byte)version.Minor, (byte)(version.Build >> 8), (byte)version.Build, 0, 0]),
            (TdsConstants.PreLoginOption.Encryption, [encryption]),
            (TdsConstants.PreLoginOption.InstOpt, [0x00]),                 // default instance
            (TdsConstants.PreLoginOption.ThreadId, BitConverter.GetBytes(Environment.CurrentManagedThreadId)),
            (TdsConstants.PreLoginOption.Mars, [mars ? (byte)1 : (byte)0]),
        };
        if (fedAuth) options.Add((TdsConstants.PreLoginOption.FedAuthRequired, [0x01]));

        return Build(options);
    }
}
