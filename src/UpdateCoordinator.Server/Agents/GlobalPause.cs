using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using UpdateCoordinator.Server.Administration;
using UpdateCoordinator.Server.Data;

namespace UpdateCoordinator.Server.Agents;

/// <summary>
/// Global pause (docs/04-central-server.md, section 3): agents start no new installations; check, search and reporting
/// continue. Switched by an operator in the dashboard; <c>Installation:GlobalPause</c> in the configuration forces it on
/// regardless of the dashboard, e.g. while the dashboard is unreachable.
/// </summary>
internal sealed class GlobalPause(ServerDbContext db, IOptionsMonitor<InstallationControlOptions> options, AuditLog audit)
{
    public const string SettingKey = "Installation.GlobalPause";

    /// <summary>True if the configuration forces the pause; the dashboard cannot lift it then.</summary>
    public bool ForcedByConfiguration => options.CurrentValue.GlobalPause;

    public async Task<bool> IsActiveAsync(CancellationToken cancellationToken) =>
        ForcedByConfiguration
        || await db.Settings.AsNoTracking().AnyAsync(s => s.Key == SettingKey && s.Value == "true", cancellationToken).ConfigureAwait(false);

    public async Task<Outcome> SetAsync(bool active, string actor, CancellationToken cancellationToken)
    {
        if (!active && ForcedByConfiguration)
        {
            return new Outcome("The pause is forced by the configuration (Installation:GlobalPause) and cannot be lifted here.");
        }

        var value = active ? "true" : "false";
        var setting = await db.Settings.SingleOrDefaultAsync(s => s.Key == SettingKey, cancellationToken).ConfigureAwait(false);
        if (setting is null)
        {
            db.Settings.Add(new SettingEntity { Key = SettingKey, Value = value });
        }
        else if (setting.Value == value)
        {
            return Outcome.Ok;
        }
        else
        {
            setting.Value = value;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, active ? "Installation.Paused" : "Installation.Resumed", "global pause", null, cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }
}
