using System.Globalization;

using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Agents;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Software;

/// <summary>Progress of the last "Uninstall from servers…" of a package.</summary>
/// <param name="Total">Servers the uninstallation was queued for.</param>
/// <param name="Done">Uninstalled, and the next inventory no longer lists the package.</param>
/// <param name="Waiting">Queued or held for the window, or uninstalled and waiting for the next inventory.</param>
/// <param name="Failed">Failed, refused, expired or cancelled.</param>
/// <param name="StillPresent">The agent reported success, but its newer inventory still lists the package.</param>
/// <param name="StartedAtUtc">When it was queued.</param>
/// <param name="StartedBy">Who queued it.</param>
internal sealed record SoftwareUninstallStatus(int Total, int Done, int Waiting, int Failed, int StillPresent, DateTime StartedAtUtc, string StartedBy);

/// <summary>How one queued software uninstallation stands, from its action and the next inventory.</summary>
internal enum SoftwareUninstallState
{
    Waiting,
    AwaitingInventory,
    Done,
    StillPresent,
    Failed,
}

/// <summary>
/// Uninstalling software with WinGet (Marc, 2026-11-13 and 2026-11-14; stage 1 of the concept, WinGet only): from a
/// server's inventory, from the package page for a server, group, ring or every server that has it, and when an
/// assignment or the package is removed. Only programs matched to a WinGet package on servers where WinGet works. An
/// uninstallation counts as done when the agent reported success and its next inventory no longer lists the package.
/// </summary>
internal sealed class SoftwareUninstall(ServerDbContext db, AgentActionService actions, SoftwareAssignments assignments, AuditLog audit, TimeProvider timeProvider)
{
    /// <summary>How long the status row of the last uninstallation stays on the package page after it finished.</summary>
    public static readonly TimeSpan StatusShownFor = TimeSpan.FromDays(7);

