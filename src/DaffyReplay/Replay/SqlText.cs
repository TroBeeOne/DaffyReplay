using System.Globalization;
using System.Text;
using System.Text.Json;
using DaffyReplay.Recording;
using DaffyReplay.Tds;

namespace DaffyReplay.Replay;

/// <summary>
/// A procedure call recorded without raw bytes, rendered as T-SQL - the way DaffyTee's --sql-log
/// writes it, from the parameter values the event decoded - and replayed as a batch. Approximate:
/// a literal is not a typed parameter (the server may choose another plan), and table-valued and
/// OUTPUT parameters become variables declared in the batch. DaffyReplay marks every request
/// replayed this way, and compares it on its results and errors only.
///
/// Prepared statements are replayed as the equivalent sp_executesql: a prepared handle means
/// nothing in rendered text, and DaffyTee names the statement behind each sp_execute. Cursor calls
/// cannot be rendered and are not replayed.
/// </summary>
internal static class SqlText
{
    /// <param name="declarations">
    /// The parameter declarations a prepared handle was prepared with, by recorded handle: an
    /// sp_execute carries its statement (DaffyTee names it) but not its declarations.
    /// </param>
    public static string? Render(RecordedRequest request, Func<int, string?>? declarations = null)
    {
        if (request.Kind != "rpc" || request.Calls.Count == 0) return null;

        var sb = new StringBuilder();
        int counter = 0;
        foreach (var call in request.Calls)
        {
            if (call.Undecoded is not null) return null;
            if (!RenderCall(sb, call, declarations, ref counter)) return null;
        }
        return sb.ToString();
    }

    private static bool RenderCall(StringBuilder sb, RecordedCall call, Func<int, string?>? prepared, ref int counter)
    {
        switch (call.ProcId)
        {
            case TdsConstants.ProcId.Unprepare:
                sb.Append($"-- {call.Proc} {call.Handle}: nothing to undo in a rendered replay\n");
                return true;

            case >= TdsConstants.ProcId.Cursor and <= TdsConstants.ProcId.CursorClose:
                return false;

            case TdsConstants.ProcId.Execute when call.Sql is null:
                return false;   // prepared before the recording began: the statement is not known
        }

        var declarations = new StringBuilder();
        var assignments = new List<string>();
        foreach (var p in call.Params)
        {
            string value;
            if (p.Default)
            {
                value = "DEFAULT";
            }
            else if (p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("tableType", out var tableType))
            {
                string variable = $"@tvp{++counter}";
                declarations.Append($"DECLARE {variable} {tableType.GetString()};\n");
                var columns = p.Value.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("type").GetString() ?? "").ToList();
                var rows = p.Value.GetProperty("rows").EnumerateArray().ToList();
                if (rows.Count > 0)
                {
                    declarations.Append($"INSERT {variable} VALUES\n    ");
                    declarations.Append(string.Join(",\n    ", rows.Select(row =>
                        "(" + string.Join(", ", row.EnumerateArray().Select((cell, i) => Literal(cell, i < columns.Count ? columns[i] : ""))) + ")")));
                    declarations.Append(";\n");
                }
                value = variable;
            }
            else if (p.Output)
            {
                string variable = $"@out{++counter}";
                declarations.Append($"DECLARE {variable} {p.Type} = {Literal(p.Value, p.Type)};\n");
                value = $"{variable} OUTPUT";
            }
            else
            {
                value = Literal(p.Value, p.Type);
            }

            assignments.Add(p.Name.Length > 0 ? $"{p.Name} = {value}" : value);
        }

        sb.Append(declarations);

