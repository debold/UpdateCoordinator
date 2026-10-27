using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Data;
using SUSyphos.Server.Rollouts;

namespace SUSyphos.Server.Tests;

/// <summary>A failed installation counts only while the server still misses the update (Marc, 2026-10-23).</summary>
public sealed class DeploymentStateTests
{
    [Fact]
    public async Task FailedInstallation_StaysFailed_WhileTheServerStillMissesTheUpdate()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var update = RolloutScenario.Update("Security Updates") with { UpdateId = Guid.NewGuid() };
        await scenario.ReportAsync(pilot, update);
        await scenario.InstallAsync(pilot, update.UpdateId, InstallResultCode.Failed);

        Assert.Equal(DeploymentState.Failed, await StateAsync(scenario, pilot.ClientId, update.UpdateId));

        // The next search still finds it: still failed.
        await scenario.ReportAsync(pilot, update);
        Assert.Equal(DeploymentState.Failed, await StateAsync(scenario, pilot.ClientId, update.UpdateId));
    }

    [Fact]
    public async Task FailedInstallation_IsOver_OnceTheSearchNoLongerReportsTheUpdate()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var update = RolloutScenario.Update("Drivers") with { UpdateId = Guid.NewGuid() };
        await scenario.ReportAsync(pilot, update);
        await scenario.InstallAsync(pilot, update.UpdateId, InstallResultCode.Failed);

        await scenario.ReportAsync(pilot);

        Assert.Null(await StateAsync(scenario, pilot.ClientId, update.UpdateId));
    }

    [Fact]
    public async Task FailedInstallation_ThenInstalledByWindows_CountsAsInstalled()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var update = RolloutScenario.Update("Drivers") with { UpdateId = Guid.NewGuid() };
        await scenario.ReportAsync(pilot, update);
        await scenario.InstallAsync(pilot, update.UpdateId, InstallResultCode.Failed);
        await scenario.Factory.WithDbAsync(async db =>
        {
            db.ClientUpdateHistory.Add(new ClientUpdateHistoryEntity { ClientId = pilot.ClientId, UpdateId = update.UpdateId, Revision = 1, Title = "Driver", DateUtc = DateTime.UtcNow, Operation = UpdateOperation.Installation, ResultCode = InstallResultCode.Succeeded });
            return await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

        await scenario.ReportAsync(pilot);

        Assert.Equal(DeploymentState.Successful, await StateAsync(scenario, pilot.ClientId, update.UpdateId));
    }

    [Fact]
    public async Task SuccessfulInstallation_ReportedMissingAgain_CountsAsNotInstalled()
    {
        // Uninstalled after the installation (Marc, 2026-10-27): the next search reports the update again.
        await using var scenario = await RolloutScenario.CreateAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var update = RolloutScenario.Update("Security Updates") with { UpdateId = Guid.NewGuid() };
        await scenario.ReportAsync(pilot, update);
        var before = await StateAsync(scenario, pilot.ClientId, update.UpdateId);
        await scenario.InstallAsync(pilot, update.UpdateId, InstallResultCode.Succeeded);
        await scenario.ReportAsync(pilot);
        Assert.Equal(DeploymentState.Successful, await StateAsync(scenario, pilot.ClientId, update.UpdateId));

        await scenario.ReportAsync(pilot, update);

        Assert.Equal(before, await StateAsync(scenario, pilot.ClientId, update.UpdateId));
        Assert.Contains(update.UpdateId, (await scenario.ReleasesAsync(pilot)).Releases.Select(r => r.UpdateId));
    }

    [Fact]
    public async Task SuccessfulInstallation_WaitingForItsRestart_StaysSuccessful()
    {
        // Until the restart that completes it, a search may still report the update: that is no uninstallation.
        await using var scenario = await RolloutScenario.CreateAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var update = RolloutScenario.Update("Security Updates") with { UpdateId = Guid.NewGuid() };
        await scenario.ReportAsync(pilot, update);
        await scenario.InstallAsync(pilot, update.UpdateId, InstallResultCode.Succeeded);
        await scenario.Factory.WithDbAsync(db => db.Installations.ExecuteUpdateAsync(s => s.SetProperty(i => i.RebootRequired, true), TestContext.Current.CancellationToken));

        await scenario.ReportAsync(pilot, update);

        Assert.Equal(DeploymentState.Successful, await StateAsync(scenario, pilot.ClientId, update.UpdateId));
    }

    [Fact]
    public void AlreadyInstalled_LeavesTheUpdateInstalled_OtherFailuresDoNot()
    {
        Assert.True(InstallEvidence.LeavesInstalled((int)InstallResultCode.Failed, unchecked((int)0x80070103)));
        Assert.True(InstallEvidence.LeavesInstalled((int)InstallResultCode.SucceededWithErrors, 0));
        Assert.False(InstallEvidence.LeavesInstalled((int)InstallResultCode.Failed, unchecked((int)0x800F081F)));
        Assert.False(InstallEvidence.LeavesInstalled((int)InstallResultCode.Aborted, 0));
    }

    private static async Task<DeploymentState?> StateAsync(RolloutScenario scenario, Guid clientId, Guid updateId)
    {
        await using var scope = scenario.Factory.Services.CreateAsyncScope();
        var entries = await scope.ServiceProvider.GetRequiredService<DeploymentStatus>().GetAsync(null, clientId, [updateId], TestContext.Current.CancellationToken);
        return entries.SingleOrDefault()?.State;
    }
}
