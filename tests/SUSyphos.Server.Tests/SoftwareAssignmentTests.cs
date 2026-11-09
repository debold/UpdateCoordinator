using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;
using SUSyphos.Server.Software;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

/// <summary>Assignments of packages to groups and servers for new installations.</summary>
public sealed class SoftwareAssignmentTests
{
    private const string Password = "correct horse battery";

    private static async Task<(RolloutScenario Scenario, RegisterResponse Server, int Group)> SetUpAsync()
    {
        var scenario = await RolloutScenario.CreateAsync();
        var server = await scenario.AddClientAsync("APP01", RingEntity.PilotId);
        var group = await scenario.Factory.WithDbAsync(async db =>
        {
            DemoSoftware.Add(db, [], [], DateTime.UtcNow);
            var entity = new GroupEntity { Name = "Web farm" };
            db.Groups.Add(entity);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            db.ClientGroups.Add(new ClientGroupEntity { ClientId = server.ClientId, GroupId = entity.Id, Manual = true });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return entity.Id;
        });
        return (scenario, server, group);
    }

    private static Task<(IReadOnlyList<SoftwareNeed> Needed, IReadOnlyList<SoftwareNeed> Installed)> NeedsAsync(RolloutScenario scenario, Guid client) =>
        scenario.WithServiceAsync<ServerDbContext, (IReadOnlyList<SoftwareNeed>, IReadOnlyList<SoftwareNeed>)>(db => SoftwareNeeds.LoadAsync(db, [client], CancellationToken.None));

    private static Task<Outcome> AssignAsync(RolloutScenario scenario, int? group, Guid? server, string? version = null) =>
        scenario.WithServiceAsync<SoftwareAssignments, Outcome>(a => a.AddAsync("Notepad++.Notepad++", group, server, version, false, "admin", CancellationToken.None));

