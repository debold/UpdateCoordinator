using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace UpdateCoordinator.Server.Administration;

/// <summary>A dashboard account as the user list shows it.</summary>
internal sealed record UserSummary(string Id, string UserName, string? Role, bool LockedOut, DateTimeOffset? LockoutEnd);

/// <summary>
/// Account management in the dashboard (docs/06-dashboard.md, users and roles): create, change role, lock, unlock,
/// reset the password and delete. Every account has exactly one role. The last active administrator can neither be
/// demoted, locked nor deleted, so the dashboard can never lock itself out. Every change is audited.
/// </summary>
internal sealed class UserAdministration(UserManager<IdentityUser> users, AuditLog audit, TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<UserSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var accounts = await users.Users.AsNoTracking().OrderBy(u => u.UserName).ToListAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        var result = new List<UserSummary>();
        foreach (var account in accounts)
        {
            var roles = await users.GetRolesAsync(account).ConfigureAwait(false);
            result.Add(new UserSummary(account.Id, account.UserName ?? string.Empty, roles.FirstOrDefault(), account.LockoutEnd > now, account.LockoutEnd));
        }

        return result;
    }

    public async Task<Outcome> CreateAsync(string? userName, string? password, string? role, string actor, CancellationToken cancellationToken)
    {
        if (!IsRole(role))
        {
            return new Outcome("The role is invalid.");
        }

        var account = new IdentityUser(userName?.Trim() ?? string.Empty);
        var created = await users.CreateAsync(account, password ?? string.Empty).ConfigureAwait(false);
        if (!created.Succeeded)
        {
            return new Outcome(Describe(created));
        }

        await users.AddToRoleAsync(account, role!).ConfigureAwait(false);
        await audit.WriteAsync(actor, "User.Created", account.UserName, role, cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    public async Task<Outcome> SetRoleAsync(string userId, string? role, string actor, CancellationToken cancellationToken)
    {
        if (!IsRole(role))
        {
            return new Outcome("The role is invalid.");
        }

        var account = await users.FindByIdAsync(userId).ConfigureAwait(false);
        if (account is null)
        {
            return new Outcome("The account no longer exists.");
        }

        var current = await users.GetRolesAsync(account).ConfigureAwait(false);
        if (current.Contains(role!) && current.Count == 1)
        {
            return Outcome.Ok;
        }

        if (role != AdminRoles.Admin && current.Contains(AdminRoles.Admin) && await IsLastActiveAdministratorAsync(account).ConfigureAwait(false))
        {
            return new Outcome("The last active administrator cannot lose the Admin role.");
        }

        await users.RemoveFromRolesAsync(account, current).ConfigureAwait(false);
        await users.AddToRoleAsync(account, role!).ConfigureAwait(false);

        // A new security stamp ends existing sessions, so the new role applies at the next sign-in.
        await users.UpdateSecurityStampAsync(account).ConfigureAwait(false);
        await audit.WriteAsync(actor, "User.RoleChanged", account.UserName, role, cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    public async Task<Outcome> SetLockedAsync(string userId, bool locked, string actor, CancellationToken cancellationToken)
    {
        var account = await users.FindByIdAsync(userId).ConfigureAwait(false);
        if (account is null)
        {
            return new Outcome("The account no longer exists.");
        }

        if (locked && string.Equals(account.UserName, actor, StringComparison.OrdinalIgnoreCase))
        {
            return new Outcome("You cannot lock your own account.");
        }

        if (locked && await IsLastActiveAdministratorAsync(account).ConfigureAwait(false))
        {
            return new Outcome("The last active administrator cannot be locked.");
        }

        await users.SetLockoutEnabledAsync(account, true).ConfigureAwait(false);
        await users.SetLockoutEndDateAsync(account, locked ? DateTimeOffset.MaxValue : null).ConfigureAwait(false);
        if (!locked)
        {
            await users.ResetAccessFailedCountAsync(account).ConfigureAwait(false);
        }

        await users.UpdateSecurityStampAsync(account).ConfigureAwait(false);
        await audit.WriteAsync(actor, locked ? "User.Locked" : "User.Unlocked", account.UserName, null, cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    public async Task<Outcome> ResetPasswordAsync(string userId, string? password, string actor, CancellationToken cancellationToken)
    {
        var account = await users.FindByIdAsync(userId).ConfigureAwait(false);
        if (account is null)
        {
            return new Outcome("The account no longer exists.");
        }

        var token = await users.GeneratePasswordResetTokenAsync(account).ConfigureAwait(false);
        var reset = await users.ResetPasswordAsync(account, token, password ?? string.Empty).ConfigureAwait(false);
        if (!reset.Succeeded)
        {
            return new Outcome(Describe(reset));
        }

        await users.UpdateSecurityStampAsync(account).ConfigureAwait(false);
        await audit.WriteAsync(actor, "User.PasswordReset", account.UserName, null, cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    public async Task<Outcome> DeleteAsync(string userId, string actor, CancellationToken cancellationToken)
    {
        var account = await users.FindByIdAsync(userId).ConfigureAwait(false);
        if (account is null)
        {
            return new Outcome("The account no longer exists.");
        }

        if (string.Equals(account.UserName, actor, StringComparison.OrdinalIgnoreCase))
        {
            return new Outcome("You cannot delete your own account.");
        }

        if (await IsLastActiveAdministratorAsync(account).ConfigureAwait(false))
        {
            return new Outcome("The last active administrator cannot be deleted.");
        }

        await users.DeleteAsync(account).ConfigureAwait(false);
        await audit.WriteAsync(actor, "User.Deleted", account.UserName, null, cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    private async Task<bool> IsLastActiveAdministratorAsync(IdentityUser account)
    {
        if (!await users.IsInRoleAsync(account, AdminRoles.Admin).ConfigureAwait(false))
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        var administrators = await users.GetUsersInRoleAsync(AdminRoles.Admin).ConfigureAwait(false);
        return !administrators.Any(a => a.Id != account.Id && !(a.LockoutEnd > now));
    }

    private static bool IsRole(string? role) => role is not null && AdminRoles.All.Contains(role, StringComparer.Ordinal);

    private static string Describe(IdentityResult result) => string.Join(" ", result.Errors.Select(e => e.Description));
}
