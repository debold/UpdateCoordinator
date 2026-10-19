using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;

using UpdateCoordinator.Contracts.Agents;
using UpdateCoordinator.Server.Administration;
using UpdateCoordinator.Server.Assignment;
using UpdateCoordinator.Server.Components;
using UpdateCoordinator.Server.Data;
using UpdateCoordinator.TestSupport;

namespace UpdateCoordinator.Server.Tests;

public sealed class ServerDetailPageTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public void Picker_TurnsRowsIntoTheWindowFormat_AndIgnoresRowsWithoutDays()
    {
        var form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["Windows.0.days"] = new(["1", "2", "3", "4", "5"]),
            ["Windows.0.from"] = "19:00",
            ["Windows.0.to"] = "05:00",
            ["Windows.1.days"] = new(["6", "0"]),
            ["Windows.1.from"] = "00:00",
            ["Windows.1.to"] = "24:00",
            ["Windows.2.from"] = "10:00",
            ["Windows.2.to"] = "11:00",
        });

        var text = WindowPickerForm.Read(form, "Windows", out var inherit);

        Assert.False(inherit);
        Assert.True(MaintenanceWindowFormat.TryParse(text, out var windows, out var error), error);
        Assert.Equal(2, windows.Count);
        Assert.Equal([DayOfWeek.Saturday, DayOfWeek.Sunday], windows[1].Days);
        Assert.Equal("Mon–Fri 19:00–05:00; Sat, Sun 00:00–24:00", WindowPickerForm.Describe(windows));
    }

    [Theory]
    [InlineData("7", "19:00", "05:00")]
    [InlineData("-1", "19:00", "05:00")]
    [InlineData("1", "25:00", "05:00")]
    [InlineData("1", "19:00", "<script>")]
    [InlineData("1", "19:00", "")]
    public void Picker_InvalidValues_NeverBecomeAWindowSilently(string day, string from, string to)
    {
        var form = new FormCollection(new Dictionary<string, StringValues> { ["W.0.days"] = day, ["W.0.from"] = from, ["W.0.to"] = to });

        var text = WindowPickerForm.Read(form, "W", out _);

        Assert.False(text.Length > 0 && MaintenanceWindowFormat.TryParse(text, out _, out _));
    }

    [Fact]
    public void Picker_Inherit_WinsOverRows()
    {
        var form = new FormCollection(new Dictionary<string, StringValues> { ["W.inherit"] = "yes", ["W.0.days"] = "1", ["W.0.from"] = "19:00", ["W.0.to"] = "05:00" });

        Assert.Equal(string.Empty, WindowPickerForm.Read(form, "W", out var inherit));
        Assert.True(inherit);
    }

    [Fact]
    public void Coverage_RunsPastMidnight()
    {
        var cover = WindowPickerForm.Coverage([new MaintenanceWindow { Days = [DayOfWeek.Friday], Start = TimeSpan.FromHours(22), End = TimeSpan.FromHours(2) }]);

        Assert.True(cover[(int)DayOfWeek.Friday, 23]);
        Assert.True(cover[(int)DayOfWeek.Saturday, 1]);
        Assert.False(cover[(int)DayOfWeek.Saturday, 2]);
        Assert.False(cover[(int)DayOfWeek.Friday, 21]);
    }

    [Fact]
    public async Task Assignment_SavesPickedWindows_AndInheritClearsThem()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        await scenario.Factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        using var admin = new DashboardClient(scenario.Factory);
        using (await admin.LoginAsync("admin", Password))
        {
        }

        var path = $"/servers/{client.ClientId}";
        using (var saved = await admin.PostFormAsync(path, new Dictionary<string, string> { ["_handler"] = "assignment", ["Windows.0.days"] = "3", ["Windows.0.from"] = "22:00", ["Windows.0.to"] = "02:00" }))
        {
            Assert.Contains("The assignment was saved.", await saved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        var stored = MaintenanceWindowFormat.FromJson(await scenario.Factory.WithDbAsync(db => db.Clients.Select(c => c.MaintenanceWindowsJson).SingleAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(DayOfWeek.Wednesday, Assert.Single(stored!).Days.Single());

        using (var invalid = await admin.PostFormAsync(path, new Dictionary<string, string> { ["_handler"] = "assignment", ["Windows.0.days"] = "3", ["Windows.0.from"] = "22:00", ["Windows.0.to"] = "nonsense" }))
        {
            Assert.Contains("Invalid window", await invalid.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        using (await admin.PostFormAsync(path, new Dictionary<string, string> { ["_handler"] = "assignment", ["Windows.inherit"] = "yes", ["Windows.0.days"] = "3", ["Windows.0.from"] = "22:00", ["Windows.0.to"] = "02:00" }))
        {
        }

        Assert.Null(await scenario.Factory.WithDbAsync(db => db.Clients.Select(c => c.MaintenanceWindowsJson).SingleAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Band_ShowsActionsByRole_AndRestartNeedsTheDialog()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        await scenario.Factory.CreateUserAsync("operator", Password, AdminRoles.Operator);
        await scenario.Factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        var path = $"/servers/{client.ClientId}";

        using (var operatorBrowser = new DashboardClient(scenario.Factory))
        {
            using (await operatorBrowser.LoginAsync("operator", Password))
            {
            }

            var page = await (await operatorBrowser.GetAsync(path)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("Search for updates", page, StringComparison.Ordinal);
            Assert.DoesNotContain("Restart server", page, StringComparison.Ordinal);
            Assert.DoesNotContain("Delete with history", page, StringComparison.Ordinal);

            using (await operatorBrowser.PostFormAsync(path + "?dialog=restart", new Dictionary<string, string> { ["_handler"] = "restart" }, pageToRead: "/"))
            {
            }
        }

        Assert.False(await scenario.Factory.WithDbAsync(db => db.AgentActions.AnyAsync(a => a.Kind == AgentActionKind.RestartServer, TestContext.Current.CancellationToken)));

        using var admin = new DashboardClient(scenario.Factory);
        using (await admin.LoginAsync("admin", Password))
        {
        }

        var dialog = await (await admin.GetAsync(path + "?dialog=restart")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("role=\"dialog\"", dialog, StringComparison.Ordinal);
        using (await admin.PostFormAsync(path + "?dialog=restart", new Dictionary<string, string> { ["_handler"] = "restart" }))
        {
        }

        Assert.True(await scenario.Factory.WithDbAsync(db => db.AgentActions.AnyAsync(a => a.Kind == AgentActionKind.RestartServer, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task UnknownServer_ShowsNotFound()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        await scenario.Factory.CreateUserAsync("reader", Password, AdminRoles.Reader);
        using var reader = new DashboardClient(scenario.Factory);
        using (await reader.LoginAsync("reader", Password))
        {
        }

        var page = await (await reader.GetAsync($"/servers/{Guid.NewGuid()}?dialog=purge")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Server not found", page, StringComparison.Ordinal);
        Assert.DoesNotContain("role=\"dialog\"", page, StringComparison.Ordinal);
    }
}
