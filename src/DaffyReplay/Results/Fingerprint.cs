using System.Text;
using System.Text.RegularExpressions;

namespace DaffyReplay.Results;

/// <summary>
/// What groups requests that are "the same statement" for timing and error comparisons: the
/// procedure for a procedure call, otherwise the SQL with its literals replaced and its whitespace
/// and case normalised - so "WHERE Id = 7" and "where id=12" are one statement, run twice.
/// A parameterised statement (sp_executesql, sp_prepexec, a statement prepared and executed by
/// handle) is fingerprinted by its SQL, which is the statement it really is.
/// </summary>
internal static partial class Fingerprint
{
    private const int MaxText = 4000;

    /// <summary>The normalised text and its 16-hex-digit id.</summary>
    public static (string Id, string Text) Of(string kind, string? sql, IReadOnlyList<(string Proc, string? Sql)> calls,
                                             string? transactionOp, string? bulkTable, string? gatewayCommand)
    {
        string text = gatewayCommand is not null ? $"daffytee command {gatewayCommand}"
            : kind switch
            {
                "batch" => Normalize(sql ?? ""),
                "rpc" => string.Join("; ", calls.Select(c => c.Sql is { Length: > 0 } s ? Normalize(s) : "exec " + c.Proc.ToLowerInvariant())),
                "txn" => $"transaction {transactionOp ?? "?"}",
                "bulk" => $"bulk load {bulkTable?.ToLowerInvariant() ?? "?"}",
                _ => kind,
            };

        if (text.Length == 0) text = "(empty)";
        return (Hash(text), text);
    }

    /// <summary>
    /// Literals become ?, comments go, whitespace collapses to one space, case folds - outside
    /// [bracketed] and "quoted" identifiers, whose case is kept. A list of literals - IN (1, 2, 3) -
    /// becomes one: IN (?), so lists of different lengths are one statement.
    /// </summary>
    public static string Normalize(string sql)
    {
        var sb = new StringBuilder(Math.Min(sql.Length, MaxText));
        int i = 0;
        bool space = false;

        void Emit(char c)
        {
            // Punctuation needs no space around it to mean the same: "a = b" and "a=b" are one. A
            // closing parenthesis keeps the space after it, which reads better: "count(*) from".
            if (space && sb.Length > 0 && (!IsPunctuation(sb[^1]) || sb[^1] == ')') && !IsPunctuation(c)) sb.Append(' ');
            space = false;
            sb.Append(c);
        }

        while (i < sql.Length && sb.Length < MaxText)
        {
            char c = sql[i];
            char next = i + 1 < sql.Length ? sql[i + 1] : '\0';
            char previous = space || sb.Length == 0 ? ' ' : sb[^1];

            if (char.IsWhiteSpace(c)) { space = true; i++; continue; }

            if (c == '-' && next == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                space = true;
                continue;
            }

            if (c == '/' && next == '*')
            {
                int end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
                space = true;
                continue;
            }

            if (c == '\'' || (c is 'N' or 'n' && next == '\'' && !IsWordChar(previous)))
            {
                if (c != '\'') i++;
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == '\'' && i + 1 < sql.Length && sql[i + 1] == '\'') { i += 2; continue; }
                    if (sql[i] == '\'') { i++; break; }
                    i++;
                }
                Emit('?');
                continue;
            }

            if (c is '[' or '"')
            {
                char close = c == '[' ? ']' : '"';
                int end = sql.IndexOf(close, i + 1);
                end = end < 0 ? sql.Length - 1 : end;
                Emit(c);
                sb.Append(sql, i + 1, end - i);
                i = end + 1;
                continue;
            }

            if (char.IsAsciiDigit(c) && !IsWordChar(previous) || (c == '.' && char.IsAsciiDigit(next) && !IsWordChar(previous)))
            {
                if (c == '0' && next is 'x' or 'X')
                {
                    i += 2;
                    while (i < sql.Length && char.IsAsciiHexDigit(sql[i])) i++;
                }
                else
                {
                    while (i < sql.Length && (char.IsAsciiDigit(sql[i]) || sql[i] == '.')) i++;
                    if (i < sql.Length && sql[i] is 'e' or 'E')
                    {
                        i++;
                        if (i < sql.Length && sql[i] is '+' or '-') i++;
                        while (i < sql.Length && char.IsAsciiDigit(sql[i])) i++;
                    }
                }
                Emit('?');
                continue;
            }

            Emit(char.ToLowerInvariant(c));
            i++;
        }

        return ValueList().Replace(sb.ToString().Trim(), "(?)");
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '@' or '#' or '$';

    private static bool IsPunctuation(char c) => c is ',' or '(' or ')' or '=' or '<' or '>' or ';';

    [GeneratedRegex(@"\(\?(?:,\?)+\)")]
    private static partial Regex ValueList();

    /// <summary>FNV-1a, 64-bit: stable across runs and machines, which string.GetHashCode is not.</summary>
    public static string Hash(string text)
    {
        ulong hash = 14695981039346656037UL;
        foreach (char c in text)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }
        return hash.ToString("x16");
    }
}
