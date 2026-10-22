using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts.Trust;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Agents;

/// <summary>
/// Move to a new central server (docs/03-communication-protocol.md, 3.4): the administrator announces the new address,
/// every approved agent gets it with its heartbeat and switches once the server there proves the same trust anchor
/// and knows the agent. The new server is restored from a backup of this one; a server with a new anchor is a new onboarding.
/// </summary>
internal sealed class ServerMove(ServerDbContext db, AuditLog audit)
{
    public const string SettingKey = "CentralServer.MoveTo";

    /// <summary>The announced new address; null when no move is announced.</summary>
    public async Task<Uri?> GetAsync(CancellationToken cancellationToken)
    {
        var value = await db.Settings.AsNoTracking().Where(s => s.Key == SettingKey).Select(s => s.Value).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return CentralServerAddress.TryNormalize(value, out var address) ? address : null;
    }

    /// <summary>Announces a new address, or withdraws the announcement with an empty one.</summary>
    public async Task<Outcome> SetAsync(string? text, string actor, CancellationToken cancellationToken)
    {
        Uri? address = null;
        if (!string.IsNullOrWhiteSpace(text) && !CentralServerAddress.TryNormalize(text, out address))
        {
            return new Outcome("Enter the address of the new central server as agents reach it, e.g. https://updates.example.local:8443/ (HTTPS, without user name, query or fragment).");
        }

        var setting = await db.Settings.SingleOrDefaultAsync(s => s.Key == SettingKey, cancellationToken).ConfigureAwait(false);
        if (address is null)
        {
            if (setting is null)
            {
                return Outcome.Ok;
            }

            db.Settings.Remove(setting);
            await db.Clients.Where(c => c.ServerMoveError != null).ExecuteUpdateAsync(s => s.SetProperty(c => c.ServerMoveError, (string?)null), cancellationToken).ConfigureAwait(false);
        }
        else if (setting is null)
        {
            db.Settings.Add(new SettingEntity { Key = SettingKey, Value = address.AbsoluteUri });
        }
        else if (setting.Value == address.AbsoluteUri)
        {
            return Outcome.Ok;
        }
        else
        {
            setting.Value = address.AbsoluteUri;
            await db.Clients.Where(c => c.ServerMoveError != null).ExecuteUpdateAsync(s => s.SetProperty(c => c.ServerMoveError, (string?)null), cancellationToken).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, address is null ? "ServerMove.Withdrawn" : "ServerMove.Announced", address?.AbsoluteUri ?? "central server move", null, cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }
}
