using System.Security.Claims;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using SUSyphos.Server.Data;

namespace SUSyphos.Server.Administration;

/// <summary>
/// Configuration file switch for Windows sign-in (section "Authentication:Windows"): false turns it off whatever the
/// dashboard says, as a way back in if the domain or the setting gets in the way.
/// </summary>
internal sealed class WindowsSignInOptions
{
    public const string SectionName = "Authentication:Windows";

    public bool Enabled { get; set; } = true;
}

/// <summary>An AD group (or account) whose members get a dashboard role when they sign in with Windows.</summary>
internal sealed record WindowsGroupMapping(string Sid, string Name, string Role);

/// <summary>Turns a group name like LAB\SUSyphos-Admins into its SID; replaced by a fake in tests.</summary>
internal interface IAccountResolver
{
    /// <summary>The SID and the canonical name, or null if the name is unknown to Windows and the domain.</summary>
    (string Sid, string Name)? Resolve(string name);
}

internal sealed class WindowsAccountResolver : IAccountResolver
{
    public (string Sid, string Name)? Resolve(string name)
    {
        try
        {
            var sid = (SecurityIdentifier)new NTAccount(name).Translate(typeof(SecurityIdentifier));
            var canonical = ((NTAccount)sid.Translate(typeof(NTAccount))).Value;
            return (sid.Value, canonical);
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            return null;
        }
    }
}

/// <summary>Why a Windows sign-in did not succeed; shown on the sign-in page.</summary>
internal enum WindowsSignInFailure
{
    Disabled,
    NoRole,
    Locked,
    Invalid,

    /// <summary>A mapped group is in the logon token only as "deny only" (filtered by User Account Control).</summary>
    FilteredGroup,
}

