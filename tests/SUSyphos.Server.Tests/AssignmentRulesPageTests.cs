using Microsoft.EntityFrameworkCore;

using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

/// <summary>The rule dialog warns before a server lands in a second coordinated group (Marc, 2026-11-13).</summary>
public sealed class AssignmentRulesPageTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task RuleForACoordinatedGroup_WarnsAboutServersInAnotherCoordinatedGroup()
    {
        await using var factory = new ServerFactory();
        using var http = factory.CreateClient();
        var credentials = await RegisterTests.RegisterAsync(http);
        var second = 0;
        await factory.WithDbAsync(async db =>
        {
            var client = await db.Clients.SingleAsync(c => c.Id == credentials.ClientId, TestContext.Current.CancellationToken);
            client.State = SUSyphos.Contracts.Agents.RegistrationState.Approved;
            client.HostName = "LABVEX01";
            client.Roles = ["Exchange"];
            var first = new GroupEntity { Name = "MyServers", Coordinated = true };
            var other = new GroupEntity { Name = "Exchange Servers", Coordinated = true };
            db.Groups.AddRange(first, other);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            db.ClientGroups.Add(new ClientGroupEntity { ClientId = client.Id, GroupId = first.Id });
            second = other.Id;
            return await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });
        await factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        using var browser = new DashboardClient(factory);
        using (await browser.LoginAsync("admin", Password))
        {
        }

        using var preview = await browser.PostFormAsync("/administration/rules?dialog=new", new Dictionary<string, string>
        {
            ["_handler"] = "rule-dialog",
            ["Mode"] = "create",
            ["Target"] = $"group:{second}",
            ["Priority"] = "100",
            ["Roles"] = "Exchange",
            ["Preview"] = "yes",
        });
        var html = await preview.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("1 server would be in more than one coordinated group (LABVEX01: MyServers)", html, StringComparison.Ordinal);
    }
}
