using System.Globalization;
using System.Text.Json;

namespace DaffyReplay.Recording;

/// <summary>
/// DaffyTee 0.3.0 and earlier decoded a <c>datetime</c> wrong when its time of day was after
/// 00:02:23: the 1/300-second units overflowed, leaving the date right and a time between 00:00:00
/// and 00:02:23, often with 7 decimal places. Raw bytes were never affected, so an exact replay is
/// not; but an approximate one renders a procedure call from its decoded values, and would send the
/// wrong time or a literal SQL Server refuses. DaffyTee 0.3.1 decodes them right, to the millisecond.
/// </summary>
internal static class OldDatetimes
{
    private static readonly Version Fixed = new(0, 3, 1);

    /// <summary>Whether a DaffyTee version decoded datetimes wrong; null when the version is not known.</summary>
    public static bool? Affects(string? daffyTeeVersion)
    {
        if (daffyTeeVersion is null) return null;
        string number = daffyTeeVersion.Split('-', '+')[0];
        return Version.TryParse(number, out var version) ? version < Fixed : null;
    }

    /// <summary>
    /// Whether a request's procedure calls carry a datetime value - a parameter, or a table-valued
    /// parameter's cell - that may be wrong. From an affected DaffyTee that is any value with a time of
    /// day: only midnight is certain, as no overflow lands on it. When the version is not known (a file
    /// that starts after a daily rollover has no proxy.start), a value with more than three decimal
    /// places gives an affected DaffyTee away: 0.3.1 never writes more.
    /// </summary>
    public static bool InRequest(JsonElement request, bool? affected)
    {
        if (affected == false) return false;
        foreach (var call in RecordedRequest.Array(request, "calls"))
        {
            foreach (var p in RecordedRequest.Array(call, "params"))
            {
                if (!p.TryGetProperty("value", out var value)) continue;
                if (IsDatetime(RecordedRequest.Text(p, "type")) && Suspect(value, affected)) return true;
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("tableType", out _)) continue;

                var columns = RecordedRequest.Array(value, "columns").Select((c, i) => (Datetime: IsDatetime(RecordedRequest.Text(c, "type")), Index: i))
                                                                 .Where(c => c.Datetime).Select(c => c.Index).ToList();
                if (columns.Count == 0) continue;
                foreach (var row in RecordedRequest.Array(value, "rows"))
                {
                    if (row.ValueKind != JsonValueKind.Array) continue;
                    var cells = row.EnumerateArray().ToList();
                    if (columns.Any(i => i < cells.Count && Suspect(cells[i], affected))) return true;
                }
            }
        }
        return false;
    }

    private static bool IsDatetime(string? type) => string.Equals(type, "datetime", StringComparison.OrdinalIgnoreCase);

    private static bool Suspect(JsonElement value, bool? affected)
    {
        if (value.ValueKind != JsonValueKind.String) return false;
        string text = value.GetString() ?? "";
        if (affected is null)
        {
            int dot = text.LastIndexOf('.');
            return dot >= 0 && text.Length - dot - 1 > 3;
        }
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var moment)
               && moment.TimeOfDay != TimeSpan.Zero;
    }
}
