using System.ComponentModel.DataAnnotations;
using System.Net;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Approval;

/// <summary>
/// Rule-based approval settings. Bound from the "Approval" configuration section; administrators add further domains on the dashboard (<see cref="ApprovalDomains"/>).
/// </summary>
internal sealed class ApprovalOptions : IValidatableObject
{
    public const string SectionName = "Approval";

    /// <summary>
    /// DNS domains whose members are approved automatically after verification. Empty disables rule-based approval.
    /// </summary>
    public List<string> AutoApproveDomains { get; set; } = [];

    [Range(typeof(TimeSpan), "00:00:10", "01:00:00")]
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromMinutes(1);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var domain in AutoApproveDomains)
        {
            if (domain.Length is 0 or > 253
                || !System.Text.RegularExpressions.Regex.IsMatch(domain, ValidationPatterns.DomainName, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)))
            {
                yield return new ValidationResult($"'{domain}' is not a valid DNS domain name.", [nameof(AutoApproveDomains)]);
            }
        }
    }
}

internal enum RuleVerdict
{
    Approve,
    NotApplicable,
    DomainNotAllowed,
    ComputerNotFound,
    ComputerDisabled,
    AddressMismatch,
}

/// <summary>
/// Decides whether a pending client may be approved automatically. Nothing the agent reports is trusted on its own:
/// the central server checks the computer account in Active Directory itself and that the registration came from
/// an IP address registered in DNS for that computer (docs/03-communication-protocol.md, section 4.1).
/// </summary>
internal sealed class RuleBasedApprover(IDirectoryLookup directory, IDnsLookup dns)
{
    /// <param name="client">Pending client; nothing it reports is trusted on its own.</param>
    /// <param name="allowed">Domains approved automatically (configuration and dashboard, see <see cref="ApprovalDomains"/>).</param>
    /// <param name="cancellationToken">Cancels the directory and DNS lookups.</param>
    public async Task<RuleVerdict> EvaluateAsync(ClientEntity client, IReadOnlyCollection<string> allowed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(allowed);
        if (allowed.Count == 0 || client.DomainName is null || client.RegisteredFromAddress is null)
        {
            return RuleVerdict.NotApplicable;
        }

        if (!allowed.Contains(client.DomainName, StringComparer.OrdinalIgnoreCase))
        {
            return RuleVerdict.DomainNotAllowed;
        }

        var account = await directory.FindComputerAsync(client.DomainName, client.HostName, cancellationToken).ConfigureAwait(false);
        if (account != ComputerAccountState.Enabled)
        {
            return account == ComputerAccountState.Disabled ? RuleVerdict.ComputerDisabled : RuleVerdict.ComputerNotFound;
        }

        if (!IPAddress.TryParse(client.RegisteredFromAddress, out var source))
        {
            return RuleVerdict.AddressMismatch;
        }

        var addresses = await dns.ResolveAsync($"{client.HostName}.{client.DomainName}", cancellationToken).ConfigureAwait(false);
        var normalizedSource = source.IsIPv4MappedToIPv6 ? source.MapToIPv4() : source;
        return addresses.Any(a => (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).Equals(normalizedSource))
            ? RuleVerdict.Approve
            : RuleVerdict.AddressMismatch;
    }
}

/// <summary>
/// Periodically evaluates pending clients against the approval rules. Clients that do not match stay pending
/// for a manual decision; a directory outage only delays the decision.
/// </summary>
internal sealed partial class RuleBasedApprovalWorker(
    IServiceScopeFactory scopes,
    IOptionsMonitor<ApprovalOptions> options,
    TimeProvider timeProvider,
    ILogger<RuleBasedApprovalWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            {
                try
                {
                    await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    LogRunFailed(ex);
                }
            }

            try
            {
                await Task.Delay(options.CurrentValue.CheckInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
        var approver = scope.ServiceProvider.GetRequiredService<RuleBasedApprover>();
        var approvals = scope.ServiceProvider.GetRequiredService<ClientApprovalService>();
        var removals = scope.ServiceProvider.GetRequiredService<ServerRemoval>();
        var allowed = await scope.ServiceProvider.GetRequiredService<ApprovalDomains>().GetAllowedAsync(cancellationToken).ConfigureAwait(false);
        if (allowed.Count == 0)
        {
            return;
        }

        var pending = await db.Clients.AsNoTracking()
            .Where(c => c.State == RegistrationState.Pending && c.DomainName != null)
            .Take(100)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var client in pending)
        {
            // A server removed before waits for a manual approval: its agent may simply have been offline when it was removed.
            if (await removals.WasRemovedAsync(client.HostName, client.DomainName, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            RuleVerdict verdict;
            try
            {
                verdict = await approver.EvaluateAsync(client, allowed, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.DirectoryServices.Protocols.LdapException or System.Net.Sockets.SocketException or System.ComponentModel.Win32Exception or TimeoutException)
            {
                LogCheckFailed(client.Id, ex.Message);
                continue;
            }

            if (verdict == RuleVerdict.Approve)
            {
                await approvals.ApproveAsync(client.Id, ClientApprovalService.RuleActor, "Computer account verified in Active Directory and source address matches DNS.", cancellationToken).ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rule-based approval check for client {ClientId} failed: {Reason}. Retrying later.")]
    private partial void LogCheckFailed(Guid clientId, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Rule-based approval run failed.")]
    private partial void LogRunFailed(Exception exception);
}
