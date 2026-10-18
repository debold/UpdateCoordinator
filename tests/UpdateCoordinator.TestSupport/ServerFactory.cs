using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

using UpdateCoordinator.Server.Data;

namespace UpdateCoordinator.TestSupport;

/// <summary>
/// Hosts the central server in memory with its own temporary SQLite database.
/// </summary>
public class ServerFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), "UpdateCoordinator.Tests", $"{Guid.NewGuid():N}.db");

    /// <summary>Registrations allowed per IP and minute; high by default so tests do not interfere.</summary>
    protected virtual int RegisterPermitLimit => 1_000;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Database:Path", _databasePath);
        builder.UseSetting("Agents:RateLimit:RegisterPermitLimit", RegisterPermitLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    internal async Task<T> WithDbAsync<T>(Func<ServerDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<ServerDbContext>());
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>Variant with a tight registration limit for rate limiting tests.</summary>
public sealed class RateLimitedServerFactory : ServerFactory
{
    public const int Limit = 2;

    protected override int RegisterPermitLimit => Limit;
}
