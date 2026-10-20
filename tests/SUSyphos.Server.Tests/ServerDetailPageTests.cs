using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Assignment;
using SUSyphos.Server.Components;
using SUSyphos.Server.Data;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

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
        Assert.Contains("Restart in the next maintenance window", dialog, StringComparison.Ordinal);

        // Without a choice nothing is queued.
        using (await admin.PostFormAsync(path + "?dialog=restart", new Dictionary<string, string> { ["_handler"] = "restart" }))
        {
        }

        Assert.False(await scenario.Factory.WithDbAsync(db => db.AgentActions.AnyAsync(TestContext.Current.CancellationToken)));

        using (var queued = await admin.PostFormAsync(path + "?dialog=restart", new Dictionary<string, string> { ["_handler"] = "restart", ["When"] = "now" }))
        {
            var html = await queued.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("Restart queued", html, StringComparison.Ordinal);
            Assert.Contains("restart now, after a five minute warning", html, StringComparison.Ordinal);
        }

        Assert.True(await scenario.Factory.WithDbAsync(db => db.AgentActions.AnyAsync(a => a.Kind == AgentActionKind.RestartServer, TestContext.Current.CancellationToken)));

        // A restart the agent has not picked up yet is cancelled on the central server.
        using (var cancelled = await admin.PostFormAsync(path, new Dictionary<string, string> { ["_handler"] = "cancel-restart" }))
        {
            Assert.DoesNotContain("Restart queued", await cancelled.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        Assert.False(await scenario.Factory.WithDbAsync(db => db.AgentActions.AnyAsync(a => a.State == AgentActionState.Queued, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task RestartInWindow_ShowsThePlannedTime_AndCancelGoesToTheAgent()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        await scenario.Factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        using var admin = new DashboardClient(scenario.Factory);
        using (await admin.LoginAsync("admin", Password))
        {
        }

        var path = $"/servers/{client.ClientId}";
        using (await admin.PostFormAsync(path + "?dialog=restart", new Dictionary<string, string> { ["_handler"] = "restart", ["When"] = "window" }))
        {
        }

        Assert.True(await scenario.Factory.WithDbAsync(db => db.AgentActions.AnyAsync(a => a.Kind == AgentActionKind.RestartServerInWindow, TestContext.Current.CancellationToken)));

        // The agent took it and reports that it holds the restart with a planned time.
        await scenario.Factory.WithDbAsync(db => db.AgentActions.ExecuteUpdateAsync(s => s.SetProperty(a => a.State, AgentActionState.Succeeded), TestContext.Current.CancellationToken));
        var planned = DateTime.UtcNow.AddDays(1);
        using (var request = new HttpRequestMessage(HttpMethod.Post, new Uri(SUSyphos.Contracts.ApiRoutes.AgentHeartbeat, UriKind.Relative)) { Content = System.Net.Http.Json.JsonContent.Create(new HeartbeatRequest { AgentVersion = "0.7.0", RestartRequested = true, PlannedRestartAt = planned, RebootWait = RebootWait.OutsideWindow }) })
        {
            request.Headers.Authorization = AgentCredentials.ToHeader(client.ClientId, client.ClientSecret);
            using var response = await scenario.Http.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        }

        var page = await (await admin.GetAsync(path)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Restart queued", page, StringComparison.Ordinal);
        Assert.Contains($"The agent plans the restart for {DisplayTime.Format(planned)}", page, StringComparison.Ordinal);
        Assert.Contains("waits for the next maintenance window", page, StringComparison.Ordinal);

        using (await admin.PostFormAsync(path, new Dictionary<string, string> { ["_handler"] = "cancel-restart" }))
        {
        }

        Assert.True(await scenario.Factory.WithDbAsync(db => db.AgentActions.AnyAsync(a => a.Kind == AgentActionKind.CancelRestart && a.State == AgentActionState.Queued, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Preview_ListsReleasedUpdatesForTheNextWindow()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var update = RolloutScenario.Update("Security Updates");
        await scenario.ReportAsync(client, update);
        await scenario.Factory.CreateUserAsync("reader", Password, AdminRoles.Reader);
        using var reader = new DashboardClient(scenario.Factory);
        using (await reader.LoginAsync("reader", Password))
        {
        }

        // Before the server reports its time zone, the window cannot be calculated.
        var before = await (await reader.GetAsync($"/servers/{client.ClientId}")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("the server has not reported its time zone yet", before, StringComparison.Ordinal);

        await scenario.Factory.WithDbAsync(db => db.Clients.ExecuteUpdateAsync(s => s.SetProperty(c => c.TimeZoneId, "UTC"), TestContext.Current.CancellationToken));
        var page = await (await reader.GetAsync($"/servers/{client.ClientId}")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var planned = page[page.IndexOf("Planned for the next maintenance window", StringComparison.Ordinal)..page.IndexOf("Missing updates", StringComparison.Ordinal)];

        Assert.Contains($"/updates/{update.UpdateId}/1", planned, StringComparison.Ordinal);
        Assert.Contains("Released", planned, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "pill fail\">problem")]
    [InlineData(false, "pill ok\">OK")]
    public async Task AutomaticUpdates_ShowClearlyWhetherThereIsAProblem(bool bypass, string expected)
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        await scenario.Factory.WithDbAsync(db => db.Clients.ExecuteUpdateAsync(s => s
            .SetProperty(c => c.AutoUpdateMode, AutomaticUpdateMode.NotConfigured)
            .SetProperty(c => c.AutomaticUpdatesBypass, bypass), TestContext.Current.CancellationToken));
        await scenario.Factory.CreateUserAsync("reader", Password, AdminRoles.Reader);
        using var reader = new DashboardClient(scenario.Factory);
        using (await reader.LoginAsync("reader", Password))
        {
        }

        var page = await (await reader.GetAsync($"/servers/{client.ClientId}")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains(expected, page, StringComparison.Ordinal);
        Assert.DoesNotContain("bypasses rings", page, StringComparison.Ordinal);
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

public sealed class WindowPickerEditTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public void Buttons_AddRemoveAndPreview_WithoutSaving()
    {
        var form = new Microsoft.AspNetCore.Http.FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["W.0.days"] = new(["1", "2"]), ["W.0.from"] = "19:00", ["W.0.to"] = "05:00",
            ["W.1.days"] = "6", ["W.1.from"] = "00:00", ["W.1.to"] = "24:00",
            ["W.action"] = "add",
        });

        var added = WindowPickerForm.ReadEdit(form, "W");

        Assert.NotNull(added);
        Assert.Equal(3, added.Drafts.Count);
        Assert.Empty(added.Drafts[2].Days);
        Assert.Equal(TimeSpan.FromDays(1), added.Drafts[1].End);

        form = new Microsoft.AspNetCore.Http.FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(form.ToDictionary(f => f.Key, f => f.Value)) { ["W.action"] = "remove:0" });
        var removed = WindowPickerForm.ReadEdit(form, "W")!;
        Assert.Equal([DayOfWeek.Saturday], Assert.Single(removed.Drafts).Days);
        Assert.Single(WindowPickerForm.ToWindows(removed.Drafts));
    }

    [Theory]
    [InlineData("remove:7")]
    [InlineData("remove:x")]
    [InlineData("preview")]
    [InlineData("nonsense")]
    public void UnknownOrOutOfRangeActions_ChangeNoRow(string action)
    {
        var form = new Microsoft.AspNetCore.Http.FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["W.0.days"] = "1", ["W.0.from"] = "<script>", ["W.0.to"] = "05:00", ["W.action"] = action,
        });

        var state = WindowPickerForm.ReadEdit(form, "W")!;

        Assert.Single(state.Drafts);
        Assert.Equal(WindowPickerForm.DefaultStart, state.Drafts[0].Start);
    }

    [Fact]
    public void SaveSubmit_IsNoPickerAction()
    {
        var form = new Microsoft.AspNetCore.Http.FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { ["W.0.days"] = "1" });

        Assert.Null(WindowPickerForm.ReadEdit(form, "W"));
    }

    [Fact]
    public async Task MaintenancePage_AddsARowWithoutSaving_AndSavesAfterRemoving()
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        using var admin = new DashboardClient(factory);
        using (await admin.LoginAsync("admin", Password))
        {
        }

        var form = new Dictionary<string, string>
        {
            ["_handler"] = "maintenance", ["Windows.0.days"] = "1", ["Windows.0.from"] = "19:00", ["Windows.0.to"] = "05:00",
            ["Windows.1.days"] = "6", ["Windows.1.from"] = "00:00", ["Windows.1.to"] = "24:00", ["Windows.action"] = "add",
        };
        using (var added = await admin.PostFormAsync("/administration/maintenance", form))
        {
            var html = await added.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Equal(3, System.Text.RegularExpressions.Regex.Count(html, "class=\"window-row\""));
            Assert.Contains("Not saved yet.", html, StringComparison.Ordinal);
        }

        Assert.Null(await factory.WithDbAsync(db => db.Settings.Where(s => s.Key == SettingEntity.SystemMaintenanceWindows).Select(s => s.Value).SingleOrDefaultAsync(TestContext.Current.CancellationToken)));

        form.Remove("Windows.action");
        form.Remove("Windows.1.days");
        form.Remove("Windows.1.from");
        form.Remove("Windows.1.to");
        using (await admin.PostFormAsync("/administration/maintenance", form))
        {
        }

        var saved = MaintenanceWindowFormat.FromJson(await factory.WithDbAsync(db => db.Settings.Where(s => s.Key == SettingEntity.SystemMaintenanceWindows).Select(s => s.Value).SingleAsync(TestContext.Current.CancellationToken)));
        Assert.Equal([DayOfWeek.Monday], Assert.Single(saved!).Days);
    }
}
