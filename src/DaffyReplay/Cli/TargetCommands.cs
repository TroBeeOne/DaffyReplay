using DaffyReplay.Security;
using DaffyReplay.Target;

namespace DaffyReplay.Cli;

/// <summary>What logging in to a target said: who answered and how, or why it failed.</summary>
internal sealed record TargetCheck(string? Summary, string? Detail, string? Failure, string? Problem)
{
    public bool Ok => Summary is not null;

    /// <summary>
    /// Logs in through the same connector a replay uses, so "tested" means "a replay will be able to
    /// do this", not merely "the connection string parsed".
    /// </summary>
    public static async Task<TargetCheck> RunAsync(TargetEndpoint endpoint, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await TargetConnector.ConnectAsync(
                endpoint, TargetConnector.CreateLogin(endpoint), mars: false, cancellationToken);

            var login = connection.Login;
            string summary = $"{login.ServerProgram} {login.ServerVersion}".Trim();
            string routed = connection.RoutedTo is { } to ? $", redirected to {to}" : "";
            return new TargetCheck(summary,
                $"{summary}, database '{login.Database}', {connection.Security}, packet size {connection.PacketSize}, " +
                $"{connection.Elapsed.TotalMilliseconds:F0} ms{routed}", null, null);
        }
        catch (ConnectException ex)
        {
            return new TargetCheck(null, null, ex.Failure.ToString(), ex.Message);
        }
    }
}