/// <summary>
/// Sign-in with Windows (Kerberos or NTLM through the browser, decided by Marc on 2026-10-21) next to the local accounts:
/// the user's AD groups, as Windows reports them in the logon token, decide the role through the mappings configured in
/// the dashboard; the highest mapped role wins. The account is a dashboard account linked to the user's SID (login
/// provider "Windows"), so audit log, locking and sessions work as for local accounts; its role is set from the groups
/// at every sign-in and cannot be changed in the dashboard. No password ever reaches the central server.
/// </summary>
internal sealed partial class WindowsSignIn(
    UserManager<IdentityUser> users,
    SignInManager<IdentityUser> signIn,
    ServerDbContext db,
    AuditLog audit,
    IAccountResolver resolver,
    IOptions<WindowsSignInOptions> options)
{
    public const string LoginProvider = "Windows";

    public const string Route = "/login/windows";

    public const string Scheme = NegotiateDefaults.AuthenticationScheme;

    public const string EnabledKey = "WindowsSignIn.Enabled";

    public const string GroupsKey = "WindowsSignIn.Groups";

    public const int MaxMappings = 50;

    /// <summary>Roles ordered from least to most rights; the highest mapped one wins.</summary>
    private static readonly string[] RoleOrder = [AdminRoles.Reader, AdminRoles.Operator, AdminRoles.Admin];

    /// <summary>True if the configuration file allows it and an administrator switched it on.</summary>
    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken) =>
        options.Value.Enabled && await ReadSettingAsync(EnabledKey, cancellationToken).ConfigureAwait(false) == "true";

    /// <summary>False if the configuration file switched Windows sign-in off.</summary>
    public bool AllowedByConfiguration => options.Value.Enabled;

    public async Task<IReadOnlyList<WindowsGroupMapping>> GetMappingsAsync(CancellationToken cancellationToken)
    {
        var json = await ReadSettingAsync(GroupsKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<WindowsGroupMapping>>(json)?.Where(m => SidRegex().IsMatch(m.Sid) && AdminRoles.All.Contains(m.Role)).ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Signs in the user Windows authenticated; returns the failure, or null after a successful sign-in.</summary>
    public async Task<WindowsSignInFailure?> SignInAsync(ClaimsPrincipal windowsUser, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(windowsUser);
        if (!await IsEnabledAsync(cancellationToken).ConfigureAwait(false))
        {
            return WindowsSignInFailure.Disabled;
        }

        var sid = windowsUser.FindFirst(ClaimTypes.PrimarySid)?.Value;
        var name = windowsUser.Identity?.Name;
        if (sid is null || !SidRegex().IsMatch(sid) || string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.Any(char.IsControl))
        {
            return WindowsSignInFailure.Invalid;
        }

        var memberships = windowsUser.FindAll(ClaimTypes.GroupSid).Select(c => c.Value).Append(sid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var role = (await GetMappingsAsync(cancellationToken).ConfigureAwait(false))
            .Where(m => memberships.Contains(m.Sid))
            .Select(m => m.Role)
            .OrderByDescending(r => Array.IndexOf(RoleOrder, r))
            .FirstOrDefault();
        if (role is null)
        {
            // Groups Windows filters for this logon (User Account Control) arrive as deny-only SIDs and give no rights.
            var denyOnly = windowsUser.FindAll(ClaimTypes.DenyOnlySid).Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var filtered = (await GetMappingsAsync(cancellationToken).ConfigureAwait(false)).Where(m => denyOnly.Contains(m.Sid)).Select(m => m.Name).ToList();
            var details = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"authentication {windowsUser.Identity?.AuthenticationType ?? "unknown"}, {memberships.Count - 1} groups in the token{(filtered.Count > 0 ? $"; mapped but filtered by UAC (deny only): {string.Join(", ", filtered)}" : "; none of them is mapped")}");
            await audit.WriteAsync(name, "Login.WindowsNoRole", null, details, cancellationToken).ConfigureAwait(false);
            return filtered.Count > 0 ? WindowsSignInFailure.FilteredGroup : WindowsSignInFailure.NoRole;
        }

        var account = await users.FindByLoginAsync(LoginProvider, sid).ConfigureAwait(false);
        if (account is null)
        {
            account = new IdentityUser(name);
            var created = await users.CreateAsync(account).ConfigureAwait(false);
            if (!created.Succeeded)
            {
                return WindowsSignInFailure.Invalid;
            }

            await users.AddLoginAsync(account, new UserLoginInfo(LoginProvider, sid, "Windows")).ConfigureAwait(false);
            await audit.WriteAsync(name, "User.CreatedFromWindows", name, role, cancellationToken).ConfigureAwait(false);
        }
        else if (!string.Equals(account.UserName, name, StringComparison.Ordinal))
        {
            // Renamed in AD: the SID stays, the name follows.
            await users.SetUserNameAsync(account, name).ConfigureAwait(false);
        }

        if (await users.IsLockedOutAsync(account).ConfigureAwait(false))
        {
            await audit.WriteAsync(name, "Login.LockedOut", null, null, cancellationToken).ConfigureAwait(false);
            return WindowsSignInFailure.Locked;
        }

        var current = await users.GetRolesAsync(account).ConfigureAwait(false);
        if (current.Count != 1 || current[0] != role)
        {
            await users.RemoveFromRolesAsync(account, current).ConfigureAwait(false);
            await users.AddToRoleAsync(account, role).ConfigureAwait(false);
            await audit.WriteAsync(name, "User.RoleFromWindows", name, role, cancellationToken).ConfigureAwait(false);
        }

        await signIn.SignInAsync(account, isPersistent: false, authenticationMethod: LoginProvider).ConfigureAwait(false);
        await audit.WriteAsync(name, "Login.Succeeded", null, "Windows", cancellationToken).ConfigureAwait(false);
        return null;
    }

    public async Task<Outcome> SetEnabledAsync(bool enabled, string actor, CancellationToken cancellationToken)
    {
        if (enabled && (await GetMappingsAsync(cancellationToken).ConfigureAwait(false)).Count == 0)
        {
            return new Outcome("Map at least one AD group to a role first; without one nobody could sign in with Windows.");
        }

        await WriteSettingAsync(EnabledKey, enabled ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, enabled ? "WindowsSignIn.Enabled" : "WindowsSignIn.Disabled", null, null, cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    /// <summary>Maps a group (name as DOMAIN\Group, or a SID) to a role; the SID is stored, so renaming the group does no harm.</summary>
    public async Task<Outcome> AddMappingAsync(string? group, string? role, string actor, CancellationToken cancellationToken)
    {
        group = group?.Trim();
        if (string.IsNullOrEmpty(group) || group.Length > 256 || group.Any(char.IsControl))
        {
            return new Outcome("Enter the group as DOMAIN\\Group (at most 256 characters) or as its SID.");
        }

        if (role is null || !AdminRoles.All.Contains(role, StringComparer.Ordinal))
        {
            return new Outcome("The role is invalid.");
        }

        (string Sid, string Name)? resolved = SidRegex().IsMatch(group) ? (group.ToUpperInvariant(), group.ToUpperInvariant()) : resolver.Resolve(group);
        if (resolved is not { } account)
        {
            return new Outcome($"Windows does not know the group \"{group}\". Check the name (DOMAIN\\Group) and that the central server is a member of the domain.");
        }

        if (IsFilteredByUac(account.Sid))
        {
            // Detected by the well-known SID, so it works whatever language the domain uses for the group names.
            return new Outcome(UacRefusal(account.Name));
        }

        var mappings = (await GetMappingsAsync(cancellationToken).ConfigureAwait(false)).ToList();
        if (mappings.Count >= MaxMappings && !mappings.Any(m => m.Sid == account.Sid))
        {
            return new Outcome($"At most {MaxMappings} groups can be mapped.");
        }

        mappings.RemoveAll(m => string.Equals(m.Sid, account.Sid, StringComparison.OrdinalIgnoreCase));
        mappings.Add(new WindowsGroupMapping(account.Sid, account.Name, role));
        await WriteSettingAsync(GroupsKey, JsonSerializer.Serialize(mappings), cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "WindowsSignIn.GroupMapped", account.Name, $"{role} ({account.Sid})", cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    public async Task<Outcome> RemoveMappingAsync(string? sid, string actor, CancellationToken cancellationToken)
    {
        var mappings = (await GetMappingsAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var mapping = mappings.FirstOrDefault(m => string.Equals(m.Sid, sid, StringComparison.OrdinalIgnoreCase));
        if (mapping is null)
        {
            return new Outcome("The group is not mapped.");
        }

        mappings.Remove(mapping);
        await WriteSettingAsync(GroupsKey, JsonSerializer.Serialize(mappings), cancellationToken).ConfigureAwait(false);
        if (mappings.Count == 0)
        {
            await WriteSettingAsync(EnabledKey, "false", cancellationToken).ConfigureAwait(false);
        }

        await audit.WriteAsync(actor, "WindowsSignIn.GroupUnmapped", mapping.Name, mapping.Role, cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    /// <summary>True for an account that signs in with Windows (its role comes from AD, it has no password).</summary>
    public static async Task<bool> IsWindowsAccountAsync(UserManager<IdentityUser> users, IdentityUser account)
    {
        ArgumentNullException.ThrowIfNull(users);
        return (await users.GetLoginsAsync(account).ConfigureAwait(false)).Any(l => l.LoginProvider == LoginProvider);
    }

    private async Task<string?> ReadSettingAsync(string key, CancellationToken cancellationToken) =>
        await db.Settings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    private async Task WriteSettingAsync(string key, string value, CancellationToken cancellationToken)
    {
        var setting = await db.Settings.SingleOrDefaultAsync(s => s.Key == key, cancellationToken).ConfigureAwait(false);
        if (setting is null)
        {
            db.Settings.Add(new SettingEntity { Key = key, Value = value });
        }
        else
        {
            setting.Value = value;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Why a group filtered by User Account Control is refused, and what to do instead.</summary>
    public static string UacRefusal(string name) =>
        $"Won't work: Windows filters {name} by User Account Control (\"deny only\") in a logon without elevation, so its members get no role. Map a dedicated group instead.";

    /// <summary>
    /// Privileged groups that User Account Control filters to "deny only" in a logon without elevation (Domain Admins,
    /// Schema Admins, Enterprise Admins, Group Policy Creator Owners, Administrators and the built-in operator groups):
    /// mapped, they usually give nobody a role. A dedicated group is the reliable choice.
    /// </summary>
    public static bool IsFilteredByUac(string sid)
    {
        ArgumentNullException.ThrowIfNull(sid);
        string[] builtIn = ["S-1-5-32-544", "S-1-5-32-548", "S-1-5-32-549", "S-1-5-32-550", "S-1-5-32-551"];
        return builtIn.Contains(sid, StringComparer.OrdinalIgnoreCase)
            || (sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) && (sid.EndsWith("-512", StringComparison.Ordinal) || sid.EndsWith("-518", StringComparison.Ordinal) || sid.EndsWith("-519", StringComparison.Ordinal) || sid.EndsWith("-520", StringComparison.Ordinal)));
    }

    [GeneratedRegex(@"^S-1-[0-9]+(-[0-9]+){1,14}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SidRegex();
}

/// <summary>The Windows sign-in route: challenges the browser with Negotiate, then signs in through <see cref="WindowsSignIn"/>.</summary>
internal static partial class WindowsSignInEndpoints
{
    /// <summary>Search of AD groups while typing in the mapping field (administrators only; used by app.js).</summary>
    public const string GroupSearchRoute = "/administration/ad-groups";

    public static IEndpointRouteBuilder MapWindowsSignIn(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(WindowsSignIn.Route, SignInAsync).AllowAnonymous().WithName("WindowsSignIn");
        endpoints.MapGet(GroupSearchRoute, SearchGroupsAsync).RequireAuthorization(AdminRoles.Policies.Administer).WithName("AdGroupSearch");
        return endpoints;
    }

    /// <summary>
    /// Up to 20 AD groups whose name contains <paramref name="q"/> (2 to 64 characters, escaped for LDAP). An unreachable
    /// directory gives an empty list: the field still takes a typed name.
    /// </summary>
    internal static async Task<IResult> SearchGroupsAsync(string? q, Approval.IDirectoryLookup directory, Approval.DirectoryCoverage coverage, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var term = q?.Trim();
        if (term is not { Length: >= 2 and <= 64 } || term.Any(char.IsControl) || coverage.Domain is not { } domain)
        {
            return TypedResults.Ok(Array.Empty<GroupSuggestion>());
        }

        // "DOMAIN\Group" typed in full: search for the group part.
        var slash = term.LastIndexOf('\\');
        if (slash >= 0 && slash < term.Length - 2)
        {
            term = term[(slash + 1)..];
        }

        try
        {
            // "privileged" lets the page warn as soon as such a group is picked (filtered by UAC, usually no role).
            var groups = await directory.FindGroupsAsync(domain, term, 20, cancellationToken).ConfigureAwait(false);
            return TypedResults.Ok(groups.Select(g => new GroupSuggestion(g.Name, g.Sid, WindowsSignIn.IsFilteredByUac(g.Sid))).ToList());
        }
        catch (Exception exception) when (exception is System.DirectoryServices.Protocols.LdapException or System.DirectoryServices.Protocols.DirectoryOperationException or InvalidOperationException)
        {
            LogGroupSearchFailed(loggers.CreateLogger(typeof(WindowsSignInEndpoints)), exception);
            return TypedResults.Ok(Array.Empty<GroupSuggestion>());
        }
    }

    internal static async Task<IResult> SignInAsync(HttpContext httpContext, WindowsSignIn windows, string? returnUrl, CancellationToken cancellationToken)
    {
        if (!await windows.IsEnabledAsync(cancellationToken).ConfigureAwait(false))
        {
            return TypedResults.NotFound();
        }

        var result = await httpContext.AuthenticateAsync(WindowsSignIn.Scheme).ConfigureAwait(false);
        if (!result.Succeeded || result.Principal is null)
        {
            return TypedResults.Challenge(properties: null, authenticationSchemes: [WindowsSignIn.Scheme]);
        }

        var failure = await windows.SignInAsync(result.Principal, cancellationToken).ConfigureAwait(false);
        return failure is { } reason
            ? TypedResults.Redirect($"{DashboardServiceCollectionExtensions.LoginPath}?windows={reason.ToString().ToLowerInvariant()}")
            : TypedResults.LocalRedirect(LocalReturnUrl.Sanitize(returnUrl));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Searching AD groups failed; the field still takes a typed group name.")]
    private static partial void LogGroupSearchFailed(ILogger logger, Exception exception);
}

/// <summary>An AD group offered while typing; <see cref="Privileged"/> marks groups User Account Control filters to "deny only".</summary>
internal sealed record GroupSuggestion(string Name, string Sid, bool Privileged);
