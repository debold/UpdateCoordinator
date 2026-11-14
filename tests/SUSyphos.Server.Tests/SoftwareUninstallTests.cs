using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Agents;
using SUSyphos.Server.Components;
using SUSyphos.Server.Data;
using SUSyphos.Server.Software;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

/// <summary>Uninstalling software with WinGet (Marc, 2026-11-14): server inventory, package page, assignments and catalog.</summary>
public sealed class SoftwareUninstallTests
{
    private const string Password = "correct horse battery";
    private const string Package = "Notepad++.Notepad++";

    private static async Task<(RolloutScenario Scenario, RegisterResponse First, RegisterResponse Second, int Group)> SetUpAsync(bool winGet = true)
    {
        var scenario = await RolloutScenario.CreateAsync();
        var first = await scenario.AddClientAsync("APP01", RingEntity.PilotId);
        var second = await scenario.AddClientAsync("APP02", RingEntity.ProductionId);
        var group = await scenario.Factory.WithDbAsync(async db =>
        {
            DemoSoftware.Add(db, [], [], DateTime.UtcNow);
            var entity = new GroupEntity { Name = "Web farm" };
            db.Groups.Add(entity);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            db.ClientGroups.Add(new ClientGroupEntity { ClientId = first.ClientId, GroupId = entity.Id, Manual = true });
            foreach (var client in new[] { first.ClientId, second.ClientId })
            {
                db.ClientSoftware.Add(new ClientSoftwareEntity { ClientId = client, View = SoftwareView.Machine64, KeyName = "Notepad++", DisplayName = "Notepad++ (64-bit x64)", PackageId = Package, InstalledVersion = "8.9.9", MatchKind = SoftwareMatch.WinGet, ReportedAtUtc = DateTime.UtcNow });
            }

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await db.Clients.ExecuteUpdateAsync(s => s.SetProperty(c => c.SoftwareState, winGet ? SoftwareSupportState.Supported : SoftwareSupportState.NotSupported), TestContext.Current.CancellationToken);
            return entity.Id;
        });
        return (scenario, first, second, group);
    }

    private static Task<Outcome> QueueOneAsync(RolloutScenario scenario, Guid client, AgentActionKind kind = AgentActionKind.UninstallInWindow) =>
        scenario.WithServiceAsync<AgentActionService, Outcome>(a => a.QueueSoftwareUninstallAsync(client, Package, kind, "admin", CancellationToken.None));

    [Fact]
    public async Task OneServer_QueuesTheUninstall_OnceAndOnlyWhereWinGetWorks()
    {
        var (scenario, first, _, _) = await SetUpAsync();
        await using (scenario)
        {
            Assert.True((await QueueOneAsync(scenario, first.ClientId, AgentActionKind.Uninstall)).Succeeded);
            Assert.False((await QueueOneAsync(scenario, first.ClientId)).Succeeded);

            var action = Assert.Single(await scenario.Factory.WithDbAsync(db => db.AgentActions.ToListAsync(TestContext.Current.CancellationToken)));
            Assert.Equal((AgentActionKind.Uninstall, Package, (Guid?)null), (action.Kind, action.PackageId, action.BatchId));
        }
    }

    [Theory]
    [InlineData(false, AgentActionKind.UninstallInWindow)]
    [InlineData(true, AgentActionKind.RestartServer)]
    public async Task Uninstall_IsRefused_WithoutWinGet_OrWithAnotherKind(bool winGet, AgentActionKind kind)
    {
        var (scenario, first, _, _) = await SetUpAsync(winGet);
        await using (scenario)
        {
            Assert.False((await QueueOneAsync(scenario, first.ClientId, kind)).Succeeded);
            Assert.False((await scenario.WithServiceAsync<AgentActionService, Outcome>(a => a.QueueSoftwareUninstallAsync(first.ClientId, "Not.Installed", AgentActionKind.Uninstall, "admin", CancellationToken.None))).Succeeded);
            Assert.False(await scenario.Factory.WithDbAsync(db => db.AgentActions.AnyAsync(TestContext.Current.CancellationToken)));
        }
    }

