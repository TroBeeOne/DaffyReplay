using System.Globalization;
using System.Text.Json;

namespace DaffyReplay.Recording;

/// <summary>One error as DaffyTee recorded it.</summary>
internal sealed record RecordedError(int Number, int Severity, int State, string Message);

/// <summary>One parameter of a recorded call: its declared type, direction and value, as JSON.</summary>
internal sealed record RecordedParam(string Name, string Type, bool Output, bool Default, bool Encrypted, JsonElement Value);

/// <summary>One procedure call of an RPC, as DaffyTee decoded it.</summary>
internal sealed record RecordedCall(string Proc, int ProcId, string? Sql, int? Handle)
{
    public string? ParamDecl { get; init; }
    public IReadOnlyList<RecordedParam> Params { get; init; } = [];

    /// <summary>Set when DaffyTee could not decode the whole call.</summary>
    public string? Undecoded { get; init; }
}

/// <summary>A transaction-manager request, as DaffyTee decoded it.</summary>
internal sealed record RecordedTransaction(string Op, string? Name, string? Isolation, bool BeginsNew);

/// <summary>How a recorded request ended, as DaffyTee saw it.</summary>
internal sealed class RecordedResponse
{
    /// <summary>ok, error, cancelled or incomplete.</summary>
    public string Status { get; init; } = "ok";

    public ulong? RowCount { get; init; }
    public long Bytes { get; init; }
    public double? FirstByteMs { get; init; }

    // Present only when DaffyTee decoded the response, which it does when it is small (--response-kb).
    public IReadOnlyList<RecordedError> Errors { get; init; } = [];
    public IReadOnlyList<ulong>? RowCounts { get; init; }
    public IReadOnlyList<long>? ResultSets { get; init; }
    public int? ReturnStatus { get; init; }

    /// <summary>OUTPUT parameters in the order they came back, keyed by name or "#ordinal". A key can repeat in a batched RPC.</summary>
    public IReadOnlyList<KeyValuePair<string, JsonElement>> Output { get; init; } = [];

    public string? Database { get; init; }
    public IReadOnlyList<string> Transactions { get; init; } = [];
    public string? DetailProblem { get; init; }

    /// <summary>
    /// Where a recording imported by <c>daffyreplay import</c> came from ("sqlsim"); null for a
    /// DaffyTee recording. An imported response knows its status and at most <see cref="RowTotal"/>.
    /// </summary>
    public string? ImportedFrom { get; init; }

    /// <summary>An imported batch's rows, all its statements added up: what XEvents' row_count says.</summary>
    public ulong? RowTotal { get; init; }

    /// <summary>The n-th (from 0) output under <paramref name="key"/>, as an int: how a recorded handle is found.</summary>
    public int? OutputInt(string key, int occurrence = 0)
    {
        foreach (var (name, value) in Output)
        {
            if (!string.Equals(name, key, StringComparison.OrdinalIgnoreCase)) continue;
            if (occurrence-- > 0) continue;
            return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : null;
        }
        return null;
    }
}

/// <summary>Who and where a recorded request ran, from its event's data.session.</summary>
internal sealed record RecordedSessionRef(
    int Id, string ConnectionId, string Login, string TargetLogin, string Target, string? RoutedTo,
    int Spid, string? Database, string Host, string App, string Client);

/// <summary>
/// A request event, decoded for replay: what was sent, its raw bytes when the recording has them,
/// and how it went when recorded. Read from the published event schema (data.v = 1), which is the
/// whole contract between DaffyTee and DaffyReplay.
/// </summary>
internal sealed class RecordedRequest
{
    /// <summary>batch, rpc, txn or bulk.</summary>
    public required string Kind { get; init; }

    public required DateTime StartUtc { get; init; }
    public required long Seq { get; init; }
    public required RecordedSessionRef Session { get; init; }
    public double? DurationMs { get; init; }

    public byte[]? Raw { get; init; }
    public string? Sql { get; init; }
    public IReadOnlyList<RecordedCall> Calls { get; init; } = [];
    public RecordedTransaction? Transaction { get; init; }
    public string? BulkTable { get; init; }

    public ulong? TransactionDescriptor { get; init; }
    public bool ResetConnection { get; init; }
    public int? MarsChannel { get; init; }
    public string? GatewayCommand { get; init; }
    public bool Truncated { get; init; }
    public bool Redacted { get; init; }
    public string? DecodeProblem { get; init; }
    public long Bytes { get; init; }

    public required RecordedResponse Response { get; init; }

    public static RecordedRequest Parse(ReadOnlyMemory<byte> json)
    {
        using var document = JsonDocument.Parse(json);
        return FromDocument(document.RootElement);
    }

