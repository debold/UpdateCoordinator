using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography.X509Certificates;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using UpdateCoordinator.Server.Trust;
using UpdateCoordinator.TestSupport;

namespace UpdateCoordinator.Server.Tests;

public sealed class CertificateLifecycleTests
{
    private static readonly string[] Names = ["uc01.lab.local", "uc01"];
    private static readonly X509Certificate2 Authority = FakeCertificateStore.CreateAuthority();

    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly FakeCertificateStore _store = new();

    public CertificateLifecycleTests() => _store.Clock = _time.GetUtcNow;

    [Fact]
    public void WithoutConfiguration_CreatesOneSelfSignedCertificate_AndReusesItAfterRestart()
    {
        var first = Lifecycle();
        var certificate = first.SelectInitial();

        Assert.True(ServerCertificates.IsSelfGenerated(certificate));
        Assert.Equal(CertificateLifecycle.Created, first.StartupChange?.Action);
        Assert.True(certificate.NotAfter.ToUniversalTime() - _time.GetUtcNow().UtcDateTime > TimeSpan.FromDays(179));
        Assert.Null(_store.Saved);

        var restarted = Lifecycle();
        Assert.Equal(certificate.Thumbprint, restarted.SelectInitial().Thumbprint);
        Assert.Null(restarted.StartupChange);
        Assert.Equal(1, _store.CreatedCount);
    }

    [Fact]
    public void ConfiguredCertificate_IsUsedUnchanged()
    {
        var configured = _store.Add(Issued(days: 200));

        var lifecycle = Lifecycle(configured.Thumbprint);

        Assert.Equal(configured.Thumbprint, lifecycle.SelectInitial().Thumbprint);
        Assert.Null(lifecycle.StartupChange);
        Assert.Equal(0, _store.CreatedCount);
    }

    [Fact]
    public void ExpiredConfiguredCertificate_StaysInUse_BecauseAgentsPinTheKey()
    {
        var configured = _store.Add(Issued(days: 10));
        _time.Advance(TimeSpan.FromDays(20));

        Assert.Equal(configured.Thumbprint, Lifecycle(configured.Thumbprint).SelectInitial().Thumbprint);
        Assert.Equal(0, _store.CreatedCount);
    }

    [Fact]
    public void MissingConfiguredCertificate_FallsBackToSelfSigned_WithoutOverwritingTheSetting()
    {
        var lifecycle = Lifecycle(new string('A', 40));

        var certificate = lifecycle.SelectInitial();

        Assert.True(ServerCertificates.IsSelfGenerated(certificate));
        Assert.Equal(CertificateLifecycle.FallBack, lifecycle.StartupChange?.Action);
        Assert.Null(_store.Saved);
    }

    [Fact]
    public void RenewalWhileStopped_IsFollowedAtStart_AndSaved()
    {
        var configured = _store.Add(Issued(days: 20));
        var renewed = _store.Add(Issued(days: 365));

        var lifecycle = Lifecycle(configured.Thumbprint);

        Assert.Equal(renewed.Thumbprint, lifecycle.SelectInitial().Thumbprint);
        Assert.Equal(CertificateLifecycle.Renewed, lifecycle.StartupChange?.Action);
        Assert.Equal(renewed.Thumbprint, _store.Saved);
    }

    [Fact]
    public void Check_FollowsAutoenrollmentRenewal()
    {
        var current = _store.Add(Issued(days: 40));
        var lifecycle = Lifecycle(current.Thumbprint);
        Assert.Null(lifecycle.Check(current));

        var renewed = _store.Add(Issued(days: 365));
        var change = lifecycle.Check(current);

        Assert.Equal(renewed.Thumbprint, change?.Certificate.Thumbprint);
        Assert.Equal(CertificateLifecycle.Renewed, change?.Action);
        Assert.Equal(renewed.Thumbprint, _store.Saved);
    }

    [Fact]
    public void Check_IgnoresCertificatesThatAreNoRenewal()
    {
        var current = _store.Add(Issued(days: 40));
        var otherAuthority = FakeCertificateStore.CreateAuthority("Other CA");
        _store.Add(FakeCertificateStore.CreateCertificate(Names, Now.AddMinutes(-5), Now.AddDays(365), issuer: otherAuthority));
        _store.Add(FakeCertificateStore.CreateCertificate(["uc02.lab.local", "uc01"], Now.AddMinutes(-5), Now.AddDays(365), issuer: Authority));
        _store.Add(FakeCertificateStore.CreateCertificate(["uc01.lab.local"], Now.AddMinutes(-5), Now.AddDays(365), issuer: Authority));
        _store.Add(FakeCertificateStore.CreateCertificate(Names, Now.AddMinutes(-5), Now.AddDays(30), issuer: Authority));
        _store.Add(FakeCertificateStore.CreateCertificate(Names, Now.AddMinutes(-5), Now.AddDays(365)));

        Assert.Null(Lifecycle(current.Thumbprint).Check(current));
        Assert.Null(_store.Saved);
    }

