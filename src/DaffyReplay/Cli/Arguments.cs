using System.Globalization;
using System.Text.RegularExpressions;
using DaffyReplay.Recording;

namespace DaffyReplay.Cli;

/// <summary>
/// A command's arguments: positional values (recording files, a run folder) and --options. Each
/// option is declared as taking a value or not, so a value is never mistaken for a file.
/// </summary>
internal sealed class Arguments
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Positional { get; } = [];

    public static Arguments Parse(IReadOnlyList<string> args, IReadOnlySet<string> withValue, IReadOnlySet<string> flags)
    {
        var parsed = new Arguments();
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith("--"))
            {
                parsed.Positional.Add(arg);
                continue;
            }

            if (withValue.Contains(arg))
            {
                if (i + 1 >= args.Count) throw new ArgumentException($"{arg} needs a value");
                parsed._options[arg] = args[++i];
            }
            else if (flags.Contains(arg))
            {
                parsed._options[arg] = null;
            }
            else
            {
                throw new ArgumentException($"unknown option {arg}");
            }
        }
        return parsed;
    }

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Get(string name) => _options.GetValueOrDefault(name);

    public int? Int(string name, int min = int.MinValue)
    {
        if (Get(name) is not { } text) return null;
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < min)
            throw new ArgumentException($"{name} needs a whole number{(min > int.MinValue ? $" of at least {min}" : "")}, not '{text}'");
        return value;
    }

    public double? Double(string name, double min)
    {
        if (Get(name) is not { } text) return null;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || value < min)
            throw new ArgumentException($"{name} needs a number of at least {min}, not '{text}'");
        return value;
    }

    // ---- the filters inspect and run share ------------------------------------------------------

    public static readonly string[] FilterOptions = ["--from", "--to", "--app", "--login", "--database", "--session", "--exclude"];

    public static readonly string[] FilterFlags = ["--no-bulk"];

    public const string FilterUsage = """
                --from T --to T       recorded start times, ISO 8601: 2026-10-10T14:00:00Z (UTC unless
                                      an offset is given, as in the recording)
                --app X --login X     sessions of one application, or one login (the DaffyTee login or
                                      the SQL Server login); * matches anything: --app "Orders*"
                --database X          requests that ran in database X
                --session N           one session, by DaffyTee's session number
                --exclude PATTERN     leave out requests whose SQL matches PATTERN (a regular
                                      expression, case-insensitive)
                --no-bulk             leave out bulk loads, and the INSERT BULK statements that
                                      announce them

        """;

    public ReplayFilter Filter() => new(
        From: Time("--from"),
        To: Time("--to"),
        App: Get("--app"),
        Login: Get("--login"),
        Database: Get("--database"),
        Session: Int("--session", 0),
        Exclude: Get("--exclude") is { } pattern ? Pattern(pattern) : null,
        NoBulk: Has("--no-bulk"));

    private DateTime? Time(string name)
    {
        if (Get(name) is not { } text) return null;
        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var value))
            throw new ArgumentException($"{name} needs a date and time such as 2026-10-10T14:00:00Z, not '{text}'");
        return DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

    private static Regex Pattern(string pattern)
    {
        try { return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
        catch (ArgumentException ex) { throw new ArgumentException($"--exclude '{pattern}' is not a regular expression: {ex.Message}"); }
    }
}
