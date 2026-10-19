using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.Options;

namespace UpdateCoordinator.Server.Trust;

/// <summary>A certificate change the central server made on its own; <see cref="Persist"/> stores it as the configured certificate.</summary>
internal sealed record CertificateChange(X509Certificate2 Certificate, string Action, bool Persist);

/// <summary>
/// Chooses the TLS certificate (docs/03-communication-protocol.md, section 3.2): the configured one, its renewal, or a
/// self-signed certificate the central server creates and rolls over itself. The trust anchor signs every new certificate,
/// so agents follow each change without any action.
/// </summary>
internal sealed partial class CertificateLifecycle(
    IServerCertificateStore store,
    IOptions<CertificateOptions> options,
    IOptions<TrustOptions> trust,
    TimeProvider timeProvider,
    ILogger<CertificateLifecycle> logger)
{
    public const string Created = "Certificate.Created";
    public const string Renewed = "Certificate.Renewed";
    public const string RolledOver = "Certificate.RolledOver";
    public const string FallBack = "Certificate.FallBack";

    private DateTime? _lastExpiryWarning;

    /// <summary>What <see cref="SelectInitial"/> changed, audited once the database is ready.</summary>
    public CertificateChange? StartupChange { get; private set; }

    /// <summary>Certificate at service start. Never fails for a missing certificate: without one, a self-signed one is used.</summary>
    public X509Certificate2 SelectInitial()
    {
        var thumbprint = trust.Value.CertificateThumbprint;
        if (!string.IsNullOrEmpty(thumbprint))
        {
            if (store.Load(thumbprint) is { } configured)
            {
                // Renewed while the service was stopped (autoenrollment archives the old certificate).
                if (Successor(configured) is { } renewed)
                {
                    configured.Dispose();
                    var change = new CertificateChange(renewed, Renewed, !ServerCertificates.IsSelfGenerated(renewed));
                    Persist(change);
                    StartupChange = change;
                    return renewed;
                }

                if (configured.NotAfter.ToUniversalTime() <= Now)
                {
                    LogConfiguredExpired(configured.Thumbprint);
                }

                return configured;
            }

            LogConfiguredMissing(thumbprint);
            var fallback = OwnCertificate(out _);
            StartupChange = new CertificateChange(fallback, FallBack, Persist: false);
            return fallback;
        }

        var own = OwnCertificate(out var created);
        if (created)
        {
            StartupChange = new CertificateChange(own, Created, Persist: false);
        }

        return own;
    }

    /// <summary>Periodic check: a renewed certificate in the store, or the rollover of an expiring self-signed one.</summary>
    public CertificateChange? Check(X509Certificate2 current)
    {
        if (Successor(current) is { } renewed)
        {
            var change = new CertificateChange(renewed, Renewed, !ServerCertificates.IsSelfGenerated(renewed));
            Persist(change);
            return change;
        }

        var remaining = current.NotAfter.ToUniversalTime() - Now;
        if (remaining > options.Value.RenewBefore)
        {
            return null;
        }

        if (ServerCertificates.IsSelfGenerated(current))
        {
            var next = Create();
            return new CertificateChange(next, RolledOver, Persist: false);
        }

        // A CA certificate is renewed by the CA or an administrator; the central server only warns, once a day.
        if (_lastExpiryWarning is not { } last || Now - last >= TimeSpan.FromDays(1))
        {
            _lastExpiryWarning = Now;
            LogExpiring(current.Subject, current.Thumbprint, current.NotAfter.ToUniversalTime());
        }

        return null;
    }

    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;

    private X509Certificate2? Successor(X509Certificate2 current) =>
        ServerCertificates.FindSuccessor(ServerCertificates.Describe(current), store.ListCandidates()) is { } successor
            ? store.Load(successor.Thumbprint)
            : null;

    private void Persist(CertificateChange change)
    {
        if (change.Persist)
        {
            store.SaveThumbprint(change.Certificate.Thumbprint);
        }
    }

    /// <summary>The newest own certificate that is not due for rollover, or a new one.</summary>
    private X509Certificate2 OwnCertificate(out bool created)
    {
        var existing = store.ListCandidates()
            .Where(c => c.SelfGenerated && c.NotAfterUtc - Now > options.Value.RenewBefore)
            .OrderByDescending(c => c.NotAfterUtc)
            .FirstOrDefault();
        if (existing is not null && store.Load(existing.Thumbprint) is { } certificate)
        {
            created = false;
            return certificate;
        }

        created = true;
        return Create();
    }

    private X509Certificate2 Create()
    {
        var certificate = store.CreateSelfSigned(options.Value.SelfSignedLifetime);
        var notAfter = certificate.NotAfter.ToUniversalTime();
        LogCreated(certificate.Subject, certificate.Thumbprint, notAfter);
        return certificate;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The configured TLS certificate {Thumbprint} with private key was not found in the LocalMachine My store; a self-signed certificate is used instead.")]
    private partial void LogConfiguredMissing(string thumbprint);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The configured TLS certificate {Thumbprint} has expired. Agents keep trusting it, browsers warn; select a valid certificate.")]
    private partial void LogConfiguredExpired(string thumbprint);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The TLS certificate {Subject} ({Thumbprint}) expires on {NotAfter:yyyy-MM-dd} and no renewal was found in the LocalMachine My store.")]
    private partial void LogExpiring(string subject, string thumbprint, DateTime notAfter);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created the self-signed TLS certificate {Subject} ({Thumbprint}), valid until {NotAfter:yyyy-MM-dd}.")]
    private partial void LogCreated(string subject, string thumbprint, DateTime notAfter);
}

/// <summary>Checks the certificate store periodically and switches to a renewed or rolled over certificate.</summary>
internal sealed partial class CertificateWorker(
    IServiceScopeFactory scopes,
    IOptionsMonitor<CertificateOptions> options,
    TimeProvider timeProvider,
    ILogger<CertificateWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // The start already chose a current certificate; the first check follows after one interval.
            try
            {
                await Task.Delay(options.CurrentValue.CheckInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<CertificateAdministration>().CheckRenewalAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogFailed(ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The TLS certificate check failed; the current certificate stays in use.")]
    private partial void LogFailed(Exception exception);
}
