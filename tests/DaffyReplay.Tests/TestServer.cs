using DaffyReplay.Target;

namespace DaffyReplay.Tests;

/// <summary>
/// The SQL Server the integration tests use: docker/compose.yml's target, seeded with docker/seed.sql.
/// They run only when DAFFYREPLAY_TEST_SERVER names it (e.g. 127.0.0.1,14362), so the unit tests
/// stay runnable anywhere, with no network.
/// </summary>
internal static class TestServer
{
    public static string? Server => Environment.GetEnvironmentVariable("DAFFYREPLAY_TEST_SERVER") is { Length: > 0 } s ? s : null;

    private const string Credentials = "User Id=svc_app;Password=Svc!App2026";

    public static TargetEndpoint Endpoint => TargetEndpoint.Parse(
        $"Server=tcp:{Server};{Credentials};Initial Catalog=ReplayTest;Encrypt=Mandatory;TrustServerCertificate=True");

    /// <summary>For Microsoft.Data.SqlClient, which the tests use to set up data and get reference answers.</summary>
    public static string SqlClient =>
        $"Server=tcp:{Server};{Credentials};Initial Catalog=ReplayTest;Encrypt=Mandatory;TrustServerCertificate=True;Pooling=False";
}

/// <summary>A test that needs the SQL Server: skipped, with the reason, when none is configured.</summary>
public sealed class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute()
    {
        if (TestServer.Server is null) Skip = "set DAFFYREPLAY_TEST_SERVER (e.g. 127.0.0.1,14362) to run against docker/compose.yml's target";
    }
}
