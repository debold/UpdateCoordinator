using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Data;
using SUSyphos.Server.Rollouts;
using SUSyphos.Server.Software;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

/// <summary>Software versions go through the rings like updates: needed by servers with an older version installed.</summary>
public sealed class SoftwareReleaseTests
{
    private static readonly Guid NewZip = SoftwarePackages.SoftwareUpdateId("7zip.7zip", "26.06");

    private static async Task<RegisterResponse> AddServerWithZipAsync(RolloutScenario scenario, string host, string version)
    {
        var client = await scenario.AddClientAsync(host, RingEntity.PilotId);
        await scenario.Factory.WithDbAsync(async db =>
        {
            if (!await db.Packages.AnyAsync(TestContext.Current.CancellationToken))
            {
                DemoSoftware.Add(db, [], [], DateTime.UtcNow);
            }

            db.ClientSoftware.Add(new ClientSoftwareEntity
            {
                ClientId = client.ClientId,
                View = SoftwareView.Machine64,
                KeyName = "7-Zip",
                DisplayName = "7-Zip",
                PackageId = "7zip.7zip",
                InstalledVersion = version,
                MatchKind = SoftwareMatch.WinGet,
                ReportedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return true;
        });
        return client;
    }

    private static async Task StartRolloutAsync(RolloutScenario scenario)
    {
        await scenario.WithServiceAsync<ServerDbContext, bool>(async db =>
        {
            var now = DateTime.UtcNow;
            db.Rollouts.Add(new RolloutEntity { UpdateId = NewZip, Revision = 1, State = RolloutState.Active, StartedAtUtc = now, StartedBy = "admin" });
            db.RolloutRings.Add(new RolloutRingEntity { UpdateId = NewZip, Revision = 1, RingId = RingEntity.PilotId, State = RingRolloutState.Released, ReleasedAtUtc = now, StateChangedAtUtc = now });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return true;
        });
        await scenario.WithServiceAsync<ReleasePlanner, bool>(async p => { await p.SyncAsync(null, CancellationToken.None); return true; });
    }

    [Fact]
    public async Task ServerWithAnOlderVersion_GetsTheNewOneReleased_OthersDoNot()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var old = await AddServerWithZipAsync(scenario, "OLD01", "26.05");
        var current = await AddServerWithZipAsync(scenario, "NEW01", "26.06");

        await StartRolloutAsync(scenario);

        var release = Assert.Single((await scenario.ReleasesAsync(old)).Releases, r => r.UpdateId == NewZip);
        Assert.Equal(new SoftwareRelease("7zip.7zip", "26.06", false), release.Software);
        Assert.DoesNotContain((await scenario.ReleasesAsync(current)).Releases, r => r.UpdateId == NewZip);
    }

    [Fact]
    public async Task SwitchedOff_NoSoftwareIsReleased()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var old = await AddServerWithZipAsync(scenario, "OLD01", "26.05");
        await scenario.WithServiceAsync<SoftwareFeature, bool>(async f => { await f.SetAsync(false, "admin", CancellationToken.None); return true; });

        await StartRolloutAsync(scenario);

        var releases = await scenario.ReleasesAsync(old);
        Assert.Empty(releases.Releases);
        Assert.True(releases.SoftwareDisabled);
    }

    [Fact]
    public async Task NeededVersion_StartsByAnAutomaticSoftwareRule_UnneededDoesNot()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        await AddServerWithZipAsync(scenario, "OLD01", "26.05");
        Assert.True((await scenario.AdministerAsync(a => a.CreateStartRuleAsync(50, null, null, "7-Zip*", StartMode.Automatic, 0, "admin", CancellationToken.None, software: new SoftwareConditions(UpdateKind.Software)))).Succeeded);

        await scenario.WithServiceAsync<RolloutService, int>(s => s.AutoStartWaitingAsync(CancellationToken.None));

        Assert.True(await scenario.Factory.WithDbAsync(db => db.Rollouts.AnyAsync(r => r.UpdateId == NewZip, TestContext.Current.CancellationToken)));
        var notepad = SoftwarePackages.SoftwareUpdateId("Notepad++.Notepad++", "8.9.9");
        Assert.False(await scenario.Factory.WithDbAsync(db => db.Rollouts.AnyAsync(r => r.UpdateId == notepad, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Needs_CompareVersionsLikeWinGet_AndSkipBlockedOnes()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var server = await AddServerWithZipAsync(scenario, "OLD01", "9.20");
        await scenario.Factory.WithDbAsync(db => db.PackageVersions.Where(v => v.Version == "26.05").ExecuteUpdateAsync(s => s.SetProperty(v => v.ScanState, InstallerScanState.Detected), TestContext.Current.CancellationToken));

        var (needed, installed) = await scenario.WithServiceAsync<ServerDbContext, (IReadOnlyList<SoftwareNeed>, IReadOnlyList<SoftwareNeed>)>(db => SoftwareNeeds.LoadAsync(db, [server.ClientId], CancellationToken.None));

        Assert.Equal([NewZip], needed.Select(n => n.UpdateId));
        Assert.Empty(installed);
    }
}
