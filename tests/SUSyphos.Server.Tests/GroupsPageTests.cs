using System.Net;

using Microsoft.EntityFrameworkCore;

using SUSyphos.Server.Administration;
using SUSyphos.Server.Assignment;
using SUSyphos.Server.Components;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

public sealed class GroupsPageTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task NewGroup_IsAShortDialog_ThatOpensTheGroupPage_WithAllSettings()
    {
        // Marc, 2026-11-13: a page of its own per group instead of a long edit dialog.
        await using var factory = new ServerFactory();
        using var admin = await LoginAsync(factory);

        var create = await (await admin.GetAsync("/administration/groups?dialog=new")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("name=\"Name\"", create, StringComparison.Ordinal);
        Assert.Contains("name=\"Coordinated\"", create, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"Reboot\"", create, StringComparison.Ordinal);

        using (var created = await admin.PostFormAsync("/administration/groups?dialog=new", new Dictionary<string, string>
        {
            ["_handler"] = "group-dialog", ["Mode"] = "create", ["Name"] = "SQL cluster", ["Description"] = "Always On nodes", ["Coordinated"] = "true",
        }))
        {
            Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        }

        var group = await factory.WithDbAsync(db => db.Groups.SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Always On nodes", group.Description);
        Assert.True(group.Coordinated);
        var page = GroupPage.Link(group.Id);

        var html = await (await admin.GetAsync(page)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        foreach (var setting in new[] { "value=\"add\"", "name=\"Reboot\"", "name=\"Percent\"", "Members", "Agent settings" })
        {
            Assert.Contains(setting, html, StringComparison.Ordinal);
        }

        using (await admin.PostFormAsync(page, new Dictionary<string, string> { ["_handler"] = "group-windows", ["Windows.0.days"] = "6", ["Windows.0.from"] = "22:00", ["Windows.0.to"] = "04:00" }))
        {
        }

        using (await admin.PostFormAsync(page, new Dictionary<string, string> { ["_handler"] = "group-coordination", ["Coordinated"] = "true", ["Percent"] = "50", ["Max"] = "1" }))
        {
        }

        using (await admin.PostFormAsync(page, new Dictionary<string, string> { ["_handler"] = "group-settings", ["Reboot"] = "InsideMaintenanceWindow", ["Description"] = "Always On nodes" }))
        {
        }

        group = await factory.WithDbAsync(db => db.Groups.SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(50, group.LeasePercent);
        Assert.Equal(1, group.LeaseMaximum);
        Assert.Equal(SUSyphos.Contracts.Agents.RebootPolicy.InsideMaintenanceWindow, group.RebootPolicy);
        Assert.Equal(DayOfWeek.Saturday, MaintenanceWindowFormat.FromJson(group.MaintenanceWindowsJson)!.Single().Days.Single());
    }
    [Fact]
    public async Task InvalidInput_KeepsTheDialogOpen_AndDeleteRemovesTheGroup()
    {
        await using var factory = new ServerFactory();
        using var admin = await LoginAsync(factory);

        using (var invalid = await admin.PostFormAsync("/administration/groups?dialog=new", new Dictionary<string, string> { ["_handler"] = "group-dialog", ["Mode"] = "create", ["Name"] = "<script>", ["Percent"] = "25", ["Max"] = "5" }))
        {
            var html = await invalid.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("role=\"dialog\"", html, StringComparison.Ordinal);
            Assert.Contains("The group name must", html, StringComparison.Ordinal);
        }

        using (await admin.PostFormAsync("/administration/groups?dialog=new", new Dictionary<string, string> { ["_handler"] = "group-dialog", ["Mode"] = "create", ["Name"] = "Web", ["Percent"] = "25", ["Max"] = "5" }))
        {
        }

        var id = await factory.WithDbAsync(db => db.Groups.Select(g => g.Id).SingleAsync(TestContext.Current.CancellationToken));
        using (await admin.PostFormAsync($"/administration/groups?dialog=delete&id={id}", new Dictionary<string, string> { ["_handler"] = "group-dialog", ["Mode"] = "delete", ["Id"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture) }))
        {
        }

        Assert.False(await factory.WithDbAsync(db => db.Groups.AnyAsync(TestContext.Current.CancellationToken)));
    }

    private static async Task<DashboardClient> LoginAsync(ServerFactory factory)
    {
        await factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        var client = new DashboardClient(factory);
        using (await client.LoginAsync("admin", Password))
        {
        }

        return client;
    }
}
