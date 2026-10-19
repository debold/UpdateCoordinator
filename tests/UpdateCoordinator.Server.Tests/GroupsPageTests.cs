using Microsoft.EntityFrameworkCore;

using UpdateCoordinator.Server.Administration;
using UpdateCoordinator.Server.Assignment;
using UpdateCoordinator.TestSupport;

namespace UpdateCoordinator.Server.Tests;

public sealed class GroupsPageTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task NewAndEdit_ShareOneDialog_WithAllSettings()
    {
        await using var factory = new ServerFactory();
        using var admin = await LoginAsync(factory);

        var create = await (await admin.GetAsync("/administration/groups?dialog=new")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        foreach (var setting in new[] { "name=\"Name\"", "name=\"Description\"", "Windows.0.days", "name=\"Reboot\"", "name=\"Coordinated\"", "name=\"Percent\"" })
        {
            Assert.Contains(setting, create, StringComparison.Ordinal);
        }

        using (var created = await admin.PostFormAsync("/administration/groups?dialog=new", new Dictionary<string, string>
        {
            ["_handler"] = "group-dialog", ["Mode"] = "create", ["Name"] = "SQL cluster", ["Description"] = "Always On nodes",
            ["Windows.0.days"] = "6", ["Windows.0.from"] = "22:00", ["Windows.0.to"] = "04:00",
            ["Reboot"] = "InsideMaintenanceWindow", ["Coordinated"] = "true", ["Percent"] = "50", ["Max"] = "1",
        }))
        {
            Assert.Contains("The group was created.", await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        var group = await factory.WithDbAsync(db => db.Groups.SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Always On nodes", group.Description);
        Assert.True(group.Coordinated);
        Assert.Equal(1, group.LeaseMaximum);
        Assert.Equal(DayOfWeek.Saturday, MaintenanceWindowFormat.FromJson(group.MaintenanceWindowsJson)!.Single().Days.Single());

        var edit = await (await admin.GetAsync($"/administration/groups?dialog=edit&id={group.Id}")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Edit group SQL cluster", edit, StringComparison.Ordinal);
        Assert.Contains("Always On nodes", edit, StringComparison.Ordinal);
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
