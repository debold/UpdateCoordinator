using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using SUSyphos.Server.Administration;
using SUSyphos.Server.Approval;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

public sealed class ApprovalDomainsTests : IAsyncDisposable
{
    private const string Password = "correct horse battery";
    private readonly ConfiguredDomainFactory _factory = new();

    [Fact]
    public async Task ConfiguredAndAddedDomains_AreBothAllowed()
    {
        Assert.True((await WithAsync(d => d.AddAsync(" Corp.Example.COM. ", "admin", CancellationToken.None))).Succeeded);

        var domains = await WithAsync(d => d.ListAsync(CancellationToken.None));

        Assert.Equal([new ApprovalDomain("config.example.local", ApprovalDomainSource.Configuration), new ApprovalDomain("corp.example.com", ApprovalDomainSource.Dashboard)], domains);
        Assert.True(await _factory.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "Approval.DomainAdded" && a.Target == "corp.example.com", TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("localhost")]
    [InlineData("bad_domain.local")]
    [InlineData("<script>.local")]
    [InlineData("corp..local")]
    public async Task InvalidDomains_AreRefused(string domain)
    {
        Assert.False((await WithAsync(d => d.AddAsync(domain, "admin", CancellationToken.None))).Succeeded);
        Assert.DoesNotContain(await WithAsync(d => d.ListAsync(CancellationToken.None)), d => d.Source == ApprovalDomainSource.Dashboard);
    }

    [Fact]
    public async Task Duplicates_AndConfiguredDomains_CannotBeAddedOrRemovedHere()
    {
        await WithAsync(d => d.AddAsync("corp.example.com", "admin", CancellationToken.None));

        Assert.False((await WithAsync(d => d.AddAsync("CORP.example.com", "admin", CancellationToken.None))).Succeeded);
        Assert.False((await WithAsync(d => d.AddAsync("config.example.local", "admin", CancellationToken.None))).Succeeded);
        Assert.Contains("configuration", (await WithAsync(d => d.RemoveAsync("config.example.local", "admin", CancellationToken.None))).Error, StringComparison.Ordinal);
        Assert.True((await WithAsync(d => d.RemoveAsync("corp.example.com", "admin", CancellationToken.None))).Succeeded);
        Assert.Single(await WithAsync(d => d.ListAsync(CancellationToken.None)));
    }

    [Fact]
    public async Task TamperedSetting_KeepsOnlyValidNames()
    {
        _factory.StartServer();
        await _factory.WithDbAsync(db =>
        {
            db.Settings.Add(new Data.SettingEntity { Key = ApprovalDomains.SettingKey, Value = "[\"ok.example.com\",\"bad name\",\"<x>\"]" });
            return db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

        var allowed = await WithAsync(d => d.GetAllowedAsync(CancellationToken.None));

        Assert.Equal(["config.example.local", "ok.example.com"], allowed);
    }

    [Fact]
    public async Task Page_LetsOnlyAdministratorsChangeDomains()
    {
        await _factory.CreateUserAsync("operator", Password, AdminRoles.Operator);
        using var operatorBrowser = new DashboardClient(_factory);
        using (await operatorBrowser.LoginAsync("operator", Password))
        {
        }

        using (var page = await operatorBrowser.GetAsync("/approvals"))
        {
            var html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("config.example.local", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Add domain", html, StringComparison.Ordinal);
        }

        using (await operatorBrowser.PostFormAsync("/approvals", new Dictionary<string, string> { ["_handler"] = "approval-domains", ["DomainAction"] = "add", ["Domain"] = "corp.example.com" }))
        {
        }

        Assert.Single(await WithAsync(d => d.ListAsync(CancellationToken.None)));

        await _factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        using var admin = new DashboardClient(_factory);
        using (await admin.LoginAsync("admin", Password))
        {
        }

        using (var added = await admin.PostFormAsync("/approvals", new Dictionary<string, string> { ["_handler"] = "approval-domains", ["DomainAction"] = "add", ["Domain"] = "corp.example.com" }))
        {
            Assert.Contains("approved automatically from now on", await added.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        Assert.Equal(2, (await WithAsync(d => d.ListAsync(CancellationToken.None))).Count);
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private async Task<T> WithAsync<T>(Func<ApprovalDomains, Task<T>> action)
    {
        _factory.StartServer();
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<ApprovalDomains>());
    }

    private sealed class ConfiguredDomainFactory : ServerFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Approval:AutoApproveDomains:0", "config.example.local");
        }
    }
}
