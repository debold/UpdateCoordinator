using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Approval;

/// <summary>Where a domain for automatic approval comes from.</summary>
internal enum ApprovalDomainSource
{
    /// <summary>`Approval:AutoApproveDomains` in the configuration; changed only there.</summary>
    Configuration,

    /// <summary>Added on the dashboard page Approvals.</summary>
    Dashboard,
}

internal sealed record ApprovalDomain(string Name, ApprovalDomainSource Source);

/// <summary>
/// DNS domains whose members are approved automatically after the directory and DNS check (docs/03-communication-protocol.md,
/// section 4.1): those from the configuration plus those an administrator added on the dashboard.
/// </summary>
internal sealed partial class ApprovalDomains(ServerDbContext db, IOptionsMonitor<ApprovalOptions> options, AuditLog audit)
{
    public const string SettingKey = "Approval.AutoApproveDomains";

    /// <summary>Upper bound for domains added on the dashboard.</summary>
    public const int MaxDomains = 50;

    public async Task<IReadOnlyList<ApprovalDomain>> ListAsync(CancellationToken cancellationToken)
    {
        var configured = options.CurrentValue.AutoApproveDomains.Select(d => new ApprovalDomain(d.ToLowerInvariant(), ApprovalDomainSource.Configuration));
        var added = (await LoadAsync(cancellationToken).ConfigureAwait(false)).Select(d => new ApprovalDomain(d, ApprovalDomainSource.Dashboard));
        return [.. configured.Concat(added).DistinctBy(d => d.Name, StringComparer.OrdinalIgnoreCase).OrderBy(d => d.Name, StringComparer.Ordinal)];
    }

    public async Task<IReadOnlyList<string>> GetAllowedAsync(CancellationToken cancellationToken) =>
        [.. (await ListAsync(cancellationToken).ConfigureAwait(false)).Select(d => d.Name)];

    public async Task<Outcome> AddAsync(string? domain, string actor, CancellationToken cancellationToken)
    {
        var name = domain?.Trim().TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(name) || name.Length > 253 || !DomainRegex().IsMatch(name) || !name.Contains('.', StringComparison.Ordinal))
        {
            return new Outcome("Enter a DNS domain name such as corp.example.com.");
        }

        var stored = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if ((await ListAsync(cancellationToken).ConfigureAwait(false)).Any(d => d.Name == name))
        {
            return new Outcome("The domain is already approved automatically.");
        }

        if (stored.Count >= MaxDomains)
        {
            return new Outcome($"At most {MaxDomains} domains can be added on the dashboard.");
        }

        await SaveAsync([.. stored, name], cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "Approval.DomainAdded", name, null, cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    public async Task<Outcome> RemoveAsync(string? domain, string actor, CancellationToken cancellationToken)
    {
        var stored = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var name = domain?.Trim().ToLowerInvariant();
        if (name is null || !stored.Contains(name))
        {
            return options.CurrentValue.AutoApproveDomains.Contains(name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                ? new Outcome("The domain comes from the configuration (Approval:AutoApproveDomains) and can only be removed there.")
                : new Outcome("The domain is not in the list.");
        }

        await SaveAsync([.. stored.Where(d => d != name)], cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "Approval.DomainRemoved", name, null, cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    private async Task<List<string>> LoadAsync(CancellationToken cancellationToken)
    {
        var value = await db.Settings.AsNoTracking().Where(s => s.Key == SettingKey).Select(s => s.Value).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (value is null)
        {
            return [];
        }

        // Only valid names survive a tampered value, so the check never runs against garbage.
        var names = JsonSerializer.Deserialize<List<string>>(value) ?? [];
        return [.. names.Where(n => n is { Length: > 0 and <= 253 } && DomainRegex().IsMatch(n)).Take(MaxDomains)];
    }

    private async Task SaveAsync(List<string> names, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(names);
        var setting = await db.Settings.SingleOrDefaultAsync(s => s.Key == SettingKey, cancellationToken).ConfigureAwait(false);
        if (setting is null)
        {
            db.Settings.Add(new SettingEntity { Key = SettingKey, Value = json });
        }
        else
        {
            setting.Value = json;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    [GeneratedRegex(ValidationPatterns.DomainName, RegexOptions.None, 1000)]
    private static partial Regex DomainRegex();
}
