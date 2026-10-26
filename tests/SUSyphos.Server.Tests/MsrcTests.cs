using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using SUSyphos.Server.Data;
using SUSyphos.Server.Security;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

/// <summary>The MSRC security data: parsing a real document excerpt, keeping the last months, and the baseline per server.</summary>
public sealed class MsrcTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static MsrcDocument Excerpt()
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "msrc-2026-Oct-excerpt.json"));
        return JsonSerializer.Deserialize<MsrcDocument>(stream, Json)!;
    }

    [Fact]
    public void Parser_ReadsBaselinesFixesAndVulnerabilities_ForWindowsServerOnly()
    {
        var month = MsrcParser.Parse("2026-Oct", Excerpt());

        // The baseline is the newest build of the month; older fixes in the document belong to revised CVEs.
        var server2022 = Assert.Single(month.Baselines, b => b.Build == 20348);
        Assert.Equal(5761, server2022.Revision);
        Assert.Equal("5126655", server2022.Kb);
        Assert.Equal(new DateTime(2026, 10, 13, 7, 0, 0, DateTimeKind.Utc), server2022.ReleasedAtUtc);
        Assert.Equal([14393, 17763, 20348, 26100], month.Baselines.Select(b => b.Build).Order());

        Assert.Contains(month.Fixes, f => f.Cve == "CVE-2026-69354" && f.Build == 20348 && f.Revision == 5622);
        Assert.DoesNotContain(month.Vulnerabilities, v => v.Cve == "CVE-2026-100236");

        var exploited = Assert.Single(month.Vulnerabilities, v => v.Cve == "CVE-2026-96951");
        Assert.True(exploited.Exploited);
        Assert.NotNull(exploited.Severity);

        var license = Assert.Single(month.Vulnerabilities, v => v.Cve == "CVE-2026-100201");
        Assert.Equal("Important", license.Severity);
        Assert.Equal("Elevation of Privilege", license.Impact);
        Assert.Equal(7.0, license.CvssScore);
        Assert.False(license.Exploited);
    }

    [Theory]
    [InlineData("Windows Server 2022", 20348)]
    [InlineData("Windows Server 2016 (Server Core installation)", 14393)]
    [InlineData("Windows Server 2022, 23H2 Edition (Server Core installation)", null)]
    [InlineData("Windows 11 Version 24H2 for x64-based Systems", null)]
    [InlineData(null, null)]
    public void OnlyTheServerVersionsOfTheLifecycleTableCount(string? product, int? build) => Assert.Equal(build, MsrcParser.ServerBuild(product));

    [Theory]
    [InlineData("10.0.20348.5761", 20348, 5761)]
    [InlineData("10.0.20348", null, null)]
    [InlineData("a.b.c.d", null, null)]
    public void FixedBuilds_AreParsed(string text, int? build, int? revision)
    {
        var parsed = MsrcParser.ParseBuild(text);
        Assert.Equal(build, parsed?.Build);
        Assert.Equal(revision, parsed?.Revision);
    }

    [Theory]
    [InlineData(2026, 10, 13)]
    [InlineData(2026, 11, 10)]
    [InlineData(2027, 1, 12)]
    public void PatchDay_IsTheSecondTuesday(int year, int month, int day) => Assert.Equal(new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc), MsrcSync.PatchDay(year, month));

    [Theory]
    [InlineData("2026-Oct", true)]
    [InlineData("2026-Okt", false)]
    [InlineData("../secret", false)]
    [InlineData("", false)]
    public void OnlyDocumentIdsGoIntoTheAddress(string id, bool valid) => Assert.Equal(valid, MsrcSync.IsDocumentId(id));

    [Fact]
    public void Snapshot_ComparesTheServerBuild_WithTheBaselinesAndOpenFixes()
    {
        var month = MsrcParser.Parse("2026-Oct", Excerpt());
        var september = new SecurityBaselineEntity { Build = 20348, Month = "2026-Sep", ReleasedAtUtc = new DateTime(2026, 9, 8, 17, 0, 0, DateTimeKind.Utc), Revision = 5622, Kb = "5122882" };
        var snapshot = new SecuritySnapshot([.. month.Baselines, september], month.Fixes, month.Vulnerabilities);

        var current = snapshot.Of("10.0.20348.5761");
        var oneBehind = snapshot.Of("10.0.20348.5700");
        var twoBehind = snapshot.Of("10.0.20348.5000");

        Assert.Equal(BaselineState.Current, current.State);
        Assert.Empty(current.Open);
        Assert.Equal(BaselineState.OneBehind, oneBehind.State);
        Assert.Equal("2026-Oct", oneBehind.Missing?.Month);
        Assert.Contains(oneBehind.Open, v => v.Cve == "CVE-2026-100201");
        Assert.DoesNotContain(oneBehind.Open, v => v.Cve == "CVE-2026-69354");
        Assert.Equal(BaselineState.TwoBehind, twoBehind.State);
        Assert.Equal("2026-Sep", twoBehind.Missing?.Month);
        Assert.Contains(twoBehind.Open, v => v.Cve == "CVE-2026-69354");
        Assert.Equal(BaselineState.Unknown, snapshot.Of("10.0.9999.1").State);
        Assert.Contains(snapshot.FixedBy("5126655"), v => v.Cve == "CVE-2026-100201");
    }

    [Fact]
    public async Task Sync_KeepsTheLastMonths_FetchesOnlyChangedDocuments_AndSurvivesFailures()
    {
        var source = new FakeMsrcSource(Excerpt());
        await using var factory = new MsrcServerFactory(source);
        factory.StartServer();

        var first = await RunAsync(factory, force: false);
        Assert.Null(first.LastError);
        Assert.Equal(["2026-Oct", "2026-Sep", "2026-Aug"], first.Months);
        Assert.Equal(3, source.Fetched.Count);
        Assert.Equal(12, await factory.WithDbAsync(db => db.SecurityBaselines.CountAsync(TestContext.Current.CancellationToken)));

        // Unchanged documents are not fetched again; a month that drops out of the window is removed.
        source.Fetched.Clear();
        source.Updates.Insert(0, new MsrcUpdate { Id = "2026-Nov", DocumentTitle = "November 2026 Security Updates", InitialReleaseDate = new DateTime(2026, 11, 10), CurrentReleaseDate = new DateTime(2026, 11, 10) });
        var second = await RunAsync(factory, force: false);
        Assert.Equal(["2026-Nov"], source.Fetched);
        Assert.Equal(["2026-Nov", "2026-Oct", "2026-Sep"], second.Months);
        Assert.False(await factory.WithDbAsync(db => db.SecurityBaselines.AnyAsync(b => b.Month == "2026-Aug", TestContext.Current.CancellationToken)));

        // A failure keeps the data and records the error.
        source.Fail = true;
        var failed = await RunAsync(factory, force: true);
        Assert.NotNull(failed.LastError);
        Assert.Equal(second.LastSuccessUtc, failed.LastSuccessUtc);
        Assert.Equal(12, await factory.WithDbAsync(db => db.SecurityBaselines.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Pages_ShowBaselineVulnerabilitiesAndStatistics()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var behind = await scenario.AddClientAsync("BEHIND01", RingEntity.PilotId);
        await scenario.AddClientAsync("CURRENT01", RingEntity.PilotId);
        var month = MsrcParser.Parse("2026-Oct", Excerpt());
        await scenario.Factory.WithDbAsync(async db =>
        {
            await db.Clients.Where(c => c.Id == behind.ClientId).ExecuteUpdateAsync(s => s.SetProperty(c => c.OsVersion, "10.0.20348.5700"));
            await db.Clients.Where(c => c.HostName == "CURRENT01").ExecuteUpdateAsync(s => s.SetProperty(c => c.OsVersion, "10.0.20348.5761"));
            db.SecurityBaselines.AddRange(month.Baselines);
            db.SecurityFixes.AddRange(month.Fixes);
            db.SecurityVulnerabilities.AddRange(month.Vulnerabilities);
            db.Updates.Add(new UpdateEntity { UpdateId = Guid.NewGuid(), Revision = 1, KbArticle = "5126655", Title = "2026-10 Cumulative Update for Microsoft server operating system version 21H2 (KB5126655)", FirstSeenAtUtc = DateTime.UtcNow });
            return await db.SaveChangesAsync();
        });
        await scenario.Factory.CreateUserAsync("reader", "correct horse battery", SUSyphos.Server.Administration.AdminRoles.Reader);
        using var reader = new DashboardClient(scenario.Factory);
        using (await reader.LoginAsync("reader", "correct horse battery"))
        {
        }

        async Task<string> GetAsync(string path) => await (await reader.GetAsync(path)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var page = await GetAsync("/operating-systems");
        var security = await GetAsync($"/servers/{behind.ClientId}?tab=security");
        var list = await GetAsync("/servers?f.security=1%20behind");
        var update = await GetAsync($"/updates/{await scenario.Factory.WithDbAsync(db => db.Updates.Where(u => u.KbArticle == "5126655").Select(u => u.UpdateId).SingleAsync(TestContext.Current.CancellationToken))}/1");
        var overview = await GetAsync("/");
        var chart = await GetAsync("/?stat=vulnerabilities");

        Assert.Contains("Windows Server 2022", page, StringComparison.Ordinal);
        Assert.Contains("CVE-2026-100201", page, StringComparison.Ordinal);
        Assert.Contains("20348.5761", page, StringComparison.Ordinal);
        Assert.Contains("1 behind", security, StringComparison.Ordinal);
        Assert.Contains("CVE-2026-100201", security, StringComparison.Ordinal);
        Assert.DoesNotContain("CVE-2026-69354", security, StringComparison.Ordinal);
        Assert.Contains("BEHIND01", list, StringComparison.Ordinal);
        Assert.DoesNotContain("CURRENT01", list, StringComparison.Ordinal);
        Assert.Contains("Fixes vulnerabilities", update, StringComparison.Ordinal);
        Assert.Contains("Security baseline", overview, StringComparison.Ordinal);
        Assert.Contains("servers have a vulnerability Microsoft knows to be exploited", overview, StringComparison.Ordinal);
        Assert.Contains("<option value=\"vulnerabilities\" selected", chart, StringComparison.Ordinal);
    }

    private static async Task<MsrcStatus> RunAsync(ServerFactory factory, bool force)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MsrcSync>().RunAsync(force, CancellationToken.None);
    }

    private sealed class MsrcServerFactory(IMsrcSource source) : ServerFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            Microsoft.AspNetCore.TestHost.WebHostBuilderExtensions.ConfigureTestServices(builder, services => services.AddSingleton(source));
        }
    }

    private sealed class FakeMsrcSource(MsrcDocument document) : IMsrcSource
    {
        public List<MsrcUpdate> Updates { get; } =
        [
            new() { Id = "2026-Oct", DocumentTitle = "October 2026 Security Updates", InitialReleaseDate = new DateTime(2026, 10, 13), CurrentReleaseDate = new DateTime(2026, 10, 26) },
            new() { Id = "2026-Sep", DocumentTitle = "September 2026 Security Updates", InitialReleaseDate = new DateTime(2026, 9, 8), CurrentReleaseDate = new DateTime(2026, 9, 30) },
            new() { Id = "2026-Aug", DocumentTitle = "August 2026 Security Updates", InitialReleaseDate = new DateTime(2026, 8, 11), CurrentReleaseDate = new DateTime(2026, 8, 20) },
            new() { Id = "2026-Jul", DocumentTitle = "July 2026 Security Updates", InitialReleaseDate = new DateTime(2026, 7, 14), CurrentReleaseDate = new DateTime(2026, 7, 20) },
            new() { Id = "1999-Sep", DocumentTitle = "Mariner Release Notes", InitialReleaseDate = new DateTime(2027, 1, 1), CurrentReleaseDate = new DateTime(2027, 1, 1) },
        ];

        public List<string> Fetched { get; } = [];

        public bool Fail { get; set; }

        public Task<IReadOnlyList<MsrcUpdate>> GetUpdatesAsync(CancellationToken cancellationToken) =>
            Fail ? throw new HttpRequestException("MSRC unreachable") : Task.FromResult<IReadOnlyList<MsrcUpdate>>([.. Updates]);

        public Task<MsrcDocument> GetDocumentAsync(string id, CancellationToken cancellationToken)
        {
            Fetched.Add(id);
            return Task.FromResult(document);
        }
    }
}
