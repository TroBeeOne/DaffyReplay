using System.Buffers.Binary;
using System.Text;

namespace DaffyReplay.Tds;

/// <summary>An ERROR or INFO token.</summary>
internal sealed record ServerMessage(int Number, byte State, byte Severity, string Text, string Server, string Procedure, int Line)
{
    public override string ToString() => $"Msg {Number}, Level {Severity}, State {State}: {Text}";
}

/// <summary>Where a routing ENVCHANGE told the client to go instead. Protocol 0 is TCP, the only one defined.</summary>
internal sealed record RoutingTarget(string Server, ushort Port, byte Protocol = 0)
{
    public override string ToString() => $"{Server}:{Port}";
}

/// <summary>
/// FEDAUTHINFO: a target asking for an Entra ID token, and saying where to get one (the STS URL,
/// which names the tenant) and for what (the SPN, the resource the token is for).
/// </summary>
internal sealed record FedAuthInfo(string StsUrl, string Spn);

/// <summary>
/// What a target said in answer to LOGIN7, decoded just far enough to decide whether the login
/// worked and what was agreed. (From DaffyTee.)
/// </summary>
internal sealed class LoginResponse
{
    public bool LoginAcknowledged { get; private set; }
    public string ServerProgram { get; private set; } = "";
    public string ServerVersion { get; private set; } = "";
    public uint TdsVersion { get; private set; }
    public string? Database { get; private set; }
    public string? Language { get; private set; }
    public int? PacketSize { get; private set; }
    public RoutingTarget? Routing { get; private set; }

    /// <summary>Set when the answer is not a login result yet but a request for an Entra ID token.</summary>
    public FedAuthInfo? FedAuth { get; private set; }
    public List<ServerMessage> Errors { get; } = [];
    public List<ServerMessage> Infos { get; } = [];
    public List<byte> AcknowledgedFeatures { get; } = [];

    /// <summary>A LOGINACK and no ERROR. A routing answer can carry a LOGINACK too, so check that first.</summary>
    public bool Succeeded => LoginAcknowledged && Errors.Count == 0;

    public static LoginResponse Parse(ReadOnlySpan<byte> payload)
    {
        var response = new LoginResponse();
        var cursor = new TdsCursor(payload);

        while (cursor.Remaining > 0)
        {
            byte token = cursor.ReadByte();
            switch (token)
            {
                case TdsConstants.Token.LoginAck:
                {
                    int end = cursor.ReadUInt16() + cursor.Position;
                    cursor.ReadByte();                                    // interface
                    response.TdsVersion = BinaryPrimitives.ReadUInt32BigEndian(cursor.ReadBytes(4));
                    response.ServerProgram = cursor.ReadBVarChar().TrimEnd('\0');
                    byte major = cursor.ReadByte(), minor = cursor.ReadByte();
                    int build = (cursor.ReadByte() << 8) | cursor.ReadByte();
                    response.ServerVersion = $"{major}.{minor}.{build}";
                    response.LoginAcknowledged = true;
                    cursor.Seek(end);
                    break;
                }

                case TdsConstants.Token.EnvChange:
                {
                    int end = cursor.ReadUInt16() + cursor.Position;
                    ReadEnvChange(ref cursor, end, response);
                    cursor.Seek(end);
                    break;
                }

                case TdsConstants.Token.Error:
                case TdsConstants.Token.Info:
                {
                    var message = ReadMessage(ref cursor);
                    (token == TdsConstants.Token.Error ? response.Errors : response.Infos).Add(message);
                    break;
                }

                case TdsConstants.Token.FeatureExtAck:
                    while (true)
                    {
                        byte feature = cursor.ReadByte();
                        if (feature == TdsConstants.FeatureId.Terminator) break;
                        response.AcknowledgedFeatures.Add(feature);
                        cursor.Skip((int)cursor.ReadUInt32());
                    }
                    break;

                case TdsConstants.Token.SessionState:
                    cursor.Skip((int)cursor.ReadUInt32());
                    break;

                case TdsConstants.Token.FedAuthInfo:
                    response.FedAuth = ReadFedAuthInfo(cursor.ReadBytes((int)cursor.ReadUInt32()));
                    break;

                case TdsConstants.Token.Done:
                case TdsConstants.Token.DoneProc:
                case TdsConstants.Token.DoneInProc:
                    cursor.Skip(12);   // status(2) curcmd(2) rowcount(8)
                    break;

                default:
                    // Nothing else belongs in a login response. Stop reading rather than guess; what
                    // was read so far is enough to decide.
                    return response;
            }
        }

        return response;
    }