    [Fact]
    public async Task FromServers_QueuesABatch_EndsTheAssignments_AndTracksTheProgress()
    {
        var (scenario, first, second, group) = await SetUpAsync();
        await using (scenario)
        {
            Assert.True((await scenario.WithServiceAsync<SoftwareAssignments, Outcome>(a => a.AddAsync(Package, group, null, null, false, "admin", CancellationToken.None))).Succeeded);

            var (queued, ended, outcome) = await scenario.WithServiceAsync<SoftwareUninstall, (int, int, Outcome)>(u => u.QueueAsync(Package, AgentActionKind.UninstallInWindow, UninstallScope.All, true, "admin", CancellationToken.None));

            Assert.True(outcome.Succeeded, outcome.Error);
            Assert.Equal((2, 1), (queued, ended));
            Assert.False(await scenario.Factory.WithDbAsync(db => db.PackageAssignments.AnyAsync(TestContext.Current.CancellationToken)));
            var actions = await scenario.Factory.WithDbAsync(db => db.AgentActions.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Single(actions.Select(a => a.BatchId).Distinct());
            Assert.NotNull(actions[0].BatchId);
            Assert.True(await scenario.Factory.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Action == "Package.UninstallQueued", TestContext.Current.CancellationToken)));

            var status = await scenario.WithServiceAsync<SoftwareUninstall, SoftwareUninstallStatus?>(u => u.StatusAsync(Package, CancellationToken.None));
            Assert.Equal((2, 0, 2), (status!.Total, status.Done, status.Waiting));

            // APP01 uninstalled it and its inventory no longer lists it; APP02 reported success, but its newer inventory still does.
            var now = DateTime.UtcNow;
            await scenario.Factory.WithDbAsync(async db =>
            {
                foreach (var action in await db.AgentActions.ToListAsync(TestContext.Current.CancellationToken))
                {
                    action.State = AgentActionState.Succeeded;
                    action.CompletedAtUtc = now.AddMinutes(-10);
                }

                await db.ClientSoftware.Where(s => s.ClientId == first.ClientId).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
                await db.Clients.Where(c => c.Id == second.ClientId).ExecuteUpdateAsync(s => s.SetProperty(c => c.InventoryAtUtc, now), TestContext.Current.CancellationToken);
                return await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            });

            status = await scenario.WithServiceAsync<SoftwareUninstall, SoftwareUninstallStatus?>(u => u.StatusAsync(Package, CancellationToken.None));
            Assert.Equal((1, 0, 1), (status!.Done, status.Waiting, status.StillPresent));
        }
    }

    [Fact]
    public async Task FromServers_InARing_OnlyThoseServers_AndNothingWhenNoneMatches()
    {
        var (scenario, first, _, _) = await SetUpAsync();
        await using (scenario)
        {
            var (queued, _, _) = await scenario.WithServiceAsync<SoftwareUninstall, (int, int, Outcome)>(u => u.QueueAsync(Package, AgentActionKind.UninstallInWindow, new UninstallScope(Rings: [RingEntity.PilotId]), false, "admin", CancellationToken.None));
            Assert.Equal(1, queued);
            Assert.Equal(first.ClientId, (await scenario.Factory.WithDbAsync(db => db.AgentActions.SingleAsync(TestContext.Current.CancellationToken))).ClientId);

            var (_, _, none) = await scenario.WithServiceAsync<SoftwareUninstall, (int, int, Outcome)>(u => u.QueueAsync("Not.InTheCatalog", AgentActionKind.UninstallInWindow, UninstallScope.All, false, "admin", CancellationToken.None));
            Assert.False(none.Succeeded);
        }
    }

    [Theory]
    [InlineData((int)AgentActionState.Queued, null, true, null, (int)SoftwareUninstallState.Waiting)]
    [InlineData((int)AgentActionState.Succeeded, 10, false, null, (int)SoftwareUninstallState.Done)]
    [InlineData((int)AgentActionState.Succeeded, 10, true, 5, (int)SoftwareUninstallState.AwaitingInventory)]
    [InlineData((int)AgentActionState.Succeeded, 10, true, 20, (int)SoftwareUninstallState.StillPresent)]
    [InlineData((int)AgentActionState.Failed, 10, true, 20, (int)SoftwareUninstallState.Failed)]
    [InlineData((int)AgentActionState.Expired, null, true, null, (int)SoftwareUninstallState.Failed)]
    public void StateOf_CombinesTheActionAndTheNextInventory(int state, int? completedMinute, bool listed, int? inventoryMinute, int expected)
    {
        var start = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal((SoftwareUninstallState)expected, SoftwareUninstall.StateOf((AgentActionState)state, completedMinute is { } c ? start.AddMinutes(c) : null, listed, inventoryMinute is { } i ? start.AddMinutes(i) : null));
    }

    [Theory]
    [InlineData("Microsoft.VCRedist.2015+.x64", true)]
    [InlineData("Microsoft.DotNet.DesktopRuntime.8", true)]
    [InlineData("EclipseAdoptium.Temurin.21.JRE", true)]
    [InlineData("Notepad++.Notepad++", false)]
    [InlineData("7zip.7zip", false)]
    public void Runtimes_AreRecognised(string packageId, bool runtime) =>
        Assert.Equal(runtime, SoftwareUninstall.IsRuntime(packageId));

    [Theory]
    [InlineData(true, null, 1)]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 1)]
    [InlineData(false, null, 0)]
    public async Task RemovingAnAssignment_TheDialogChoiceOverridesTheSavedOne(bool saved, bool? chosen, int expected)
    {
        var (scenario, _, _, group) = await SetUpAsync();
        await using (scenario)
        {
            Assert.True((await scenario.WithServiceAsync<SoftwareAssignments, Outcome>(a => a.AddAsync(Package, group, null, null, saved, "admin", CancellationToken.None))).Succeeded);
            var id = await scenario.Factory.WithDbAsync(db => db.PackageAssignments.Select(a => a.Id).SingleAsync(TestContext.Current.CancellationToken));

            var preview = await scenario.WithServiceAsync<SoftwareAssignments, SoftwareAssignments.RemovalPreview?>(a => a.PreviewRemovalAsync(id, CancellationToken.None));
            Assert.Equal(new SoftwareAssignments.RemovalPreview(1, 0), preview);

            Assert.True((await scenario.WithServiceAsync<SoftwareAssignments, Outcome>(a => a.RemoveAsync(id, "admin", CancellationToken.None, chosen))).Succeeded);
            Assert.Equal(expected, await scenario.Factory.WithDbAsync(db => db.AgentActions.CountAsync(TestContext.Current.CancellationToken)));
        }
    }

    [Fact]
    public async Task RemovingAnInstalledPackageAddedByHand_IsAllowed_AndCanUninstallIt()
    {
        var (scenario, _, _, _) = await SetUpAsync();
        await using (scenario)
        {
            await scenario.Factory.WithDbAsync(db => db.Packages.Where(p => p.PackageId == Package).ExecuteUpdateAsync(s => s.SetProperty(p => p.AddedBy, "admin"), TestContext.Current.CancellationToken));
            await scenario.Factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
            using var admin = new DashboardClient(scenario.Factory);
            using (await admin.LoginAsync("admin", Password))
            {
            }

            var page = await (await admin.GetAsync($"{PackagePage.Link(Package)}?dialog=remove")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("Also uninstall it from 2 servers", page, StringComparison.Ordinal);

            using (await admin.PostFormAsync($"{PackagePage.Link(Package)}?dialog=remove", new Dictionary<string, string> { ["_handler"] = "package-remove", ["RemoveConfirmed"] = "true", ["RemoveUninstall"] = "true" }))
            {
            }

            Assert.False(await scenario.Factory.WithDbAsync(db => db.Packages.AnyAsync(p => p.PackageId == Package, TestContext.Current.CancellationToken)));
            Assert.Equal(2, await scenario.Factory.WithDbAsync(db => db.AgentActions.CountAsync(a => a.PackageId == Package, TestContext.Current.CancellationToken)));
        }
    }

    [Fact]
    public async Task PackagePage_UninstallFromServers_NeedsConfirmationForMoreThanOneServer()
    {
        var (scenario, _, _, _) = await SetUpAsync();
        await using (scenario)
        {
            await scenario.Factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
            using var admin = new DashboardClient(scenario.Factory);
            using (await admin.LoginAsync("admin", Password))
            {
            }

            var dialog = await (await admin.GetAsync($"{PackagePage.Link(Package)}?dialog=uninstall")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("Uninstall from servers", dialog, StringComparison.Ordinal);
            Assert.Contains("In the next software window", dialog, StringComparison.Ordinal);
            Assert.Contains("I understand that the chosen servers will lose this program", dialog, StringComparison.Ordinal);

            var form = new Dictionary<string, string> { ["_handler"] = "package-uninstall", ["UninstallTarget"] = "all", ["UninstallWhen"] = "in-window" };
            using (var refused = await admin.PostFormAsync($"{PackagePage.Link(Package)}?dialog=uninstall", form))
            {
                Assert.Contains("Confirm that 2 servers will lose this program", await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
            }

            Assert.False(await scenario.Factory.WithDbAsync(db => db.AgentActions.AnyAsync(TestContext.Current.CancellationToken)));

            form["UninstallConfirmed"] = "true";
            using (await admin.PostFormAsync($"{PackagePage.Link(Package)}?dialog=uninstall", form))
            {
            }

            Assert.Equal(2, await scenario.Factory.WithDbAsync(db => db.AgentActions.CountAsync(TestContext.Current.CancellationToken)));
            var page = await (await admin.GetAsync(PackagePage.Link(Package))).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("0 of 2 servers done", page, StringComparison.Ordinal);
            Assert.Contains(">Uninstalling<", page, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ServerPage_OffersUninstall_ForWinGetPrograms_ToAdministratorsOnly()
    {
        var (scenario, first, _, _) = await SetUpAsync();
        await using (scenario)
        {
            await scenario.Factory.WithDbAsync(async db =>
            {
                db.ClientSoftware.Add(new ClientSoftwareEntity { ClientId = first.ClientId, View = SoftwareView.Machine64, KeyName = "Contoso", DisplayName = "Contoso Viewer", ReportedAtUtc = DateTime.UtcNow });
                return await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            });
            await scenario.Factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
            await scenario.Factory.CreateUserAsync("operator", Password, AdminRoles.Operator);
            using var admin = new DashboardClient(scenario.Factory);
            using (await admin.LoginAsync("admin", Password))
            {
            }

            var tab = await (await admin.GetAsync($"/servers/{first.ClientId}?tab=software")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("Not known to WinGet. Remove it on the server.", tab, StringComparison.Ordinal);
            Assert.Contains($"dialog=uninstall-software&amp;package={Uri.EscapeDataString(Package)}", tab, StringComparison.Ordinal);

            var dialog = await (await admin.GetAsync($"/servers/{first.ClientId}?tab=software&dialog=uninstall-software&package={Uri.EscapeDataString(Package)}")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("Uninstall program", dialog, StringComparison.Ordinal);

            using (await admin.PostFormAsync($"/servers/{first.ClientId}?tab=software&dialog=uninstall-software&package={Uri.EscapeDataString(Package)}", new Dictionary<string, string> { ["_handler"] = "uninstall-software", ["UninstallPackage"] = Package, ["UninstallSoftwareWhen"] = "now" }))
            {
            }

            var action = await scenario.Factory.WithDbAsync(db => db.AgentActions.SingleAsync(TestContext.Current.CancellationToken));
            Assert.Equal((AgentActionKind.Uninstall, Package), (action.Kind, action.PackageId));

            using var op = new DashboardClient(scenario.Factory);
            using (await op.LoginAsync("operator", Password))
            {
            }

            using (await op.PostFormAsync($"/servers/{first.ClientId}?tab=software&dialog=uninstall-software&package={Uri.EscapeDataString(Package)}", new Dictionary<string, string> { ["_handler"] = "uninstall-software", ["UninstallPackage"] = Package, ["UninstallSoftwareWhen"] = "now" }))
            {
            }

            Assert.Equal(1, await scenario.Factory.WithDbAsync(db => db.AgentActions.CountAsync(TestContext.Current.CancellationToken)));
        }
    }
}
