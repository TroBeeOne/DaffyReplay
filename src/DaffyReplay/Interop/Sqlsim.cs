namespace DaffyReplay.Interop;

/// <summary>
/// sqlsim's replay XML (Microsoft's sqlsimtools: github.com/microsoft/sqlsimtools): SQL Server's
/// Extended Events event_data, one &lt;event&gt; per sql_batch_completed, rpc_completed, login and
/// logout, wrapped in a &lt;replay&gt; root - what its convert-xel.ps1 writes from an .xel file.
/// <c>daffyreplay export sqlsim</c> writes it from a DaffyTee recording; <c>daffyreplay import
/// sqlsim</c> reads it into a recording DaffyReplay can run. What they share is here.
/// </summary>
internal static class Sqlsim
{
    /// <summary>What an imported recording's proxy.start and responses say they came from.</summary>
    public const string Name = "sqlsim";

    /// <summary>sqlsim 1.0.0 stops at more events than this; so does sqlsim Studio.</summary>
    public const int MaxEvents = 100_000;

    /// <summary>sqlsim Studio refuses a larger file (100,000,000 characters); sqlsim itself, past 100 MiB.</summary>
    public const long MaxBytes = 100_000_000;

    /// <summary>How the warning about an imported recording begins: the report shows it as its own callout.</summary>
    public const string ImportedWarningStart = "Imported from sqlsim's replay XML";

    /// <summary>What a recording imported from sqlsim's XML (an Extended Events capture) cannot tell: the report lists these.</summary>
    public static readonly IReadOnlyList<(string Lead, string Detail)> ImportLimits =
    [
        ("Transactions the application ran through its driver are not in it",
         "SqlTransaction, EF Core and JDBC with autocommit off begin and end them with requests Extended Events has no event for, " +
         "so their statements replay one at a time and work the application rolled back is committed. BEGIN TRAN written in the SQL is there"),
        ("Errors raised while a statement ran were recorded as OK",
         "a divide by zero, a duplicate key or a RAISERROR leaves the event's result OK, so a new error may have happened when recorded too"),
        ("Rows are known per batch, not per statement",
         "a batch's total is compared; a procedure call's is not, since the text Extended Events wrote for it adds statements of its own"),
        ("Every request replays as T-SQL text",
         "procedure calls as Extended Events wrote them, with literal values rather than typed parameters; nothing is byte for byte"),
        ("Logins, bulk loads and cursors",
         "sessions replay as the target's login unless the capture had the username action; bulk loads and cursor calls are not in the XML"),
    ];

    /// <summary>The one-line version, for inspect and the console; the report lists <see cref="ImportLimits"/> in full.</summary>
    public static string ImportedWarning(string? file) =>
        $"{ImportedWarningStart}{(file is null ? "" : $" ({file})")}, an Extended Events capture: transactions the application ran " +
        "through its driver are not in it, errors raised while a statement ran were recorded as OK, rows are known per batch only, " +
        "and every request replays as T-SQL text. The report spells out what that means";
}