/// <summary>
/// `daffyreplay target ...` - the servers a replay may go to, and the logins it uses there.
///
/// A target is only saved once it has actually been logged in to, through the same connector a
/// replay uses. Secrets are typed at prompts, never put on a command line, and stored encrypted.
/// </summary>
internal sealed class TargetCommands(TargetStore store, TextWriter output, Prompter prompter)
{
    public static string Usage => """
          daffyreplay target add NAME [--target "CONNECTION STRING"]
              Name a replay target. Prompts for the server, encryption and the default login (or
              takes them from --target; a password missing from it is prompted for), logs in to
              prove it works, and only then saves. The password is stored encrypted.
          daffyreplay target list
          daffyreplay target show NAME
          daffyreplay target test NAME          log in again, now, as the default and every mapped login
          daffyreplay target remove NAME
          daffyreplay target logins NAME        the recorded logins mapped to logins on this target
          daffyreplay target logins NAME add RECORDED [--login USER]
              Replay sessions recorded as RECORDED (a SQL Server login, or daffytee:NAME for one
              DaffyTee login) as USER on this target. Prompts for USER's password and tests it.
          daffyreplay target logins NAME remove RECORDED

        """;

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count == 0 || args[0] is "help" or "--help" or "-h")
        {
            output.WriteLine(Usage);
            return args.Count == 0 ? 1 : 0;
        }

        string command = args[0].ToLowerInvariant();
        string? name = args.Count > 1 && !args[1].StartsWith("--") ? args[1] : null;

        try
        {
            switch (command)
            {
                case "list" or "ls":
                    List();
                    return 0;

                case "add" when name is not null:
                    return await AddAsync(name, Option(args, "--target"), cancellationToken);

                case "show" when name is not null:
                    return Show(name);

                case "test" when name is not null:
                    return await TestAsync(name, cancellationToken);

                case "remove" or "rm" or "delete" when name is not null:
                    return Remove(name);

                case "logins" when name is not null:
                    return await LoginsAsync(name, args.Skip(2).ToList(), cancellationToken);

                default:
                    output.WriteLine($"  unknown or incomplete command: target {string.Join(' ', args)}");
                    output.WriteLine(Usage);
                    return 1;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or EndOfStreamException)
        {
            output.WriteLine($"  {ex.Message}");
            return 1;
        }
    }

    private void List()
    {
        var targets = store.Targets;
        if (targets.Count == 0)
        {
            output.WriteLine("  no targets yet - add one with: daffyreplay target add NAME");
            return;
        }

        var rows = targets.Select(t => new[]
        {
            t.Name,
            Describe(t),
            t.Logins.Count == 0 ? "" : t.Logins.Count.ToString(),
            t.LastTestedUtc is { } v ? v.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "never",
            t.LastTest ?? "",
        }).ToList();

        TextTable.Write(output, ["name", "server", "mapped logins", "tested", "answered"], rows);
    }

    private int Show(string name)
    {
        var target = Require(name);
        output.WriteLine();
        output.WriteLine($"  name       : {target.Name}");
        output.WriteLine($"  server     : {Describe(target)}");
        if (TryEndpoint(target) is { } endpoint)
            output.WriteLine($"  encryption : {endpoint.DescribeSecurity()}");
        output.WriteLine($"  tested     : {(target.LastTestedUtc is { } v ? $"{v.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {target.LastTest}" : "never")}");
        output.WriteLine($"  logins     : {(target.Logins.Count == 0 ? "none mapped - every session uses the default login" : "")}");
        foreach (var login in target.Logins.OrderBy(l => l.Recorded, StringComparer.OrdinalIgnoreCase))
            output.WriteLine($"               {login.Recorded} -> {login.UserName}");
        output.WriteLine($"  created    : {target.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        output.WriteLine($"  updated    : {target.UpdatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        output.WriteLine();
        return 0;
    }

    private async Task<int> AddAsync(string name, string? connectionString, CancellationToken cancellationToken)
    {
        TargetStore.ValidateName(name);
        if (store.Find(name) is not null)
        {
            output.WriteLine($"  '{name}' already exists - remove it first to change it");
            return 1;
        }

        var endpoint = connectionString is not null ? Complete(TargetEndpoint.Parse(connectionString)) : Ask();

        var summary = await TestOneAsync(endpoint, cancellationToken);
        if (summary is null)
        {
            output.WriteLine($"  '{name}' was NOT saved.");
            return 1;
        }

        var now = DateTime.UtcNow;
        store.Save(new TargetRecord
        {
            Name = name,
            Target = endpoint.ToConnectionString(),
            CreatedUtc = now,
            UpdatedUtc = now,
            LastTestedUtc = now,
            LastTest = summary,
        });
        output.WriteLine($"  saved '{name}' -> {endpoint.Describe()}");
        return 0;
    }

    private async Task<int> TestAsync(string name, CancellationToken cancellationToken)
    {
        var target = Require(name);
        var summary = await TestOneAsync(target.Endpoint, cancellationToken);
        bool allOk = summary is not null;

        foreach (var login in target.Logins)
        {
            output.WriteLine($"  mapped login {login.Recorded} -> {login.UserName}:");
            var mapped = target.EndpointFor(login.IsDaffyTeeLogin ? login.Recorded[LoginMapping.DaffyTeePrefix.Length..] : null,
                                            login.IsDaffyTeeLogin ? null : login.Recorded, out _);
            if (await TestOneAsync(mapped, cancellationToken) is null) allOk = false;
            else login.LastTestedUtc = DateTime.UtcNow;
        }

        if (summary is not null)
        {
            target.LastTestedUtc = DateTime.UtcNow;
            target.LastTest = summary;
            store.Save(target);
        }
        return allOk ? 0 : 1;
    }

    private int Remove(string name)
    {
        var target = Require(name);
        if (!prompter.Confirm($"remove '{target.Name}' and its stored credentials", defaultValue: false))
        {
            output.WriteLine("  not removed");
            return 1;
        }

        store.Remove(target.Name);
        output.WriteLine($"  removed '{target.Name}'");
        return 0;
    }

    private async Task<int> LoginsAsync(string name, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var target = Require(name);
        if (args.Count == 0)
        {
            if (target.Logins.Count == 0)
            {
                output.WriteLine($"  no logins mapped on '{target.Name}': every replayed session logs in as {target.Endpoint.UserName}");
                output.WriteLine("  map one with: daffyreplay target logins NAME add RECORDED_LOGIN [--login USER]");
                return 0;
            }

            var rows = target.Logins.OrderBy(l => l.Recorded, StringComparer.OrdinalIgnoreCase)
                .Select(l => new[] { l.Recorded, l.UserName, l.LastTestedUtc is { } v ? v.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "never" })
                .ToList();
            TextTable.Write(output, ["recorded login", "replays as", "tested"], rows);
            return 0;
        }

        string verb = args[0].ToLowerInvariant();
        string? recorded = args.Count > 1 && !args[1].StartsWith("--") ? args[1] : null;
        if (recorded is null)
        {
            output.WriteLine("  usage: daffyreplay target logins NAME add|remove RECORDED_LOGIN [--login USER]");
            return 1;
        }

        switch (verb)
        {
            case "add" or "set":
            {
                string user = Option(args, "--login") ?? prompter.Ask($"login on {target.Name} for sessions recorded as {recorded}");
                if (user.Length == 0) throw new ArgumentException("a login is required");

                var endpoint = target.Endpoint with { UserName = user, Password = "" };
                string password = prompter.AskSecret(endpoint.SecretName);
                if (password.Length == 0) throw new ArgumentException("a password is required");
                endpoint = endpoint with { Password = password };

                if (await TestOneAsync(endpoint, cancellationToken) is null)
                {
                    output.WriteLine($"  '{recorded}' was NOT mapped.");
                    return 1;
                }

                target.Logins.RemoveAll(l => string.Equals(l.Recorded, recorded, StringComparison.OrdinalIgnoreCase));
                target.Logins.Add(new LoginMapping
                {
                    Recorded = recorded, UserName = user, Password = password,
                    Authentication = endpoint.Authentication, LastTestedUtc = DateTime.UtcNow,
                });
                target.UpdatedUtc = DateTime.UtcNow;
                store.Save(target);
                output.WriteLine($"  sessions recorded as {recorded} replay as {user} on '{target.Name}'");
                return 0;
            }

            case "remove" or "rm":
            {
                int removed = target.Logins.RemoveAll(l => string.Equals(l.Recorded, recorded, StringComparison.OrdinalIgnoreCase));
                if (removed == 0)
                {
                    output.WriteLine($"  '{recorded}' is not mapped on '{target.Name}'");
                    return 1;
                }
                target.UpdatedUtc = DateTime.UtcNow;
                store.Save(target);
                output.WriteLine($"  '{recorded}' unmapped: its sessions now use the default login");
                return 0;
            }

            default:
                output.WriteLine("  usage: daffyreplay target logins NAME add|remove RECORDED_LOGIN [--login USER]");
                return 1;
        }
    }

    /// <summary>Logs in and reports what answered. Returns a one-line summary, or null after explaining the failure.</summary>
    private async Task<string?> TestOneAsync(TargetEndpoint endpoint, CancellationToken cancellationToken)
    {
        output.WriteLine($"  logging in to {endpoint.Describe()} ({endpoint.DescribeSecurity()}) ...");
        var check = await TargetCheck.RunAsync(endpoint, cancellationToken);
        if (check.Ok)
        {
            output.WriteLine($"  ok: {check.Detail}");
            return check.Summary;
        }

        output.WriteLine($"  FAILED ({check.Failure}): {check.Problem}");
        return null;
    }

    /// <summary>A connection string given with --target: a password missing from it is asked for.</summary>
    private TargetEndpoint Complete(TargetEndpoint endpoint) =>
        endpoint.Password.Length == 0 ? endpoint with { Password = prompter.AskSecret(endpoint.SecretName) } : endpoint;

    private TargetEndpoint Ask()
    {
        output.WriteLine("  replay target:");
        string server = prompter.Ask("server (host or host,port)");
        var (host, port) = TargetEndpoint.ParseServer(server);

        string user = prompter.Ask("default login");
        if (user.Length == 0) throw new ArgumentException("a login is required");

        string password = prompter.AskSecret($"password for {user}");
        if (password.Length == 0) throw new ArgumentException("the login's password is required");

        string database = prompter.Ask("default database (blank: each session's recorded database)", "");
        string encryptText = prompter.Ask("encrypt: Mandatory, Optional or Strict", TargetEncrypt.Mandatory.ToString());
        var encrypt = Enum.TryParse<TargetEncrypt>(encryptText, ignoreCase: true, out var e)
            ? e
            : throw new ArgumentException($"'{encryptText}' is not Mandatory, Optional or Strict");

        bool trust = encrypt != TargetEncrypt.Optional &&
                     prompter.Confirm("trust the server certificate without validating it", false);

        return new TargetEndpoint(host, port, user, password, database.Length == 0 ? null : database,
                                  encrypt, trust, null, TargetEndpoint.DefaultConnectTimeout);
    }

    private TargetRecord Require(string name) =>
        store.Find(name) ?? throw new ArgumentException($"no target '{name}' - 'daffyreplay target list' shows them");

    private static TargetEndpoint? TryEndpoint(TargetRecord target)
    {
        try { return target.Endpoint; }
        catch (FormatException) { return null; }
    }

    private static string Describe(TargetRecord target) => TryEndpoint(target)?.Describe() ?? "(unreadable target)";

    private static string? Option(IReadOnlyList<string> args, string name)
    {
        for (int i = 0; i < args.Count - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}
