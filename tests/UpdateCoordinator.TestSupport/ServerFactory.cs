using System.Globalization;
using System.Security.Cryptography.X509Certificates;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

using UpdateCoordinator.Server.Data;
using UpdateCoordinator.Server.Trust;

namespace UpdateCoordinator.TestSupport;

/// <summary>
/// Hosts the central server in memory with its own temporary database, trust anchor and TLS certificate.
/// </summary>
public class ServerFactory : WebApplicationFactory<Program>
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "UpdateCoordinator.Tests", Guid.NewGuid().ToString("N"));
    private readonly MutableCertificateProvider _certificates = new(TestCertificates.Create());

    /// <summary>Registrations allowed per IP and minute; high by default so tests do not interfere.</summary>
    protected virtual int RegisterPermitLimit => 1_000;

    /// <summary>The TLS certificate the server currently presents and signs bindings for.</summary>
    public X509Certificate2 Certificate => _certificates.Certificate;

    /// <summary>Path of the trust anchor file of this server instance.</summary>
    public string AnchorPath => Path.Combine(_directory, "trust-anchor.key");

    /// <summary>One-time setup token file of this server instance.</summary>
    public string SetupTokenPath => Path.Combine(_directory, "setup-token.txt");

    /// <summary>Folder of the rolling log files of this server instance.</summary>
    public string LogDirectory => Path.Combine(_directory, "Logs");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Database:Path", Path.Combine(_directory, "server.db"));
        builder.UseSetting("Trust:AnchorPath", AnchorPath);
        builder.UseSetting("Logging:File:Directory", LogDirectory);
        builder.UseSetting("Administration:SetupTokenPath", SetupTokenPath);
        builder.UseSetting("Administration:AgentLogDirectory", Path.Combine(_directory, "AgentLogs"));
        builder.UseSetting("Agents:RateLimit:RegisterPermitLimit", RegisterPermitLimit.ToString(CultureInfo.InvariantCulture));
        // Tests drive the ring logic explicitly; the worker only runs once at start.
        builder.UseSetting("Rollout:EvaluationInterval", "01:00:00");
        builder.ConfigureTestServices(services => services.AddSingleton<ITlsCertificateProvider>(_certificates));
    }

    /// <summary>Simulates an admin configuring a new TLS certificate and restarting the trust binding step.</summary>
    internal async Task ReplaceCertificateAsync(X509Certificate2 certificate)
    {
        _certificates.Certificate = certificate;
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TrustBindingService>().EnsureCurrentAsync(CancellationToken.None);
    }

    /// <summary>Creates a dashboard account with one role, bypassing the setup.</summary>
    public async Task CreateUserAsync(string userName, string password, string role)
    {
        StartServer();
        await using var scope = Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Microsoft.AspNetCore.Identity.IdentityUser>>();
        var user = new Microsoft.AspNetCore.Identity.IdentityUser(userName);
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            throw new InvalidOperationException(string.Join(" ", created.Errors.Select(e => e.Description)));
        }

        await users.AddToRoleAsync(user, role);
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
        await DeleteDirectoryAsync(_directory);

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Best-effort cleanup of the temporary folder. Windows may release file handles (log file, database)
    /// shortly after the host is disposed, so deletion is retried; a leftover temp folder never fails a test.
    /// </summary>
    private static async Task DeleteDirectoryAsync(string directory)
    {
        for (var attempt = 0; attempt < 10 && Directory.Exists(directory); attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                await Task.Delay(100);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(100);
            }
        }
    }

    private sealed class MutableCertificateProvider(X509Certificate2 certificate) : ITlsCertificateProvider
    {
        public X509Certificate2 Certificate { get; set; } = certificate;
    }
}

/// <summary>Variant with a tight registration limit for rate limiting tests.</summary>
public sealed class RateLimitedServerFactory : ServerFactory
{
    public const int Limit = 2;

    protected override int RegisterPermitLimit => Limit;
}
