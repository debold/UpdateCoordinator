using Microsoft.AspNetCore.Identity;

using UpdateCoordinator.Server.Approval;
using UpdateCoordinator.Server.Assignment;
using UpdateCoordinator.Server.Data;
using UpdateCoordinator.Server.Rollouts;

namespace UpdateCoordinator.Server.Administration;

/// <summary>
/// Web administration: local accounts (ASP.NET Core Identity), roles, login cookie, audit log and the Blazor dashboard.
/// </summary>
internal static class DashboardServiceCollectionExtensions
{
    public const string LoginPath = "/login";
    public const string LogoutPath = "/logout";
    public const string SetupPath = "/setup";
    public const string AccessDeniedPath = "/access-denied";

    public static IServiceCollection AddDashboard(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AdministrationOptions>()
            .Bind(configuration.GetSection(AdministrationOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddIdentityCore<IdentityUser>(options =>
            {
                options.Password.RequiredLength = 12;
                // Length over composition rules (NIST SP 800-63B): long passphrases are allowed and encouraged.
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequireDigit = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireLowercase = false;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.User.RequireUniqueEmail = false;
                options.User.AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._@";
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ServerDbContext>()
            .AddSignInManager();

        services.AddAuthentication().AddIdentityCookies();
        services.ConfigureApplicationCookie(options =>
        {
            options.LoginPath = LoginPath;
            options.LogoutPath = LogoutPath;
            options.AccessDeniedPath = AccessDeniedPath;
            options.Cookie.Name = "UpdateCoordinator.Session";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
        });

        services.AddAuthorizationBuilder().AddDashboardPolicies();
        services.AddCascadingAuthenticationState();
        services.AddRazorComponents();

        services.AddSingleton<SetupService>();
        services.AddScoped<AuditLog>();

        services.AddOptions<ApprovalOptions>()
            .Bind(configuration.GetSection(ApprovalOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddScoped<ClientApprovalService>();
        services.AddScoped<AssignmentService>();
        services.AddScoped<AssignmentAdministration>();

        services.AddOptions<RolloutOptions>()
            .Bind(configuration.GetSection(RolloutOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddScoped<ReleasePlanner>();
        services.AddScoped<RolloutService>();
        services.AddScoped<RolloutAdministration>();
        services.AddHostedService<RolloutWorker>();
        services.AddSingleton<IDirectoryLookup, LdapDirectoryLookup>();
        services.AddSingleton<IDnsLookup, SystemDnsLookup>();
        services.AddScoped<RuleBasedApprover>();
        services.AddHostedService<RuleBasedApprovalWorker>();
        return services;
    }

    /// <summary>Creates the roles, opens the one-time setup if no account exists yet and recalculates assignments.</summary>
    public static async Task InitializeDashboardAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        await AdminAccounts.EnsureRolesAsync(scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>()).ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<SetupService>()
            .InitializeAsync(scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>(), cancellationToken)
            .ConfigureAwait(false);

        // Brings ring, groups and effective windows of existing clients up to date (e.g. after an upgrade).
        await scope.ServiceProvider.GetRequiredService<AssignmentService>().RecalculateAsync(null, cancellationToken).ConfigureAwait(false);
    }
}