    public static RecordedRequest FromDocument(JsonElement root)
    {
        string type = Text(root, "type") ?? "";
        var data = root.GetProperty("data");
        var request = data.TryGetProperty("request", out var r) ? r : default;
        var response = data.TryGetProperty("response", out var p) ? p : default;

        return new RecordedRequest
        {
            Kind = Text(request, "kind") ?? (type.StartsWith("request.") ? type[8..] : type),
            StartUtc = Time(root),
            Seq = Long(data, "seq") ?? 0,
            Session = SessionRef(data.GetProperty("session")),
            DurationMs = Double(data, "durationMs"),
            Raw = request.ValueKind == JsonValueKind.Object && request.TryGetProperty("raw", out var raw) ? raw.GetBytesFromBase64() : null,
            Sql = Text(request, "sql"),
            Calls = Array(request, "calls").Select(c => new RecordedCall(
                Text(c, "proc") ?? "", (int)(Long(c, "procId") ?? 0), Text(c, "sql"), (int?)Long(c, "handle"))
            {
                ParamDecl = Text(c, "paramDecl"),
                Undecoded = Text(c, "undecoded"),
                Params = Array(c, "params").Select(p => new RecordedParam(
                    Text(p, "name") ?? "", Text(p, "type") ?? "", Text(p, "dir") == "out", Bool(p, "default"), Bool(p, "encrypted"),
                    p.TryGetProperty("value", out var value) ? value.Clone() : default)).ToList(),
            }).ToList(),
            Transaction = request.ValueKind == JsonValueKind.Object && request.TryGetProperty("transaction", out var t)
                ? new RecordedTransaction(Text(t, "op") ?? "", Text(t, "name"), Text(t, "isolation"), Bool(t, "beginsNew"))
                : null,
            BulkTable = request.ValueKind == JsonValueKind.Object && request.TryGetProperty("bulk", out var b) ? Text(b, "table") : null,
            TransactionDescriptor = Text(request, "txn") is { } txn
                ? ulong.Parse(txn.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : null,
            ResetConnection = Bool(request, "resetConnection"),
            MarsChannel = (int?)Long(request, "marsChannel"),
            GatewayCommand = Text(request, "gatewayCommand"),
            Truncated = Bool(request, "truncated"),
            Redacted = Bool(request, "redacted"),
            DecodeProblem = Text(request, "decodeProblem"),
            Bytes = Long(request, "bytes") ?? 0,
            Response = ResponseFrom(response),
        };
    }

    private static RecordedResponse ResponseFrom(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object) return new RecordedResponse { Status = "incomplete" };

        return new RecordedResponse
        {
            Status = Text(response, "status") ?? "ok",
            RowCount = response.TryGetProperty("rowCount", out var rc) && rc.TryGetUInt64(out ulong rows) ? rows : null,
            Bytes = Long(response, "bytes") ?? 0,
            FirstByteMs = Double(response, "firstByteMs"),
            Errors = Array(response, "errors").Select(e => new RecordedError(
                (int)(Long(e, "number") ?? 0), (int)(Long(e, "severity") ?? 0), (int)(Long(e, "state") ?? 0), Text(e, "message") ?? "")).ToList(),
            RowCounts = response.TryGetProperty("rowCounts", out var counts) ? counts.EnumerateArray().Select(c => c.GetUInt64()).ToList() : null,
            ResultSets = response.TryGetProperty("resultSets", out var sets) ? sets.EnumerateArray().Select(s => s.GetInt64()).ToList() : null,
            ReturnStatus = (int?)Long(response, "returnStatus"),
            Output = response.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Object
                ? output.EnumerateObject().Select(o => new KeyValuePair<string, JsonElement>(o.Name, o.Value.Clone())).ToList()
                : [],
            Database = Text(response, "database"),
            Transactions = Array(response, "transactions").Select(x => x.GetString() ?? "").ToList(),
            DetailProblem = Text(response, "detailProblem"),
            ImportedFrom = Text(response, "importedFrom"),
            RowTotal = response.TryGetProperty("rowTotal", out var total) && total.TryGetUInt64(out ulong sum) ? sum : null,
        };
    }

    public static RecordedSessionRef SessionRef(JsonElement s) => new(
        (int)(Long(s, "id") ?? 0), Text(s, "connectionId") ?? "", Text(s, "login") ?? "", Text(s, "targetLogin") ?? "",
        Text(s, "target") ?? "", Text(s, "routedTo"), (int)(Long(s, "spid") ?? 0), Text(s, "database"),
        Text(s, "host") ?? "", Text(s, "app") ?? "", Text(s, "client") ?? "");

    // ---- small readers, tolerant of anything absent: fields may be added within a schema version ----

    public static DateTime Time(JsonElement root) =>
        root.TryGetProperty("time", out var time) && time.TryGetDateTime(out var value)
            ? value.ToUniversalTime()
            : DateTime.MinValue;

    public static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static long? Long(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out long number)
            ? number
            : null;

    public static double? Double(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    public static bool Bool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    public static IEnumerable<JsonElement> Array(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];
}