    private static void ReadEnvChange(ref TdsCursor cursor, int end, LoginResponse response)
    {
        byte type = cursor.ReadByte();
        switch (type)
        {
            case TdsConstants.EnvChangeType.Database:
                response.Database = cursor.ReadBVarChar();
                break;

            case TdsConstants.EnvChangeType.Language:
                response.Language = cursor.ReadBVarChar();
                break;

            case TdsConstants.EnvChangeType.PacketSize:
                if (int.TryParse(cursor.ReadBVarChar(), out int size)) response.PacketSize = size;
                break;

            case TdsConstants.EnvChangeType.RoutingInformation:
            {
                // RoutingDataValueLength(2) Protocol(1, 0 = TCP) Port(2) AlternateServer(US_VARCHAR)
                cursor.ReadUInt16();
                byte protocol = cursor.ReadByte();
                ushort port = cursor.ReadUInt16();
                string server = cursor.ReadUsVarChar();
                response.Routing = new RoutingTarget(server, port, protocol);
                break;
            }
        }
    }

    /// <summary>
    /// CountOfInfoIDs, then (id, length, offset) per item, then the data the offsets point into -
    /// offsets from the start of CountOfInfoIDs, strings in UTF-16LE. 1 is the STS URL, 2 the SPN.
    /// </summary>
    private static FedAuthInfo ReadFedAuthInfo(ReadOnlySpan<byte> data)
    {
        string stsUrl = "", spn = "";
        int count = data.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(data) : 0;

        for (int i = 0; i < count && 4 + i * 9 + 9 <= data.Length; i++)
        {
            int entry = 4 + i * 9;
            int length = BinaryPrimitives.ReadInt32LittleEndian(data[(entry + 1)..]);
            int offset = BinaryPrimitives.ReadInt32LittleEndian(data[(entry + 5)..]);
            if (length < 0 || offset < 0 || offset + length > data.Length) continue;

            string value = Encoding.Unicode.GetString(data.Slice(offset, length));
            switch (data[entry])
            {
                case 0x01: stsUrl = value; break;
                case 0x02: spn = value; break;
            }
        }

        return new FedAuthInfo(stsUrl, spn);
    }

    public static ServerMessage ReadMessage(ref TdsCursor cursor)
    {
        int end = cursor.ReadUInt16() + cursor.Position;
        int number = cursor.ReadInt32();
        byte state = cursor.ReadByte();
        byte severity = cursor.ReadByte();
        string text = cursor.ReadUsVarChar();
        string server = cursor.ReadBVarChar();
        string procedure = cursor.ReadBVarChar();
        int line = cursor.Position + 4 <= end ? cursor.ReadInt32() : 0;
        cursor.Seek(end);
        return new ServerMessage(number, state, severity, text, server, procedure, line);
    }

    public string TdsVersionName => TdsVersion switch
    {
        0x74000004 => "7.4",
        0x08000000 => "8.0",
        _ => $"0x{TdsVersion:X8}",
    };

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(LoginAcknowledged ? $"LOGINACK {ServerProgram} {ServerVersion} tds={TdsVersionName}" : "no LOGINACK");
        if (Database is not null) sb.Append($" db='{Database}'");
        if (PacketSize is not null) sb.Append($" packetSize={PacketSize}");
        if (AcknowledgedFeatures.Count > 0)
            sb.Append($" features=[{string.Join(",", AcknowledgedFeatures.Select(TdsConstants.FeatureId.Name))}]");
        if (Routing is not null) sb.Append($" ROUTING->{Routing}");
        foreach (var error in Errors) sb.Append($" | {error}");
        return sb.ToString();
    }
}
