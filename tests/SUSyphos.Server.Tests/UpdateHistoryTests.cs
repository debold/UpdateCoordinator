using System.Net;

using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Agents;
using SUSyphos.Server.Data;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

public sealed class UpdateHistoryTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task History_IsStored_ShowsInstallationsOutsideSUSyphos_AndNullKeepsIt()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        var now = DateTimeOffset.UtcNow;

        using (var response = await InventoryTests.SendAsync(scenario.Http, client, InventoryTests.ValidInventory() with
        {
            UpdateHistory =
            [
                Entry("2026-10 Cumulative Update", now.AddHours(-2), UpdateHistoryEntry.AgentClientApplicationId),
                Entry("Broadcom driver update", now.AddHours(-3), "AutomaticUpdates"),
                Entry("Windows Malicious Software Removal Tool", now.AddHours(-4), null, InstallResultCode.Failed),
                Entry("Entry from the future", now.AddDays(3), "AutomaticUpdates"),
            ],
        }))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        var stored = await scenario.Factory.WithDbAsync(db => db.ClientUpdateHistory.Where(h => h.ClientId == client.ClientId).OrderByDescending(h => h.DateUtc).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["2026-10 Cumulative Update", "Broadcom driver update", "Windows Malicious Software Removal Tool"], stored.Select(h => h.Title));

        await scenario.Factory.CreateUserAsync("reader", Password, AdminRoles.Reader);
        using var reader = new DashboardClient(scenario.Factory);
        using (await reader.LoginAsync("reader", Password))
        {
        }

        using (var page = await reader.GetAsync($"/servers/{client.ClientId}"))
        {
            var html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("Windows Update history", html, StringComparison.Ordinal);
            Assert.Contains("Windows Automatic Updates (AutomaticUpdates)", html, StringComparison.Ordinal);
            Assert.Contains("outside SUSyphos", html, StringComparison.Ordinal);
            Assert.Contains("installations in the last 90 days were started by Windows or an administrator", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Entry from the future", html, StringComparison.Ordinal);
        }

        // An inventory without history (could not be read) keeps the stored one.
        using (var response = await InventoryTests.SendAsync(scenario.Http, client, InventoryTests.ValidInventory()))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        Assert.Equal(3, await scenario.Factory.WithDbAsync(db => db.ClientUpdateHistory.CountAsync(h => h.ClientId == client.ClientId, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task History_WithControlCharactersOrTooManyEntries_IsRejected()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);

        using (var response = await InventoryTests.SendAsync(scenario.Http, client, InventoryTests.ValidInventory() with { UpdateHistory = [Entry("bad\u0007title", DateTimeOffset.UtcNow, null)] }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using (var response = await InventoryTests.SendAsync(scenario.Http, client, InventoryTests.ValidInventory() with { UpdateHistory = [Entry("ok", DateTimeOffset.UtcNow, "x\u0001")] }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var tooMany = Enumerable.Range(0, UpdateHistoryEntry.MaxEntries + 1).Select(i => Entry($"Update {i}", DateTimeOffset.UtcNow, null)).ToList();
        using (var response = await InventoryTests.SendAsync(scenario.Http, client, InventoryTests.ValidInventory() with { UpdateHistory = tooMany }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.Equal(0, await scenario.Factory.WithDbAsync(db => db.ClientUpdateHistory.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("SUSyphos.Agent", "SUSyphos")]
    [InlineData("AutomaticUpdatesWuApp", "Windows Automatic Updates (AutomaticUpdatesWuApp)")]
    [InlineData("MoUpdateOrchestrator", "Windows Update orchestrator, e.g. Settings (MoUpdateOrchestrator)")]
    [InlineData("Windows Defender", "Microsoft Defender (Windows Defender)")]
    [InlineData("wusa", "Standalone installer (wusa)")]
    [InlineData("SomeTool", "SomeTool")]
    [InlineData(null, "unknown")]
    public void Source_IsReadable(string? clientApplicationId, string expected)
    {
        Assert.Equal(expected, UpdateHistoryText.Source(clientApplicationId));
        Assert.Equal(clientApplicationId == "SUSyphos.Agent", UpdateHistoryText.IsAgent(clientApplicationId));
    }

    private static UpdateHistoryEntry Entry(string title, DateTimeOffset date, string? source, InstallResultCode result = InstallResultCode.Succeeded) => new()
    {
        UpdateId = Guid.NewGuid(),
        Revision = 1,
        Title = title,
        Date = date,
        Operation = UpdateOperation.Installation,
        ResultCode = result,
        HResult = result == InstallResultCode.Failed ? unchecked((int)0x80070643) : 0,
        ClientApplicationId = source,
    };
}