    [Fact]
    public void Check_NeverFollowsAForeignSelfSignedCertificate()
    {
        var foreign = _store.Add(FakeCertificateStore.CreateCertificate(Names, Now.AddMinutes(-5), Now.AddDays(40)));
        _store.Add(FakeCertificateStore.CreateCertificate(Names, Now.AddMinutes(-5), Now.AddDays(365)));

        Assert.Null(Lifecycle(foreign.Thumbprint).Check(foreign));
    }

    [Fact]
    public void Check_RollsOverOwnCertificate_ShortlyBeforeExpiry()
    {
        var lifecycle = Lifecycle();
        var current = lifecycle.SelectInitial();

        _time.Advance(TimeSpan.FromDays(140));
        Assert.Null(lifecycle.Check(current));

        _time.Advance(TimeSpan.FromDays(11));
        var change = lifecycle.Check(current);

        Assert.Equal(CertificateLifecycle.RolledOver, change?.Action);
        Assert.NotEqual(current.Thumbprint, change!.Certificate.Thumbprint);
        Assert.True(ServerCertificates.IsSelfGenerated(change.Certificate));
        Assert.False(change.Persist);
        Assert.Null(_store.Saved);
        Assert.Equal(2, _store.CreatedCount);

        // After a restart the newer certificate is chosen, nothing new is created.
        Assert.Equal(change.Certificate.Thumbprint, Lifecycle().SelectInitial().Thumbprint);
        Assert.Equal(2, _store.CreatedCount);
    }

    [Fact]
    public void Check_OnlyWarnsForAnExpiringCaCertificate()
    {
        var current = _store.Add(Issued(days: 10));

        Assert.Null(Lifecycle(current.Thumbprint).Check(current));
        Assert.Equal(0, _store.CreatedCount);
    }

    [Theory]
    [InlineData(365, 30, 15, true)]
    [InlineData(30, 15, 1, true)]
    [InlineData(6, 1, 15, false)]
    [InlineData(7, 3, 15, true)]
    [InlineData(3651, 30, 15, false)]
    [InlineData(365, 0, 15, false)]
    [InlineData(60, 31, 15, false)]
    [InlineData(365, 30, 0, false)]
    public void Options_AreValidated(int lifetimeDays, int renewBeforeDays, int checkMinutes, bool valid)
    {
        var options = new CertificateOptions
        {
            SelfSignedLifetime = TimeSpan.FromDays(lifetimeDays),
            RenewBefore = TimeSpan.FromDays(renewBeforeDays),
            CheckInterval = TimeSpan.FromMinutes(checkMinutes),
        };

        Assert.Equal(valid, Validator.TryValidateObject(options, new ValidationContext(options), [], validateAllProperties: true));
    }

    [Fact]
    public async Task Renewal_IsPresented_SignedForAgents_AndAudited()
    {
        await using var factory = new ServerFactory();
        factory.StartServer();
        var store = factory.CertificateStore;
        var current = store.Add(FakeCertificateStore.CreateCertificate(Names, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(20), issuer: Authority));
        await factory.ReplaceCertificateAsync(current);
        var serial = await SerialAsync(factory);
        var renewed = store.Add(FakeCertificateStore.CreateCertificate(Names, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(365), issuer: Authority));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<CertificateAdministration>().CheckRenewalAsync(CancellationToken.None));
            Assert.False(await scope.ServiceProvider.GetRequiredService<CertificateAdministration>().CheckRenewalAsync(CancellationToken.None));
        }

        Assert.Equal(renewed.Thumbprint, factory.Certificate.Thumbprint);
        Assert.Equal(serial + 1, await SerialAsync(factory));
        Assert.True(await factory.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Action == CertificateLifecycle.Renewed && e.Actor == "system", TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task StartupChange_IsAudited()
    {
        await using var factory = new ServerFactory();
        factory.StartServer();
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<CertificateLifecycle>().SelectInitial();

        await scope.ServiceProvider.GetRequiredService<CertificateAdministration>().AuditStartupChangeAsync(CancellationToken.None);

        Assert.True(await factory.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Action == CertificateLifecycle.Created, TestContext.Current.CancellationToken)));
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    private X509Certificate2 Issued(int days) =>
        FakeCertificateStore.CreateCertificate(Names, Now.AddMinutes(-5), Now.AddDays(days), issuer: Authority);

    private CertificateLifecycle Lifecycle(string? thumbprint = null) => new(
        _store,
        Options.Create(new CertificateOptions()),
        Options.Create(new TrustOptions { CertificateThumbprint = thumbprint }),
        _time,
        NullLogger<CertificateLifecycle>.Instance);

    private static async Task<long> SerialAsync(ServerFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<TrustBindingService>().GetCurrentAsync(CancellationToken.None))!.Serial;
    }
}