    /// <summary>Approved servers where WinGet works and that report the package as installed, with ring and groups.</summary>
    public async Task<List<UninstallTarget>> TargetsAsync(string packageId, CancellationToken cancellationToken)
    {
        var clients = await db.ClientSoftware.AsNoTracking()
            .Where(s => s.PackageId == packageId)
            .Join(db.Clients.Where(c => c.State == RegistrationState.Approved && c.SoftwareState == SoftwareSupportState.Supported), s => s.ClientId, c => c.Id, (s, c) => new { c.Id, c.HostName, c.RingId })
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var ids = clients.Select(c => c.Id).ToList();
        var groups = await db.ClientGroups.AsNoTracking().Where(g => ids.Contains(g.ClientId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. clients.Select(c => new UninstallTarget(c.Id, c.HostName, c.RingId, [.. groups.Where(g => g.ClientId == c.Id).Select(g => g.GroupId)])).OrderBy(c => c.HostName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Queues the uninstallation on the servers of the scope; ends every assignment of the package first when asked, so
    /// they do not install it again. Returns how many servers got it and how many assignments ended.
    /// </summary>
    public async Task<(int Queued, int Ended, Outcome Outcome)> QueueAsync(string packageId, AgentActionKind kind, UninstallScope scope, bool endAssignments, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!AgentActionService.IsHeld(kind))
        {
            return (0, 0, new Outcome("Choose when the servers uninstall it."));
        }

        if (!await SoftwareFeature.IsEnabledAsync(db, cancellationToken).ConfigureAwait(false))
        {
            return (0, 0, new Outcome("Software management is switched off."));
        }

        var package = await db.Packages.AsNoTracking().Where(p => p.PackageId == packageId).Select(p => p.PackageId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (package is null)
        {
            return (0, 0, new Outcome("The package is not in the catalog."));
        }

        var targets = (await TargetsAsync(package, cancellationToken).ConfigureAwait(false)).Where(scope.Contains).ToList();
        if (targets.Count == 0)
        {
            return (0, 0, new Outcome("No server of the choice has the package installed with WinGet working."));
        }

        var ended = endAssignments ? await assignments.EndAllAsync(package, actor, cancellationToken).ConfigureAwait(false) : 0;
        var batch = Guid.NewGuid();
        var queued = 0;
        foreach (var target in targets)
        {
            if ((await actions.QueueSoftwareUninstallAsync(target.ClientId, package, kind, actor, cancellationToken, batch).ConfigureAwait(false)).Succeeded)
            {
                queued++;
            }
        }

        await audit.WriteAsync(actor, "Package.UninstallQueued", package, string.Create(CultureInfo.InvariantCulture, $"{queued} of {targets.Count} servers, {kind}, {ended} assignments ended"), cancellationToken).ConfigureAwait(false);
        return (queued, ended, Outcome.Ok);
    }

    /// <summary>The state of one software uninstallation: its action, and whether the inventory after it still lists the package.</summary>
    public static SoftwareUninstallState StateOf(AgentActionState state, DateTime? completedAtUtc, bool stillListed, DateTime? inventoryAtUtc) => state switch
    {
        AgentActionState.Queued or AgentActionState.Delivered => SoftwareUninstallState.Waiting,
        AgentActionState.Succeeded when !stillListed => SoftwareUninstallState.Done,
        AgentActionState.Succeeded when inventoryAtUtc is { } inventory && completedAtUtc is { } done && inventory > done => SoftwareUninstallState.StillPresent,
        AgentActionState.Succeeded => SoftwareUninstallState.AwaitingInventory,
        _ => SoftwareUninstallState.Failed,
    };

    /// <summary>Progress of the last uninstallation of the package from servers, while it runs and for a week after; null otherwise.</summary>
    public async Task<SoftwareUninstallStatus?> StatusAsync(string packageId, CancellationToken cancellationToken)
    {
        var batch = await db.AgentActions.AsNoTracking()
            .Where(a => a.PackageId == packageId && a.BatchId != null)
            .OrderByDescending(a => a.RequestedAtUtc)
            .Select(a => a.BatchId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (batch is null)
        {
            return null;
        }

        var rows = await db.AgentActions.AsNoTracking()
            .Where(a => a.BatchId == batch)
            .Join(db.Clients, a => a.ClientId, c => c.Id, (a, c) => new { a.State, a.CompletedAtUtc, a.RequestedAtUtc, a.RequestedBy, a.ClientId, c.InventoryAtUtc })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var ids = rows.Select(r => r.ClientId).ToList();
        var listed = (await db.ClientSoftware.AsNoTracking().Where(s => s.PackageId == packageId && ids.Contains(s.ClientId)).Select(s => s.ClientId).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var states = rows.Select(r => StateOf(r.State, r.CompletedAtUtc, listed.Contains(r.ClientId), r.InventoryAtUtc)).ToList();
        var waiting = states.Count(s => s is SoftwareUninstallState.Waiting or SoftwareUninstallState.AwaitingInventory);
        var finished = rows.Max(r => r.CompletedAtUtc);
        if (waiting == 0 && finished is { } last && last < timeProvider.GetUtcNow().UtcDateTime - StatusShownFor)
        {
            return null;
        }

        return new SoftwareUninstallStatus(
            rows.Count,
            states.Count(s => s == SoftwareUninstallState.Done),
            waiting,
            states.Count(s => s == SoftwareUninstallState.Failed),
            states.Count(s => s == SoftwareUninstallState.StillPresent),
            rows.Min(r => r.RequestedAtUtc),
            rows[0].RequestedBy);
    }

    /// <summary>
    /// Runtimes other programs may need (Visual C++ Redistributable, .NET, Java, WebView2, Windows App Runtime); the
    /// uninstall dialogs warn about them.
    /// </summary>
    public static bool IsRuntime(string packageId)
    {
        ArgumentNullException.ThrowIfNull(packageId);
        string[] markers = ["VCRedist", "DotNet", "Runtime", "Java", "JDK", "JRE", "WebView2", "Temurin", "Corretto", "Zulu", "VCLibs", "UI.Xaml"];
        return markers.Any(m => packageId.Contains(m, StringComparison.OrdinalIgnoreCase));
    }
}
