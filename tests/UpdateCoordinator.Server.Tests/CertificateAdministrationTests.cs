using System.Net;
using System.Security.Cryptography.X509Certificates;

using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using UpdateCoordinator.Server.Administration;
using UpdateCoordinator.Server.Trust;
using UpdateCoordinator.TestSupport;

namespace UpdateCoordinator.Server.Tests;

public sealed class CertificateAdministrationTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task SwitchingCertificate_PresentsIt_AndSignsTheNextBinding()
    {
        var store = new FakeCertificateStore();
        var renewed = store.Add(TestCertificates.Create("uc01.lab.local"));
        await using var factory = new CertificateFactory(store);
        var before = await SerialAsync(factory);

        var outcome = await UseAsync(factory, renewed.Thumbprint, "admin");

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(renewed.Thumbprint, factory.Certificate.Thumbprint);
        Assert.Equal(before + 1, await SerialAsync(factory));
        Assert.Equal(renewed.Thumbprint, store.Saved);
        Assert.True(await factory.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Action == "Certificate.Changed", TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-thumbprint")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789")]
    public async Task MalformedThumbprint_IsRefused(string? thumbprint)
    {
        await using var factory = new CertificateFactory(new FakeCertificateStore());

        Assert.False((await UseAsync(factory, thumbprint, "admin")).Succeeded);
    }

    [Fact]
    public async Task UnknownOrCurrentCertificate_IsRefused()
    {
        var store = new FakeCertificateStore();
        await using var factory = new CertificateFactory(store);
        factory.StartServer();

        Assert.False((await UseAsync(factory, new string('A', 40), "admin")).Succeeded);
        store.Add(factory.Certificate);
        Assert.False((await UseAsync(factory, factory.Certificate.Thumbprint, "admin")).Succeeded);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task Page_NeedsAdministratorAndConfirmation()
    {
        var store = new FakeCertificateStore();
        var renewed = store.Add(TestCertificates.Create("uc01.lab.local"));
        await using var factory = new CertificateFactory(store);
        await factory.CreateUserAsync("operator", Password, AdminRoles.Operator);
        await factory.CreateUserAsync("admin", Password, AdminRoles.Admin);

        using (var operatorBrowser = new DashboardClient(factory))
        {
            using (await operatorBrowser.LoginAsync("operator", Password))
            {
            }

            using var refused = await operatorBrowser.GetAsync("/administration/certificate");
            Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        }

        using var admin = new DashboardClient(factory);
        using (await admin.LoginAsync("admin", Password))
        {
        }

        using (var page = await admin.GetAsync("/administration/certificate"))
        {
            Assert.Contains(renewed.Thumbprint, await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        using (var unconfirmed = await admin.PostFormAsync("/administration/certificate", new Dictionary<string, string> { ["Thumbprint"] = renewed.Thumbprint }))
        {
            Assert.Contains("Confirm the change", await unconfirmed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        Assert.Null(store.Saved);

        using (await admin.PostFormAsync("/administration/certificate", new Dictionary<string, string> { ["Thumbprint"] = renewed.Thumbprint, ["ConfirmChange"] = "yes" }))
        {
        }

        Assert.Equal(renewed.Thumbprint, store.Saved);
    }

    private static async Task<Outcome> UseAsync(ServerFactory factory, string? thumbprint, string actor)
    {
        factory.StartServer();
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CertificateAdministration>().UseAsync(thumbprint, actor, CancellationToken.None);
    }

    private static async Task<long> SerialAsync(ServerFactory factory)
    {
        factory.StartServer();
        await using var scope = factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<TrustBindingService>().GetCurrentAsync(CancellationToken.None))!.Serial;
    }

    private sealed class CertificateFactory(FakeCertificateStore store) : ServerFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddSingleton<IServerCertificateStore>(store));
        }
    }

    private sealed class FakeCertificateStore : IServerCertificateStore
    {
        private readonly List<X509Certificate2> _certificates = [];

        public string? Saved { get; private set; }

        public X509Certificate2 Add(X509Certificate2 certificate)
        {
            _certificates.Add(certificate);
            return certificate;
        }

        public IReadOnlyList<CertificateCandidate> ListCandidates() =>
            [.. _certificates.Select(c => new CertificateCandidate(c.Thumbprint, c.Subject, c.Issuer, c.NotBefore.ToUniversalTime(), c.NotAfter.ToUniversalTime(), []))];

        public X509Certificate2? Load(string thumbprint) =>
            _certificates.FirstOrDefault(c => string.Equals(c.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase));

        public void SaveThumbprint(string thumbprint) => Saved = thumbprint;
    }
}
