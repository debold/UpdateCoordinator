using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using UpdateCoordinator.Server.Agents;
using UpdateCoordinator.Server.Data;

namespace UpdateCoordinator.Server;

/// <summary>
/// Composition of the central server services, kept out of Program for readability.
/// </summary>
internal static class ServerServiceCollectionExtensions
{
    public static IServiceCollection AddCentralServer(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddProblemDetails();

        // Malformed request bodies are client errors (400) in every environment, never server errors.
        services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
        services.AddValidation();
        services.AddHealthChecks();
        services.AddOpenApi();

        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddDbContext<ServerDbContext>((provider, options) =>
        {
            var path = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.Path;
            var connection = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true };
            options.UseSqlite(connection.ToString());
        });

        services.AddAuthentication(AgentAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, AgentAuthenticationHandler>(AgentAuthenticationHandler.SchemeName, null);
        services.AddAuthorization();

        services.AddOptions<AgentRateLimitOptions>()
            .Bind(configuration.GetSection(AgentRateLimitOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(AgentEndpoints.RegisterRateLimitPolicy, httpContext =>
            {
                var limits = httpContext.RequestServices.GetRequiredService<IOptions<AgentRateLimitOptions>>().Value;
                var partition = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limits.RegisterPermitLimit,
                    Window = limits.Window,
                    QueueLimit = 0,
                });
            });
        });

        return services;
    }

    /// <summary>Creates the database directory if needed and applies pending schema migrations.</summary>
    public static async Task MigrateDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var path = scope.ServiceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value.Path;
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var db = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }
}
