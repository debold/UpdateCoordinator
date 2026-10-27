using Microsoft.AspNetCore.Identity;

using SUSyphos.Server.Approval;
using SUSyphos.Server.Assignment;
using SUSyphos.Server.Data;
using SUSyphos.Server.Notifications;
using SUSyphos.Server.Rollouts;

namespace SUSyphos.Server.Administration;

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
                options.Password.RequiredLength = PasswordRules.MinimumLength;
                // Length over composition rules (NIST SP 800-63B): long passphrases are allowed and encouraged.
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequireDigit = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireLowercase = false;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.User.RequireUniqueEmail = false;
                // The backslash is only for accounts that sign in with Windows (DOMAIN\name); local accounts refuse it.
                options.User.AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._@\\";
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ServerDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

        services.AddAuthentication().AddIdentityCookies();

        // Windows sign-in (Kerberos/NTLM) only authenticates the route /login/windows, which then issues the dashboard cookie.
        services.AddAuthentication().AddNegotiate();
        services.AddOptions<WindowsSignInOptions>().Bind(configuration.GetSection(WindowsSignInOptions.SectionName));
        services.AddSingleton<IAccountResolver, WindowsAccountResolver>();
        services.AddScoped<WindowsSignIn>();

        // Role changes, locks and password resets renew the security stamp; sessions notice it within a minute.
        services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.FromMinutes(1));
        services.ConfigureApplicationCookie(options =>
        {
            options.LoginPath = LoginPath;
            options.LogoutPath = LogoutPath;
            options.AccessDeniedPath = AccessDeniedPath;
            options.Cookie.Name = "SUSyphos.Session";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
        });

        services.AddAuthorizationBuilder().AddDashboardPolicies();
        services.AddCascadingAuthenticationState();
        services.AddRazorComponents();

        services.AddSingleton<IInstallerSetupToken, RegistryInstallerSetupToken>();
        services.AddSingleton<SetupService>();
        // Explicit factory: the other constructor (sources from tests) would be satisfied by an empty enumerable.
        services.AddSingleton(_ => new Help.HelpCatalog());
        services.AddScoped<AuditLog>();
        services.AddScoped<UserAdministration>();
        services.AddScoped<BackupService>();

        services.AddOptions<ApprovalOptions>()
            .Bind(configuration.GetSection(ApprovalOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddScoped<ClientApprovalService>();
        services.AddScoped<ClientLifecycle>();
        services.AddScoped<AssignmentService>();
        services.AddScoped<AssignmentAdministration>();
        services.AddScoped<LeaseService>();

        services.AddOptions<RolloutOptions>()
            .Bind(configuration.GetSection(RolloutOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddScoped<ReleasePlanner>();
        services.AddScoped<RolloutService>();
        services.AddScoped<RolloutAdministration>();
        services.AddScoped<UpdateHolds>();
        services.AddScoped<RingAdministration>();
        services.AddOptions<UpdateArchiveOptions>()
            .Bind(configuration.GetSection(UpdateArchiveOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddScoped<UpdateArchive>();
        services.AddHostedService<UpdateArchiveWorker>();
        services.AddScoped<DeploymentStatus>();
        services.AddScoped<ComplianceHistory>();
        services.AddScoped<DashboardStatistics>();
        services.AddHostedService<RolloutWorker>();
        services.AddSingleton<IDirectoryLookup, LdapDirectoryLookup>();
        services.AddSingleton<IDnsLookup, SystemDnsLookup>();
        services.AddScoped<RuleBasedApprover>();
        services.AddScoped<ApprovalDomains>();
        services.AddHostedService<RuleBasedApprovalWorker>();

        services.AddOptions<CoverageOptions>()
            .Bind(configuration.GetSection(CoverageOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddScoped<DirectoryCoverage>();
        services.AddHostedService<CoverageWorker>();

        services.AddOptions<NotificationOptions>()
            .Bind(configuration.GetSection(NotificationOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddScoped<NotificationSettingsStore>();
        services.AddScoped<NotificationEvents>();
        services.AddScoped<NotificationService>();
        services.AddSingleton<IMailTransport, SmtpMailTransport>();
        services.AddScoped<INotificationChannel, EmailChannel>();
        // Webhooks never follow redirects, so a configured address cannot be bent to another target.
        services.AddHttpClient<INotificationChannel, WebhookChannel>(http => http.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddHostedService<NotificationWorker>();

        services.AddOptions<AgentPackages.AgentPackageOptions>()
            .Bind(configuration.GetSection(AgentPackages.AgentPackageOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddScoped<AgentPackages.AgentPackageStore>();
        services.AddSingleton<AgentPackages.IRunningServerVersion, AgentPackages.RunningServerVersion>();
        services.AddScoped<AgentPackages.AgentPackageAdministration>();
        services.AddScoped<AgentPackages.AgentUpdateService>();
        // GitHub requires a user agent; packages are tens of megabytes, so the timeout is generous.
        services.AddHttpClient<AgentPackages.IAgentReleaseSource, AgentPackages.GitHubAgentReleases>(http =>
        {
            http.Timeout = TimeSpan.FromMinutes(10);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SUSyphos-CentralServer");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        });
        services.AddHostedService<AgentPackages.AgentPackageWorker>();

        services.AddOptions<Security.MsrcOptions>()
            .Bind(configuration.GetSection(Security.MsrcOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        // The monthly documents are 20 to 30 MB; they are read as a stream.
        services.AddHttpClient<Security.IMsrcSource, Security.MsrcApi>(http =>
        {
            http.Timeout = TimeSpan.FromMinutes(5);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SUSyphos-CentralServer");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        });
        services.AddScoped<Security.MsrcSync>();
        services.AddOptions<Security.SecurityOptions>()
            .Bind(configuration.GetSection(Security.SecurityOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddScoped<Security.SecurityBaseline>();
        services.AddHostedService<Security.MsrcWorker>();

        services.AddOptions<ScheduledBackupOptions>()
            .Bind(configuration.GetSection(ScheduledBackupOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddScoped<ScheduledBackup>();
        services.AddHostedService<ScheduledBackupWorker>();
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
