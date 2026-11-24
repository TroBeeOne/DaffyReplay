using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DaffyReplay.Target;

namespace DaffyReplay.Security;

/// <summary>
/// A login on the replay target that stands in for one recorded login. Passwords are never
/// recorded, so every login a replay uses comes from here or from the target's default.
/// </summary>
internal sealed class LoginMapping
{
    /// <summary>
    /// The recorded SQL Server login (the event's targetLogin), or "daffytee:NAME" for sessions of
    /// one DaffyTee login (the event's login), which wins over the SQL Server login when both match.
    /// </summary>
    public string Recorded { get; set; } = "";

    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>SqlPassword, or EntraServicePrincipal (UserName is the application id, Password its secret).</summary>
    public TargetAuthentication Authentication { get; set; } = TargetAuthentication.SqlPassword;

    public DateTime? LastTestedUtc { get; set; }

    public const string DaffyTeePrefix = "daffytee:";

    public bool IsDaffyTeeLogin => Recorded.StartsWith(DaffyTeePrefix, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A replay target: where replays go, as whom by default, and as whom for particular recorded logins.</summary>
internal sealed class TargetRecord
{
    public string Name { get; set; } = "";

    /// <summary>The connection string, password included. Readable only inside the sealed store.</summary>
    public string Target { get; set; } = "";

    public List<LoginMapping> Logins { get; set; } = [];

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? LastTestedUtc { get; set; }

    /// <summary>What answered when last tested, e.g. "Microsoft SQL Server 16.0.4295".</summary>
    public string? LastTest { get; set; }

    [JsonIgnore]
    public TargetEndpoint Endpoint => TargetEndpoint.Parse(Target);

    /// <summary>
    /// The endpoint a recorded session logs in with: its DaffyTee login's mapping, else its SQL
    /// Server login's, else the target's default login. <paramref name="mapping"/> says which.
    /// </summary>
    public TargetEndpoint EndpointFor(string? daffyTeeLogin, string? targetLogin, out LoginMapping? mapping)
    {
        mapping = (daffyTeeLogin is { Length: > 0 } ? FindLogin(LoginMapping.DaffyTeePrefix + daffyTeeLogin) : null)
                  ?? (targetLogin is { Length: > 0 } ? FindLogin(targetLogin) : null);

        var endpoint = Endpoint;
        return mapping is null
            ? endpoint
            : endpoint with { UserName = mapping.UserName, Password = mapping.Password, Authentication = mapping.Authentication };
    }

    public LoginMapping? FindLogin(string recorded) =>
        Logins.FirstOrDefault(l => string.Equals(l.Recorded, recorded, StringComparison.OrdinalIgnoreCase));
}

internal sealed class TargetDocument
{
    public int Version { get; set; } = 1;
    public List<TargetRecord> Targets { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             UseStringEnumConverter = true)]
[JsonSerializable(typeof(TargetDocument))]
internal sealed partial class TargetJsonContext : JsonSerializerContext;

/// <summary>
/// The replay targets, encrypted at rest as a single sealed blob - the same arrangement as
/// DaffyTee's user store, under DaffyReplay's own key: nothing about a target, not even its name,
/// can be read off disk.
///
/// Writes take an exclusive lock file and re-read the store before changing it, so two commands
/// in two terminals never overwrite each other.
/// </summary>
internal sealed class TargetStore
{
    private const string FileName = "targets.dat";
    private const string LockName = "targets.lock";
    private const string Purpose = "DaffyReplay.targets.v1";
    private static readonly byte[] Magic = "DRS1"u8.ToArray();

    private readonly string _directory;
    private readonly SecretProtector _protector;

    private TargetStore(string directory, SecretProtector protector)
    {
        _directory = directory;
        _protector = protector;
    }

    public string FilePath => Path.Combine(_directory, FileName);

    public string KeyDescription => _protector.KeyDescription;

    public static TargetStore Open(string directory) => new(directory, SecretProtector.LoadOrCreate(directory));

    public IReadOnlyList<TargetRecord> Targets =>
        Read().Targets.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public TargetRecord? Find(string name) => Read().Targets.FirstOrDefault(t => SameName(t.Name, name));

    /// <summary>Adds or replaces a target, matched by name without regard to case.</summary>
    public void Save(TargetRecord record)
    {
        ValidateName(record.Name);
        Mutate(document =>
        {
            document.Targets.RemoveAll(t => SameName(t.Name, record.Name));
            document.Targets.Add(record);
        });
    }

    public bool Remove(string name)
    {
        bool removed = false;
        Mutate(document => removed = document.Targets.RemoveAll(t => SameName(t.Name, name)) > 0);
        return removed;
    }

    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("a target name is required");
        if (name != name.Trim()) throw new ArgumentException("a target name cannot start or end with a space");
        if (name.Length > 128) throw new ArgumentException("a target name can be at most 128 characters");
        if (name.StartsWith('-')) throw new ArgumentException("a target name cannot start with '-'");
    }

    private static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private TargetDocument Read()
    {
        if (!File.Exists(FilePath)) return new TargetDocument();

        byte[] file = ReadShared(FilePath);
        if (!file.AsSpan().StartsWith(Magic))
            throw new CryptographicException($"{FilePath} is not a DaffyReplay target store");

        byte[] json = _protector.Unseal(file.AsSpan(Magic.Length), Purpose);
        try
        {
            return JsonSerializer.Deserialize(json, TargetJsonContext.Default.TargetDocument) ?? new TargetDocument();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    /// <summary>Read-modify-write under an exclusive lock, so two processes never overwrite each other.</summary>
    private void Mutate(Action<TargetDocument> change)
    {
        using var fileLock = AcquireLock();
        var document = Read();
        change(document);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(document, TargetJsonContext.Default.TargetDocument);
        byte[] sealedBytes = [.. Magic, .. _protector.Seal(json, Purpose)];
        CryptographicOperations.ZeroMemory(json);

        // Write beside, then rename over: a crash mid-write leaves the old store, never half a new one.
        string temporary = FilePath + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temporary, options))
        {
            stream.Write(sealedBytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, FilePath, overwrite: true);
    }

    private FileStream AcquireLock()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, LockName);
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>Reads while another process may be renaming a new version into place.</summary>
    private static byte[] ReadShared(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                return memory.ToArray();
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(25);
            }
        }
    }
}
