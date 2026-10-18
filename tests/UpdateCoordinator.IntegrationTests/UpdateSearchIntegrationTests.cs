using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using UpdateCoordinator.Agent.Communication;
using UpdateCoordinator.Agent.Inventory;
using UpdateCoordinator.Agent.Updates;
using UpdateCoordinator.Contracts.Agents;
using UpdateCoordinator.TestSupport;

namespace UpdateCoordinator.IntegrationTests;

/// <summary>
/// Update run with the real Windows sources of this machine against the central server.
/// The online search needs internet access; without it the test is skipped.
/// </summary>
public sealed class UpdateSearchIntegrationTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    [Fact]
    public async Task RealUpdateRun_IsAcceptedByCentralServer()
    {
        var client = new CentralServerClient(factory.CreateClient());
        var identity = await client.RegisterAsync(
            new RegisterRequest { HostName = "SRV-SEARCH", OsVersion = "10.0.20348.1", AgentVersion = "1.0.0" },
            TestContext.Current.CancellationToken);
        await factory.WithDbAsync(db => db.Clients.Where(c => c.Id == identity.ClientId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.State, RegistrationState.Approved), TestContext.Current.CancellationToken));

        var options = Options.Create(new UpdateOptions());
        var reporter = new UpdateReporter(
            new PreFlightCheck(new WindowsSystemProbe(), new DiskSpace(), options),
            new WuaAvailableUpdateSearch(options),
            client,
            options,
            TimeProvider.System,
            NullLogger<UpdateReporter>.Instance);

        var report = await reporter.CreateReportAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        Assert.SkipWhen(
            report.SearchHResult is not null,
            $"Online search not possible on this machine (0x{report.SearchHResult:X8}).");

        Assert.True(await client.SendUpdateReportAsync(identity, report, TestContext.Current.CancellationToken));
        var stored = await factory.WithDbAsync(db => db.Clients.SingleAsync(c => c.Id == identity.ClientId, TestContext.Current.CancellationToken));
        Assert.Equal(report.SearchSucceeded, stored.LastSearchSucceeded);
        Assert.Equal(report.HealthChecks.Count, stored.HealthChecks.Count);
    }
}
