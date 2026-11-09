using Microsoft.EntityFrameworkCore;

using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Software;

/// <summary>
/// The central switch of software management with WinGet (Marc, 2026-11-09): an administrator who does not want the
/// feature switches it off. Then the central server neither reads the community source nor fills its cache, its WinGet
/// source answers nothing, no software version starts, the agents run no WinGet and the software pages show a notice.
/// On by default; stored in the settings table.
/// </summary>
internal sealed class SoftwareFeature(ServerDbContext db, AuditLog audit)
{
    /// <summary>Whether software management is on; missing setting means on.</summary>
    public static async Task<bool> IsEnabledAsync(ServerDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return !await db.Settings.AnyAsync(s => s.Key == SettingEntity.SoftwareEnabled && s.Value == "false", cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken) => IsEnabledAsync(db, cancellationToken);

    /// <summary>Switches software management on or off, tells every agent with its next release list and writes the audit log.</summary>
    public async Task SetAsync(bool enabled, string actor, CancellationToken cancellationToken)
    {
        if (await IsEnabledAsync(cancellationToken).ConfigureAwait(false) == enabled)
        {
            return;
        }

        var setting = await db.Settings.SingleOrDefaultAsync(s => s.Key == SettingEntity.SoftwareEnabled, cancellationToken).ConfigureAwait(false);
        if (setting is null)
        {
            db.Settings.Add(new SettingEntity { Key = SettingEntity.SoftwareEnabled, Value = enabled ? "true" : "false" });
        }
        else
        {
            setting.Value = enabled ? "true" : "false";
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // The agents learn it with their release list, which they fetch when its version changes.
        await db.Clients.ExecuteUpdateAsync(s => s.SetProperty(c => c.ReleasesVersion, c => c.ReleasesVersion + 1), cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, enabled ? "Software.Enabled" : "Software.Disabled", "software management", null, cancellationToken).ConfigureAwait(false);
    }
}
