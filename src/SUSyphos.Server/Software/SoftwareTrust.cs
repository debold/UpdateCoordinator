using Microsoft.EntityFrameworkCore;

using SUSyphos.Server.Agents;
using SUSyphos.Server.Assignment;
using SUSyphos.Server.Data;
using SUSyphos.Server.Trust;

namespace SUSyphos.Server.Software;

/// <summary>
/// Whether the servers can trust the WinGet source of the central server (Marc, 2026-11-10: the hint must be very
/// present, and a failure because of the certificate must be named clearly). WinGet checks the certificate like
/// Windows does; a self-signed certificate works only with the agent setting that adds it to the trusted roots.
/// </summary>
/// <param name="SelfSignedWithoutTrust">The central server uses a self-signed certificate and the system default of
/// the agent setting "trust-central-certificate" is not on.</param>
/// <param name="FailingServers">Host names of the approved servers whose last software installation failed because
/// they do not trust the certificate.</param>
public sealed record SoftwareTrustState(bool SelfSignedWithoutTrust, IReadOnlyList<string> FailingServers)
{
    public static readonly SoftwareTrustState None = new(false, []);

    public bool NeedsAttention => SelfSignedWithoutTrust || FailingServers.Count > 0;
}

internal sealed class SoftwareTrust(ServerDbContext db, ITlsCertificateProvider certificates, AssignmentAdministration administration)
{
    /// <summary>Key of the agent setting that trusts the central server's own certificate.</summary>
    public const string SettingKey = "trust-central-certificate";

    /// <summary>Where the setting is changed.</summary>
    public const string SettingLink = "/administration/agent-settings#software";

    /// <summary>The state for the whole system, or for one server.</summary>
    public async Task<SoftwareTrustState> GetAsync(Guid? clientId, CancellationToken cancellationToken)
    {
        if (!await SoftwareFeature.IsEnabledAsync(db, cancellationToken).ConfigureAwait(false))
        {
            return SoftwareTrustState.None;
        }

        var certificate = certificates.Certificate;
        var selfSigned = certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData);
        var settings = await administration.GetSystemAgentSettingsAsync(cancellationToken).ConfigureAwait(false);
        var trusted = string.Equals(settings.GetValueOrDefault(SettingKey), "true", StringComparison.OrdinalIgnoreCase);

        // The last software result per server decides: a later success means the problem is solved there.
        var software = db.Updates.Where(u => u.Kind == UpdateKind.Software).Select(u => u.UpdateId);
        var last = await db.Installations.AsNoTracking()
            .Where(i => software.Contains(i.UpdateId) && (clientId == null || i.ClientId == clientId))
            .GroupBy(i => i.ClientId)
            .Select(g => g.OrderByDescending(i => i.FinishedAtUtc).ThenByDescending(i => i.Id).Select(i => new { i.ClientId, i.HResult }).First())
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var failing = last.Where(l => HResultCatalog.IsCertificateProblem(l.HResult)).Select(l => l.ClientId).ToList();
        var names = failing.Count == 0
            ? []
            : await db.Clients.AsNoTracking().Where(c => failing.Contains(c.Id) && c.State == SUSyphos.Contracts.Agents.RegistrationState.Approved)
                .OrderBy(c => c.HostName).Select(c => c.HostName).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new SoftwareTrustState(selfSigned && !trusted, names);
    }
}
