namespace DaffyReplay;

/// <summary>
/// Where DaffyReplay keeps its state: the encrypted target store and its key.
///
/// Per user rather than per directory, as in DaffyTee: %LOCALAPPDATA%\DaffyReplay on Windows,
/// ~/.local/share/DaffyReplay elsewhere. DAFFYREPLAY_HOME or --config-dir moves it, which is how a
/// test run gets a store of its own.
/// </summary>
internal static class AppPaths
{
    private static string? _home;

    public static string Home
    {
        get => _home ??= Environment.GetEnvironmentVariable("DAFFYREPLAY_HOME") is { Length: > 0 } fromEnvironment
            ? Path.GetFullPath(fromEnvironment)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
                                          Environment.SpecialFolderOption.Create),
                "DaffyReplay");
        set => _home = Path.GetFullPath(value);
    }

    public static string EnsureHome()
    {
        Directory.CreateDirectory(Home);
        return Home;
    }
}
