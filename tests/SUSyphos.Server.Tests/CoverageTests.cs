using System.Net;

using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Approval;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

public sealed class CoverageTests
{
    private const string Password = "correct horse battery";

    [Theory]
    [InlineData("10.0 (17763)", 17763)]
    [InlineData("10.0 (14393)", 14393)]
    [InlineData("6.3 (9600)", 9600)]
    [InlineData("10.0", null)]
    [InlineData("(abc)", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Build_IsParsedFromOperatingSystemVersion(string? version, int? expected) =>
        Assert.Equal(expected, DirectoryCoverage.ParseBuild(version));

    [Fact]
    public async Task Report_CountsOnlySupportedServers_AndMatchesByDnsOrHostName()
    {
        var directory = new FakeDirectory();
        directory.Servers.AddRange(
        [
            new DirectoryComputer("SRV01", "srv01.example.local", "Windows Server 2022 Standard", "10.0 (20348)"),
            new DirectoryComputer("srv02", null, "Windows Server 2019 Standard", "10.0 (17763)"),
            new DirectoryComputer("SRV03", "srv03.example.local", "Windows Server 2016 Datacenter", "10.0 (14393)"),
            new DirectoryComputer("OLD01", "old01.example.local", "Windows Server 2012 R2 Standard", "6.3 (9600)"),
            new DirectoryComputer("NOVER", null, "Windows Server", null),
            new DirectoryComputer("SRV01", "duplicate.example.local", "Windows Server 2022", "10.0 (20348)"),
        ]);
        await using var factory = CreateFactory(directory);
        using var http = factory.CreateClient();
        await RegisterApprovedAsync(factory, http, "SRV01", "example.local");
        await RegisterApprovedAsync(factory, http, "SRV02", "other.local");

        var count = await WithCoverageAsync(factory, c => c.RefreshAsync("example.local", CancellationToken.None));
        var report = await WithCoverageAsync(factory, c => c.GetReportAsync(CancellationToken.None));

        Assert.Equal(6, count);
        Assert.Equal(3, report.Supported);
        Assert.Equal(2, report.Covered);
        Assert.Equal(66, report.Percent);
        Assert.Equal("SRV03", Assert.Single(report.Missing).Name);
        Assert.Equal(["NOVER", "OLD01"], report.Unsupported.Select(c => c.Name));
        Assert.NotNull(report.ReadAtUtc);
    }

    [Fact]
    public async Task Refresh_ReplacesThePreviousList()
    {
        var directory = new FakeDirectory();
        directory.Servers.Add(new DirectoryComputer("SRV01", null, "Windows Server 2022", "10.0 (20348)"));
        await using var factory = CreateFactory(directory);

        await WithCoverageAsync(factory, c => c.RefreshAsync("example.local", CancellationToken.None));
        directory.Servers.Clear();
        directory.Servers.Add(new DirectoryComputer("SRV09", null, "Windows Server 2025", "10.0 (26100)"));
        await WithCoverageAsync(factory, c => c.RefreshAsync("example.local", CancellationToken.None));

        var report = await WithCoverageAsync(factory, c => c.GetReportAsync(CancellationToken.None));
        Assert.Equal("SRV09", Assert.Single(report.Missing).Name);
    }

    [Fact]
    public async Task UnreachableDirectory_KeepsThePreviousList()
    {
        var directory = new FakeDirectory();
        directory.Servers.Add(new DirectoryComputer("SRV01", null, "Windows Server 2022", "10.0 (20348)"));
        await using var factory = CreateFactory(directory);
        await WithCoverageAsync(factory, c => c.RefreshAsync("example.local", CancellationToken.None));

        directory.Failure = new System.DirectoryServices.Protocols.LdapException("unreachable");
        await Assert.ThrowsAsync<System.DirectoryServices.Protocols.LdapException>(() => WithCoverageAsync(factory, c => c.RefreshAsync("example.local", CancellationToken.None)));

        Assert.Equal(1, (await WithCoverageAsync(factory, c => c.GetReportAsync(CancellationToken.None))).Supported);
    }

    [Fact]
    public async Task Pages_ShowCoverage_AndWorkWithoutData()
    {
        var directory = new FakeDirectory();
        await using var factory = CreateFactory(directory);
        using var http = factory.CreateClient();
        await CreateUserAsync(factory);
        using var browser = new DashboardClient(factory);
        using (await browser.LoginAsync("reader", Password))
        {
        }

        var empty = await GetHtmlAsync(browser, "/coverage");
        Assert.Contains("has not been read yet", empty, StringComparison.Ordinal);

        directory.Servers.Add(new DirectoryComputer("SRV-NOAGENT", "srv-noagent.example.local", "Windows Server 2022 <b>Standard</b>", "10.0 (20348)"));
        await WithCoverageAsync(factory, c => c.RefreshAsync("example.local", CancellationToken.None));

        var page = await GetHtmlAsync(browser, "/coverage");
        var overview = await GetHtmlAsync(browser, "/");
        Assert.Contains("SRV-NOAGENT", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>Standard</b>", page, StringComparison.Ordinal);
        Assert.Contains("AD coverage", overview, StringComparison.Ordinal);
        Assert.Contains("1 without agent", overview, StringComparison.Ordinal);
    }

    private sealed class CoverageFactory(FakeDirectory directory) : ServerFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddSingleton<IDirectoryLookup>(directory));
        }
    }

    private static CoverageFactory CreateFactory(FakeDirectory directory) => new(directory);

    private static async Task<T> WithCoverageAsync<T>(ServerFactory factory, Func<DirectoryCoverage, Task<T>> action)
    {
        factory.StartServer();
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<DirectoryCoverage>());
    }

    private static async Task RegisterApprovedAsync(ServerFactory factory, HttpClient http, string host, string domain)
    {
        var credentials = await RegisterTests.RegisterAsync(http);
        await factory.WithDbAsync(db => db.Clients.Where(c => c.Id == credentials.ClientId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.State, RegistrationState.Approved).SetProperty(c => c.HostName, host).SetProperty(c => c.DomainName, domain), TestContext.Current.CancellationToken));
    }

    private static Task CreateUserAsync(ServerFactory factory) => factory.CreateUserAsync("reader", Password, AdminRoles.Reader);

    private static async Task<string> GetHtmlAsync(DashboardClient browser, string path)
    {
        using var response = await browser.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }
}
