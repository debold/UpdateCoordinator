using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using SUSyphos.Server.Agents;
using SUSyphos.Server.Data;
using SUSyphos.Server.Trust;

namespace SUSyphos.Server;

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
            options.UseSqlite(connection.ToString())
                // SQLite table rebuilds switch foreign keys off outside the transaction; expected, not worth a warning on every first start.
                .ConfigureWarnings(warnings => warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.NonTransactionalMigrationOperationWarning));
        });

        services.AddOptions<TrustOptions>()
            .Bind(configuration.GetSection(TrustOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<TrustAnchor>();
        services.AddSingleton<ITlsCertificateProvider, StoreTlsCertificateProvider>();
        services.AddSingleton<IServerCertificateStore, WindowsServerCertificateStore>();
        services.AddOptions<CertificateOptions>()
            .Bind(configuration.GetSection(CertificateOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<CertificateLifecycle>();
        services.AddHostedService<CertificateWorker>();
        services.AddScoped<CertificateAdministration>();
        services.AddScoped<TrustBindingService>();
        services.AddScoped<UpdateCatalog>();
        services.AddScoped<ClientActivityLog>();
        services.AddScoped<InventoryService>();
        services.AddScoped<UpdateReportService>();
        services.AddScoped<InstallationService>();
        services.AddOptions<InstallationControlOptions>().Bind(configuration.GetSection(InstallationControlOptions.SectionName));
        services.AddScoped<BlackoutPeriods>();
        services.AddScoped<GlobalPause>();
        services.AddScoped<ServerMove>();
        services.AddScoped<AgentActionService>();

        // Kestrel presents the certificate the trust binding was signed for.
        services.AddOptions<KestrelServerOptions>()
            .Configure<ITlsCertificateProvider>((kestrel, provider) =>
                kestrel.ConfigureHttpsDefaults(https => https.ServerCertificateSelector = (_, _) => provider.Certificate));

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

    /// <summary>
    /// Startup work before requests are served: applies pending schema migrations and makes sure
    /// a trust binding for the current TLS certificate exists; a certificate the start created or switched to is audited.
    /// </summary>
    public static async Task InitializeCentralServerAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await services.MigrateDatabaseAsync(cancellationToken).ConfigureAwait(false);

        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TrustBindingService>().EnsureCurrentAsync(cancellationToken).ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<CertificateAdministration>().AuditStartupChangeAsync(cancellationToken).ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<Assignment.AssignmentAdministration>().RenumberDuplicatePrioritiesAsync(cancellationToken).ConfigureAwait(false);
        if (scope.ServiceProvider.GetRequiredService<IOptions<Rollouts.RolloutOptions>>().Value.CreateDefinitionRule)
        {
            await scope.ServiceProvider.GetRequiredService<Rollouts.RolloutAdministration>().EnsureDefinitionRuleAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Creates the database directory if needed and applies pending schema migrations.</summary>
    private static async Task MigrateDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken)
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
