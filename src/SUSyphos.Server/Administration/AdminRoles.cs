using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;

namespace SUSyphos.Server.Administration;

/// <summary>
/// Hierarchical roles of the web interface (docs/04-central-server.md, section 6): Admin includes Operator includes Reader.
/// </summary>
internal static class AdminRoles
{
    public const string Reader = "Reader";
    public const string Operator = "Operator";
    public const string Admin = "Admin";

    public static IReadOnlyList<string> All { get; } = [Reader, Operator, Admin];

    /// <summary>Authorization policies; each accepts its role and every higher role.</summary>
    public static class Policies
    {
        public const string Read = "dashboard-read";
        public const string Operate = "dashboard-operate";
        public const string Administer = "dashboard-administer";
    }

    public static AuthorizationBuilder AddDashboardPolicies(this AuthorizationBuilder builder) => builder
        .AddPolicy(Policies.Read, policy => Dashboard(policy).RequireRole(Reader, Operator, Admin))
        .AddPolicy(Policies.Operate, policy => Dashboard(policy).RequireRole(Operator, Admin))
        .AddPolicy(Policies.Administer, policy => Dashboard(policy).RequireRole(Admin));

    // Dashboard policies only accept the login cookie, never agent credentials.
    private static AuthorizationPolicyBuilder Dashboard(AuthorizationPolicyBuilder policy) =>
        policy.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme).RequireAuthenticatedUser();
}
