using System.Text;
using DaffyReplay.Tds;

namespace DaffyReplay.Replay;

/// <summary>
/// Builds a request from what an event decoded, for an event that carries no raw bytes. A batch is
/// rebuilt exactly - it is nothing but its text - and so is a transaction-manager request, which
/// has few fields. (Procedure calls without raw bytes are rendered as T-SQL instead; see SqlText.)
/// </summary>
internal static class RequestBuilder
{
    /// <summary>SQL_BATCH: ALL_HEADERS, then the text as UTF-16.</summary>
    public static byte[] Batch(string sql, ulong descriptor = 0)
    {
        var writer = new TdsWriter();
        AllHeaders.Write(writer, descriptor);
        writer.WriteBytes(Encoding.Unicode.GetBytes(sql));
        return writer.ToArray();
    }

    /// <summary>
    /// TRANSACTION_MANAGER, as SqlClient's SqlTransaction sends it. Names are length-prefixed in
    /// bytes, not characters, as everywhere else in TDS. Null for a request that cannot be rebuilt
    /// (an operation this does not know).
    /// </summary>
    public static byte[]? Transaction(string operation, string? name, string? isolation, bool beginsNew, ulong descriptor = 0)
    {
        var writer = new TdsWriter();
        AllHeaders.Write(writer, descriptor);

        switch (operation)
        {
            case "begin":
                writer.WriteUInt16(5);
                writer.WriteByte(IsolationCode(isolation));
                WriteName(writer, name);
                break;

            case "commit" or "rollback":
                writer.WriteUInt16(operation == "commit" ? (ushort)7 : (ushort)8);
                WriteName(writer, name);
                writer.WriteByte(beginsNew ? (byte)1 : (byte)0);
                if (beginsNew)
                {
                    writer.WriteByte(IsolationCode(isolation));
                    WriteName(writer, null);
                }
                break;

            case "save":
                writer.WriteUInt16(9);
                WriteName(writer, name);
                break;

            default:
                return null;
        }

        return writer.ToArray();
    }

    private static void WriteName(TdsWriter writer, string? name)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(name ?? "");
        writer.WriteByte((byte)Math.Min(bytes.Length, 254));
        writer.WriteBytes(bytes.AsSpan(0, Math.Min(bytes.Length, 254)));
    }

    /// <summary>The reverse of DaffyTee's names for ISOLATION_LEVEL.</summary>
    private static byte IsolationCode(string? isolation) => isolation switch
    {
        "read uncommitted" => 1,
        "read committed" => 2,
        "repeatable read" => 3,
        "serializable" => 4,
        "snapshot" => 5,
        _ => 0,
    };
}