        if (call.Sql is not null && call.ProcId is TdsConstants.ProcId.ExecuteSql or TdsConstants.ProcId.PrepExec
                or TdsConstants.ProcId.Prepare or TdsConstants.ProcId.Execute)
        {
            if (call.ProcId == TdsConstants.ProcId.Prepare)
            {
                sb.Append($"-- {call.Proc}: prepared only; its executions are replayed as they come\n");
                return true;
            }

            string? paramDecl = call.ParamDecl
                                ?? (call.ProcId == TdsConstants.ProcId.Execute && call.Handle is int handle ? prepared?.Invoke(handle) : null);
            if (call.ProcId == TdsConstants.ProcId.Execute && call.Params.Count > 0 && paramDecl is null) return false;

            sb.Append("EXEC sp_executesql ").Append(Unicode(call.Sql));
            if (!string.IsNullOrEmpty(paramDecl))
            {
                sb.Append(",\n    ").Append(Unicode(paramDecl));
                foreach (string assignment in assignments) sb.Append(",\n    ").Append(assignment);
            }
            sb.Append(";\n");
            return true;
        }

        sb.Append("EXEC ").Append(call.Proc);
        if (assignments.Count > 0) sb.Append(' ').Append(string.Join(", ", assignments));
        sb.Append(";\n");
        return true;
    }

    /// <summary>
    /// The declarations a request's sp_prepare or sp_prepexec prepared, under the handle the recording
    /// says it was given: what a later sp_execute of that handle is rendered with.
    /// </summary>
    public static IEnumerable<(int Handle, string Declarations)> Prepared(RecordedRequest request)
    {
        var response = request.Response;
        foreach (var call in request.Calls)
        {
            if (call.ProcId is not (TdsConstants.ProcId.Prepare or TdsConstants.ProcId.PrepExec) || call.ParamDecl is null) continue;
            if ((response.OutputInt("#0") ?? response.Output.Select(o => response.OutputInt(o.Key)).FirstOrDefault(v => v is not null)) is int handle)
                yield return (handle, call.ParamDecl);
        }
    }

    /// <summary>A recorded value, as DaffyTee wrote it in JSON, as a T-SQL literal of its declared type.</summary>
    public static string Literal(JsonElement value, string declaredType)
    {
        string type = declaredType.ToLowerInvariant();
        switch (value.ValueKind)
        {
            case JsonValueKind.Undefined or JsonValueKind.Null:
                return "NULL";
            case JsonValueKind.True:
                return "1";
            case JsonValueKind.False:
                return "0";
            case JsonValueKind.Number:
                return value.GetRawText();   // exactly as recorded: decimals keep every digit
            case JsonValueKind.Object when value.TryGetProperty("ciphertext", out _):
                return "NULL /* Always Encrypted value: ciphertext only */";
            case JsonValueKind.Object when value.TryGetProperty("undecoded", out var why):
                return $"NULL /* {why.GetString()}, not decoded */";
            case JsonValueKind.String:
            {
                string text = value.GetString() ?? "";
                if (type.StartsWith("binary") || type.StartsWith("varbinary") || type is "image" or "timestamp" or "rowversion")
                {
                    try { return "0x" + Convert.ToHexString(Convert.FromBase64String(text)); }
                    catch (FormatException) { return "NULL /* binary value not readable */"; }
                }
                // datetime holds 1/300 s; as a literal it takes no more than 3 places (error 241).
                // DaffyTee 0.3.1 writes it to the millisecond, but earlier versions wrote up to 7.
                if (type is "datetime" or "smalldatetime"
                    && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var moment))
                    return type == "datetime"
                        ? $"'{moment.AddTicks(5000).ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture)}'"
                        : $"'{moment.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}'";

                bool ansi = type.StartsWith("varchar") || type.StartsWith("char") || type == "text";
                bool numeric = type.StartsWith("decimal") || type.StartsWith("numeric");
                // A decimal(38,x) beyond .NET's range: DaffyTee writes it as a string of digits.
                if (numeric && text.Length > 0 && text.TrimStart('-').All(c => char.IsAsciiDigit(c) || c == '.')) return text;
                return ansi || type.StartsWith("date") || type.StartsWith("time") || type.StartsWith("smalldatetime")
                       || type == "uniqueidentifier"
                    ? $"'{text.Replace("'", "''")}'"
                    : Unicode(text);
            }
            default:
                return "NULL /* value not renderable */";
        }
    }

    private static string Unicode(string s) => $"N'{s.Replace("'", "''")}'";
}
