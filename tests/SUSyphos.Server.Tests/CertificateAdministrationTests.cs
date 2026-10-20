using System.Net;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using SUSyphos.Server.Administration;
using SUSyphos.Server.Trust;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

public sealed class CertificateAdministrationTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task SwitchingCertificate_PresentsIt_AndSignsTheNextBinding()
    {
        await using var factory = new ServerFactory();
        var store = factory.CertificateStore;
        var renewed = store.Add(TestCertificates.Create("uc01.lab.local"));
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
        await using var factory = new ServerFactory();

        Assert.False((await UseAsync(factory, thumbprint, "admin")).Succeeded);
    }

    [Fact]
    public async Task UnknownOrCurrentCertificate_IsRefused()
    {
        await using var factory = new ServerFactory();
        var store = factory.CertificateStore;
        factory.StartServer();

        Assert.False((await UseAsync(factory, new string('A', 40), "admin")).Succeeded);
        store.Add(factory.Certificate);
        Assert.False((await UseAsync(factory, factory.Certificate.Thumbprint, "admin")).Succeeded);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task Page_NeedsAdministratorAndConfirmation()
    {
        await using var factory = new ServerFactory();
        var store = factory.CertificateStore;
        var renewed = store.Add(TestCertificates.Create("uc01.lab.local"));
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

        // An unknown thumbprint in the link opens no dialog, so nothing can be switched to a certificate the list does not offer.
        using (var unknown = await admin.GetAsync("/administration/certificate?dialog=use&thumbprint=%3Cscript%3E"))
        {
            var html = await unknown.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain("Switch certificate", html, StringComparison.Ordinal);
            Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        }

        using (var unconfirmed = await admin.PostFormAsync($"/administration/certificate?dialog=use&thumbprint={renewed.Thumbprint}", new Dictionary<string, string> { ["Thumbprint"] = renewed.Thumbprint }))
        {
            Assert.Contains("Confirm the change", await unconfirmed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        Assert.Null(store.Saved);

        using (await admin.PostFormAsync($"/administration/certificate?dialog=use&thumbprint={renewed.Thumbprint}", new Dictionary<string, string> { ["Thumbprint"] = renewed.Thumbprint, ["ConfirmChange"] = "yes" }))
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
}
