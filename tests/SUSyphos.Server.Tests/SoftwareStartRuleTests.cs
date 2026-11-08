using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using SUSyphos.Server.Data;
using SUSyphos.Server.Rollouts;
using SUSyphos.Server.Software;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

/// <summary>Software versions (WinGet) in the update catalog and the start rule conditions kind, package ID and publisher.</summary>
public sealed class SoftwareStartRuleTests
{
    [Fact]
    public void SoftwareUpdateId_IsStable_NameBased_AndIgnoresTheCaseOfThePackageId()
    {
        var id = SoftwarePackages.SoftwareUpdateId("7zip.7zip", "26.06");

        Assert.Equal(id, SoftwarePackages.SoftwareUpdateId("7Zip.7Zip", "26.06"));
        Assert.NotEqual(id, SoftwarePackages.SoftwareUpdateId("7zip.7zip", "26.07"));
        Assert.NotEqual(id, SoftwarePackages.SoftwareUpdateId("7zip.7zip.x", "26.06"));
        Assert.Equal(5, id.Version);
        Assert.Throws<ArgumentException>(() => SoftwarePackages.SoftwareUpdateId(string.Empty, "1.0"));
    }

    [Theory]
    [InlineData("7zip.7zip", true)]
    [InlineData("Microsoft.VCRedist.2015+.x64", true)]
    [InlineData("Notepad++.Notepad++", true)]
    [InlineData("Git_Hub.cli-tool", true)]
    [InlineData("nodots", false)]
    [InlineData("a..b", false)]
    [InlineData(".a.b", false)]
    [InlineData("a.b c", false)]
    [InlineData("a.b;drop", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void PackageId_IsValidated(string? packageId, bool valid)
    {
        Assert.Equal(valid, SoftwarePackages.IsValidId(packageId));
    }

    [Fact]
    public void PackageId_LongerThanTheLimit_IsRefused()
    {
        Assert.False(SoftwarePackages.IsValidId("a." + new string('b', PackageEntity.MaxIdLength)));
    }

    [Fact]
    public void FindStartRule_TellsWindowsUpdatesAndSoftwareApart()
    {
        var windows = new StartRuleEntity { Id = 1, Priority = 1000, Kind = UpdateKind.WindowsUpdate };
        var software = new StartRuleEntity { Id = 2, Priority = 1001, Kind = UpdateKind.Software, Mode = StartMode.Manual };
        var microsoft = new StartRuleEntity { Id = 3, Priority = 10, Kind = UpdateKind.Software, PackageIdPattern = "Microsoft.*" };
        var publisher = new StartRuleEntity { Id = 4, Priority = 20, Kind = UpdateKind.Software, Publisher = "igor pavlov" };
        StartRuleEntity[] rules = [windows, software, microsoft, publisher];

        Assert.Same(windows, RolloutService.FindStartRule(rules, WindowsUpdate()));
        Assert.Same(microsoft, RolloutService.FindStartRule(rules, Software("Microsoft.PowerShell", "7.6.0")));
        Assert.Same(publisher, RolloutService.FindStartRule(rules, Software("7zip.7zip", "26.06"), "Igor Pavlov"));
        Assert.Same(software, RolloutService.FindStartRule(rules, Software("7zip.7zip", "26.06"), "Someone else"));
        Assert.Same(software, RolloutService.FindStartRule(rules, Software("7zip.7zip", "26.06")));
    }

    [Fact]
    public void FindStartRule_WithoutKind_MatchesBoth_AndPackageConditionsNeverMatchWindowsUpdates()
    {
        var both = new StartRuleEntity { Id = 1, Priority = 1, TitlePattern = "*" };
        var package = new StartRuleEntity { Id = 2, Priority = 2, PackageIdPattern = "*" };

        Assert.Same(both, RolloutService.FindStartRule([both], WindowsUpdate()));
        Assert.Same(both, RolloutService.FindStartRule([both], Software("7zip.7zip", "26.06")));
        Assert.Null(RolloutService.FindStartRule([package], WindowsUpdate()));
        Assert.Null(RolloutService.FindStartRule([new StartRuleEntity { Id = 3, Classification = "Security Updates", Kind = UpdateKind.Software }], Software("7zip.7zip", "26.06")));
    }

    [Fact]
    public async Task SoftwareRule_IsSaved_WithItsConditions()
    {
        await using var scenario = await RolloutScenario.CreateAsync();

        var outcome = await scenario.AdministerAsync(a => a.CreateStartRuleAsync(
            50, string.Empty, string.Empty, "7-Zip *", StartMode.Automatic, 2, "admin", CancellationToken.None, software: new SoftwareConditions(UpdateKind.Software, " 7zip.* ", " Igor Pavlov ")));

        Assert.True(outcome.Succeeded, outcome.Error);
        var rule = await scenario.Factory.WithDbAsync(db => db.StartRules.AsNoTracking().SingleAsync(r => r.Priority == 50, TestContext.Current.CancellationToken));
        Assert.Equal(UpdateKind.Software, rule.Kind);
        Assert.Equal("7zip.*", rule.PackageIdPattern);
        Assert.Equal("Igor Pavlov", rule.Publisher);
        Assert.Null(rule.Classification);
        Assert.True(await scenario.Factory.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Action == "StartRule.Created" && e.Details!.Contains("package 7zip.*"), TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task RuleWithoutSoftwareConditions_AppliesToWindowsUpdates()
    {
        await using var scenario = await RolloutScenario.CreateAsync();

        Assert.True((await scenario.AdministerAsync(a => a.CreateStartRuleAsync(50, "Security Updates", null, null, StartMode.Automatic, 0, "admin", CancellationToken.None))).Succeeded);

        Assert.Equal(UpdateKind.WindowsUpdate, await scenario.Factory.WithDbAsync(db => db.StartRules.Where(r => r.Priority == 50).Select(r => r.Kind).SingleAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(UpdateKind.WindowsUpdate, await scenario.Factory.WithDbAsync(db => db.StartRules.Where(r => r.Id == StartRuleEntity.CatchAllId).Select(r => r.Kind).SingleAsync(TestContext.Current.CancellationToken)));
    }

    public static readonly TheoryData<string?, string?, int?, bool, int, string?, string?, string> InvalidRules = new()
    {
        { null, null, null, false, (int)UpdateKind.WindowsUpdate, "Microsoft.*", null, "conditions for software" },
        { null, null, null, false, -1, "Microsoft.*", null, "conditions for software" },
        { null, null, null, false, (int)UpdateKind.WindowsUpdate, null, "Microsoft", "conditions for software" },
        { "Security Updates", null, null, false, (int)UpdateKind.Software, null, null, "Windows updates only" },
        { null, "Critical", null, false, (int)UpdateKind.Software, null, null, "Windows updates only" },
        { null, null, (int)SUSyphos.Contracts.Agents.RebootBehavior.NeverReboots, true, (int)UpdateKind.Software, null, null, "Windows updates only" },
        { null, null, null, false, (int)UpdateKind.Software, "Microsoft.*'; DROP TABLE StartRules;--", null, "package ID pattern" },
        { null, null, null, false, (int)UpdateKind.Software, "a b", null, "package ID pattern" },
        { null, null, null, false, (int)UpdateKind.Software, null, "Igor\nPavlov", "publisher" },
        { null, null, null, false, 7, null, null, "kind is invalid" },
    };

    [Theory]
    [MemberData(nameof(InvalidRules))]
    public async Task InvalidSoftwareConditions_AreRefused(string? classification, string? severity, int? restart, bool outsideWindows, int kind, string? packageIdPattern, string? publisher, string error)
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        UpdateKind? conditionKind = kind < 0 ? null : (UpdateKind)kind;

        var outcome = await scenario.AdministerAsync(a => a.CreateStartRuleAsync(
            50, classification, severity, null, StartMode.Automatic, 0, "admin", CancellationToken.None, null, (SUSyphos.Contracts.Agents.RebootBehavior?)restart, outsideWindows, new SoftwareConditions(conditionKind, packageIdPattern, publisher)));

        Assert.False(outcome.Succeeded);
        Assert.Contains(error, outcome.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(await scenario.Factory.WithDbAsync(db => db.StartRules.AnyAsync(r => r.Priority == 50, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task TooLongConditions_AreRefused()
    {
        await using var scenario = await RolloutScenario.CreateAsync();

        var pattern = await scenario.AdministerAsync(a => a.CreateStartRuleAsync(50, null, null, null, StartMode.Automatic, 0, "admin", CancellationToken.None, software: new SoftwareConditions(UpdateKind.Software, new string('a', PackageEntity.MaxIdLength + 1))));
        var publisher = await scenario.AdministerAsync(a => a.CreateStartRuleAsync(50, null, null, null, StartMode.Automatic, 0, "admin", CancellationToken.None, software: new SoftwareConditions(UpdateKind.Software, null, new string('p', StartRuleEntity.MaxPublisherLength + 1))));

        Assert.False(pattern.Succeeded);
        Assert.False(publisher.Succeeded);
    }

    [Fact]
    public async Task DefaultRuleForSoftware_IsManual_CreatedOnce_AndStaysDeleted()
    {
        // The test server switches the startup creation off (Rollout:CreateSoftwareRule), so the call here is the first.
        await using var factory = new ServerFactory();
        factory.StartServer();
        await using var scope = factory.Services.CreateAsyncScope();
        var administration = scope.ServiceProvider.GetRequiredService<RolloutAdministration>();

        Assert.True(await administration.EnsureSoftwareRuleAsync(CancellationToken.None));
        var rule = await factory.WithDbAsync(db => db.StartRules.AsNoTracking().SingleAsync(r => r.Kind == UpdateKind.Software, TestContext.Current.CancellationToken));
        Assert.Equal(StartMode.Manual, rule.Mode);
        Assert.True(rule.Priority > 1000);
        Assert.False(await administration.EnsureSoftwareRuleAsync(CancellationToken.None));

        Assert.True((await administration.DeleteStartRuleAsync(rule.Id, "admin", CancellationToken.None)).Succeeded);
        Assert.False(await administration.EnsureSoftwareRuleAsync(CancellationToken.None));
        Assert.False(await factory.WithDbAsync(db => db.StartRules.AnyAsync(r => r.Kind == UpdateKind.Software, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task DefaultRuleForSoftware_IsSkipped_WhenASoftwareRuleExists()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        Assert.True((await scenario.AdministerAsync(a => a.CreateStartRuleAsync(50, null, null, null, StartMode.Automatic, 0, "admin", CancellationToken.None, software: new SoftwareConditions(UpdateKind.Software)))).Succeeded);

        Assert.False(await scenario.WithServiceAsync<RolloutAdministration, bool>(a => a.EnsureSoftwareRuleAsync(CancellationToken.None)));
        Assert.Equal(1, await scenario.Factory.WithDbAsync(db => db.StartRules.CountAsync(r => r.Kind == UpdateKind.Software, TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public async Task SoftwareWithAgreements_StartsAutomaticallyOnlyOnceAccepted(bool agreements, bool accepted, bool starts)
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        Assert.True((await scenario.AdministerAsync(a => a.CreateStartRuleAsync(50, null, null, null, StartMode.Automatic, 0, "admin", CancellationToken.None, software: new SoftwareConditions(UpdateKind.Software)))).Succeeded);
        var update = await AddSoftwareAsync(scenario, "Vendor.Tool", "1.2.3", agreements, accepted);

        await scenario.WithServiceAsync<RolloutService, bool>(async s => { await s.AutoStartAsync([(update.UpdateId, update.Revision)], CancellationToken.None); return true; });

        Assert.Equal(starts, await scenario.Factory.WithDbAsync(db => db.Rollouts.AnyAsync(r => r.UpdateId == update.UpdateId, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task SoftwareVersionWithoutPackage_NeverStartsAutomatically()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        Assert.True((await scenario.AdministerAsync(a => a.CreateStartRuleAsync(50, null, null, null, StartMode.Automatic, 0, "admin", CancellationToken.None, software: new SoftwareConditions(null)))).Succeeded);
        var update = Software("Vendor.Orphan", "1.0");
        await scenario.Factory.WithDbAsync(async db => { db.Updates.Add(update); await db.SaveChangesAsync(TestContext.Current.CancellationToken); return true; });

        await scenario.WithServiceAsync<RolloutService, bool>(async s => { await s.AutoStartAsync([(update.UpdateId, update.Revision)], CancellationToken.None); return true; });

        Assert.False(await scenario.Factory.WithDbAsync(db => db.Rollouts.AnyAsync(r => r.UpdateId == update.UpdateId, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task WindowsRule_DoesNotStartSoftware()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var update = await AddSoftwareAsync(scenario, "Vendor.Tool", "1.2.3", agreements: false, accepted: false);

        // Only the catch-all rule for Windows updates exists: the software version stays queued.
        await scenario.WithServiceAsync<RolloutService, bool>(async s => { await s.AutoStartAsync([(update.UpdateId, update.Revision)], CancellationToken.None); return true; });

        Assert.False(await scenario.Factory.WithDbAsync(db => db.Rollouts.AnyAsync(r => r.UpdateId == update.UpdateId, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Schema_RefusesAnAssignmentWithoutExactlyOneTarget()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        await AddSoftwareAsync(scenario, "Vendor.Tool", "1.2.3", agreements: false, accepted: false);

        await Assert.ThrowsAsync<DbUpdateException>(() => scenario.Factory.WithDbAsync(async db =>
        {
            db.PackageAssignments.Add(new PackageAssignmentEntity { PackageId = "Vendor.Tool", TargetKind = PackageAssignmentTarget.Server, CreatedBy = "admin", CreatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return true;
        }));
    }

    [Fact]
    public async Task Schema_ComparesPackageIdsWithoutCase()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        await AddSoftwareAsync(scenario, "Vendor.Tool", "1.2.3", agreements: false, accepted: false);

        Assert.True(await scenario.Factory.WithDbAsync(db => db.Packages.AnyAsync(p => p.PackageId == "vendor.tool", TestContext.Current.CancellationToken)));
        Assert.True(await scenario.Factory.WithDbAsync(db => db.PackageVersions.AnyAsync(v => v.PackageId == "VENDOR.TOOL", TestContext.Current.CancellationToken)));
    }

    private static UpdateEntity WindowsUpdate() => new() { UpdateId = Guid.NewGuid(), Revision = 1, Title = "2026-11 Cumulative Update", Classification = "Security Updates" };

    private static UpdateEntity Software(string packageId, string version) => new()
    {
        UpdateId = SoftwarePackages.SoftwareUpdateId(packageId, version),
        Revision = SoftwarePackages.SoftwareRevision,
        Title = SoftwarePackages.Title(packageId, version),
        Kind = UpdateKind.Software,
        PackageId = packageId,
        PackageVersion = version,
        FirstSeenAtUtc = DateTime.UtcNow,
    };

    private static async Task<UpdateEntity> AddSoftwareAsync(RolloutScenario scenario, string packageId, string version, bool agreements, bool accepted)
    {
        var update = Software(packageId, version);
        await scenario.Factory.WithDbAsync(async db =>
        {
            db.Packages.Add(new PackageEntity
            {
                PackageId = packageId,
                Name = packageId,
                Publisher = "Vendor",
                HasAgreements = agreements,
                AgreementsAcceptedAtUtc = accepted ? DateTime.UtcNow : null,
                AgreementsAcceptedBy = accepted ? "admin" : null,
                AddedAtUtc = DateTime.UtcNow,
                AddedBy = "admin",
            });
            db.Updates.Add(update);
            db.PackageVersions.Add(new PackageVersionEntity { PackageId = packageId, Version = version, UpdateId = update.UpdateId });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return true;
        });
        return update;
    }
}