    [Fact]
    public async Task GroupAssignment_MembersWithoutThePackageNeedTheNewestVersion()
    {
        var (scenario, server, group) = await SetUpAsync();
        await using (scenario)
        {
            var outcome = await AssignAsync(scenario, group, null);

            Assert.True(outcome.Succeeded, outcome.Error);
            Assert.Equal([SoftwarePackages.SoftwareUpdateId("Notepad++.Notepad++", "8.9.9")], (await NeedsAsync(scenario, server.ClientId)).Needed.Select(n => n.UpdateId));
            Assert.True(await scenario.Factory.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "Package.Assigned", TestContext.Current.CancellationToken)));
        }
    }

    [Fact]
    public async Task ServerAssignment_WithAFixedVersion_NeedsThatVersion_AndRemovingEndsIt()
    {
        var (scenario, server, _) = await SetUpAsync();
        await using (scenario)
        {
            Assert.True((await AssignAsync(scenario, null, server.ClientId, "8.9.8")).Succeeded);
            Assert.Equal([SoftwarePackages.SoftwareUpdateId("Notepad++.Notepad++", "8.9.8")], (await NeedsAsync(scenario, server.ClientId)).Needed.Select(n => n.UpdateId));

            var id = await scenario.Factory.WithDbAsync(db => db.PackageAssignments.Select(a => a.Id).SingleAsync(TestContext.Current.CancellationToken));
            Assert.True((await scenario.WithServiceAsync<SoftwareAssignments, Outcome>(a => a.RemoveAsync(id, "admin", CancellationToken.None))).Succeeded);

            Assert.Empty((await NeedsAsync(scenario, server.ClientId)).Needed);
        }
    }

    [Fact]
    public async Task InstalledPackage_IsNotInstalledAgain_AndUnsupportedServersAreLeftOut()
    {
        var (scenario, server, group) = await SetUpAsync();
        await using (scenario)
        {
            var other = await scenario.AddClientAsync("CORE01", RingEntity.PilotId);
            await scenario.Factory.WithDbAsync(async db =>
            {
                db.ClientSoftware.Add(new ClientSoftwareEntity { ClientId = server.ClientId, View = SoftwareView.Machine64, KeyName = "Notepad++", DisplayName = "Notepad++", PackageId = "Notepad++.Notepad++", InstalledVersion = "8.9.9", MatchKind = SoftwareMatch.WinGet, ReportedAtUtc = DateTime.UtcNow });
                db.ClientGroups.Add(new ClientGroupEntity { ClientId = other.ClientId, GroupId = group, Manual = true });
                (await db.Clients.SingleAsync(c => c.Id == other.ClientId, TestContext.Current.CancellationToken)).SoftwareState = SoftwareSupportState.NotSupported;
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
                return true;
            });

            Assert.True((await AssignAsync(scenario, group, null)).Succeeded);

            Assert.Empty((await NeedsAsync(scenario, server.ClientId)).Needed);
            Assert.Empty((await NeedsAsync(scenario, other.ClientId)).Needed);
        }
    }

    public static readonly TheoryData<string, string> Invalid = new()
    {
        { "both", "one group or one server" },
        { "none", "one group or one server" },
        { "version", "not known" },
        { "group", "does not exist" },
        { "package", "not in the catalog" },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public async Task InvalidAssignments_AreRefused(string kind, string error)
    {
        var (scenario, server, group) = await SetUpAsync();
        await using (scenario)
        {
            var outcome = await scenario.WithServiceAsync<SoftwareAssignments, Outcome>(a => kind switch
            {
                "both" => a.AddAsync("Notepad++.Notepad++", group, server.ClientId, null, false, "admin", CancellationToken.None),
                "none" => a.AddAsync("Notepad++.Notepad++", null, null, null, false, "admin", CancellationToken.None),
                "version" => a.AddAsync("Notepad++.Notepad++", group, null, "1.0'; DROP TABLE Packages;--", false, "admin", CancellationToken.None),
                "group" => a.AddAsync("Notepad++.Notepad++", 9999, null, null, false, "admin", CancellationToken.None),
                _ => a.AddAsync("Unknown.Package", group, null, null, false, "admin", CancellationToken.None),
            });

            Assert.False(outcome.Succeeded);
            Assert.Contains(error, outcome.Error, StringComparison.Ordinal);
            Assert.False(await scenario.Factory.WithDbAsync(db => db.PackageAssignments.AnyAsync(TestContext.Current.CancellationToken)));
        }
    }

    [Fact]
    public async Task SameTargetTwice_IsRefused_AndSwitchedOffRefusesEverything()
    {
        var (scenario, _, group) = await SetUpAsync();
        await using (scenario)
        {
            Assert.True((await AssignAsync(scenario, group, null)).Succeeded);
            Assert.Contains("already assigned", (await AssignAsync(scenario, group, null)).Error, StringComparison.Ordinal);

            await scenario.WithServiceAsync<SoftwareFeature, bool>(async f => { await f.SetAsync(false, "admin", CancellationToken.None); return true; });
            Assert.Contains("switched off", (await AssignAsync(scenario, null, null)).Error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task PackagePage_AdminAssignsAndUnassigns_OperatorCannot()
    {
        var (scenario, _, group) = await SetUpAsync();
        await using (scenario)
        {
            await scenario.Factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
            await scenario.Factory.CreateUserAsync("operator", Password, AdminRoles.Operator);
            using var admin = new DashboardClient(scenario.Factory);
            using (await admin.LoginAsync("admin", Password))
            {
            }

            const string Page = "/software/Notepad%2B%2B.Notepad%2B%2B";
            using var assign = await admin.PostFormAsync(Page + "?dialog=assign", new Dictionary<string, string> { ["_handler"] = "package-assign", ["Target"] = $"g:{group}", ["Version"] = string.Empty });
            Assert.Contains("The package was assigned", await assign.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
            var id = await scenario.Factory.WithDbAsync(db => db.PackageAssignments.Select(a => a.Id).SingleAsync(TestContext.Current.CancellationToken));

            using var op = new DashboardClient(scenario.Factory);
            using (await op.LoginAsync("operator", Password))
            {
            }

            using var forged = await op.PostFormAsync($"{Page}?dialog=unassign&id={id}", new Dictionary<string, string> { ["_handler"] = "package-unassign", ["AssignmentId"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            Assert.True(await scenario.Factory.WithDbAsync(db => db.PackageAssignments.AnyAsync(TestContext.Current.CancellationToken)));

            using var unassign = await admin.PostFormAsync($"{Page}?dialog=unassign&id={id}", new Dictionary<string, string> { ["_handler"] = "package-unassign", ["AssignmentId"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            Assert.Contains("The assignment was removed", await unassign.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
            Assert.False(await scenario.Factory.WithDbAsync(db => db.PackageAssignments.AnyAsync(TestContext.Current.CancellationToken)));
        }
    }

    [Fact]
    public async Task RemovingWithUninstall_QueuesTheUninstallWhereInstalled_UnlessStillAssigned()
    {
        var (scenario, server, group) = await SetUpAsync();
        await using (scenario)
        {
            var second = await scenario.AddClientAsync("APP02", RingEntity.PilotId);
            var third = await scenario.AddClientAsync("APP03", RingEntity.PilotId);
            await scenario.Factory.WithDbAsync(async db =>
            {
                foreach (var client in new[] { server.ClientId, second.ClientId })
                {
                    db.ClientSoftware.Add(new ClientSoftwareEntity { ClientId = client, View = SoftwareView.Machine64, KeyName = "Notepad++", DisplayName = "Notepad++", PackageId = "Notepad++.Notepad++", InstalledVersion = "8.9.9", MatchKind = SoftwareMatch.WinGet, ReportedAtUtc = DateTime.UtcNow });
                    db.ClientGroups.Add(new ClientGroupEntity { ClientId = client == server.ClientId ? third.ClientId : client, GroupId = group, Manual = true });
                }

                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
                return true;
            });

            // APP01 and APP02 have it installed; APP02 stays assigned directly; APP03 is in the group but has nothing installed.
            Assert.True((await scenario.WithServiceAsync<SoftwareAssignments, Outcome>(a => a.AddAsync("Notepad++.Notepad++", group, null, null, true, "admin", CancellationToken.None))).Succeeded);
            Assert.True((await AssignAsync(scenario, null, second.ClientId)).Succeeded);
            var id = await scenario.Factory.WithDbAsync(db => db.PackageAssignments.Where(a => a.GroupId == group).Select(a => a.Id).SingleAsync(TestContext.Current.CancellationToken));

            Assert.True((await scenario.WithServiceAsync<SoftwareAssignments, Outcome>(a => a.RemoveAsync(id, "admin", CancellationToken.None))).Succeeded);

            var action = Assert.Single(await scenario.Factory.WithDbAsync(db => db.AgentActions.ToListAsync(TestContext.Current.CancellationToken)));
            Assert.Equal((server.ClientId, AgentActionKind.UninstallInWindow, "Notepad++.Notepad++"), (action.ClientId, action.Kind, action.PackageId));

            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(SUSyphos.Contracts.ApiRoutes.AgentActions, UriKind.Relative));
            request.Headers.Authorization = AgentCredentials.ToHeader(server.ClientId, server.ClientSecret);
            using var response = await scenario.Http.SendAsync(request, TestContext.Current.CancellationToken);
            var delivered = await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<AgentActionList>(response.Content, TestContext.Current.CancellationToken);
            Assert.Equal("Notepad++.Notepad++", Assert.Single(delivered!.Actions).PackageId);
        }
    }

    [Fact]
    public async Task RemovingWithoutUninstall_QueuesNothing()
    {
        var (scenario, server, group) = await SetUpAsync();
        await using (scenario)
        {
            await scenario.Factory.WithDbAsync(async db =>
            {
                db.ClientSoftware.Add(new ClientSoftwareEntity { ClientId = server.ClientId, View = SoftwareView.Machine64, KeyName = "Notepad++", DisplayName = "Notepad++", PackageId = "Notepad++.Notepad++", InstalledVersion = "8.9.9", MatchKind = SoftwareMatch.WinGet, ReportedAtUtc = DateTime.UtcNow });
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
                return true;
            });
            Assert.True((await AssignAsync(scenario, group, null)).Succeeded);
            var id = await scenario.Factory.WithDbAsync(db => db.PackageAssignments.Select(a => a.Id).SingleAsync(TestContext.Current.CancellationToken));

            Assert.True((await scenario.WithServiceAsync<SoftwareAssignments, Outcome>(a => a.RemoveAsync(id, "admin", CancellationToken.None))).Succeeded);

            Assert.False(await scenario.Factory.WithDbAsync(db => db.AgentActions.AnyAsync(TestContext.Current.CancellationToken)));
        }
    }}
