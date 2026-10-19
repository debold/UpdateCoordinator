using Microsoft.Extensions.DependencyInjection;

using UpdateCoordinator.Server.Assignment;
using UpdateCoordinator.Server.Data;
using UpdateCoordinator.TestSupport;

namespace UpdateCoordinator.Server.Tests;

public sealed class RuleRoleTests
{
    [Theory]
    [InlineData("DomainController,SqlServer", new[] { "SqlServer" }, true)]
    [InlineData("DomainController,SqlServer", new[] { "domaincontroller" }, true)]
    [InlineData("DomainController,SqlServer", new[] { "Exchange" }, false)]
    [InlineData("DomainController", new string[0], false)]
    public void RuleWithSeveralRoles_MatchesAnyOfThem(string roles, string[] serverRoles, bool matches)
    {
        var rule = new AssignmentRuleEntity { Role = roles };
        var client = new ClientEntity { SecretHash = [1], HostName = "SRV01", OsVersion = "10.0.20348.1", AgentVersion = "0.2.0", Roles = [.. serverRoles] };

        Assert.Equal(matches, AssignmentService.Matches(rule, client));
    }

    [Theory]
    [InlineData("A1,A2,A3,A4,A5,A6,A7,A8,A9,A10,A11")]
    [InlineData("SqlServer,<script>")]
    [InlineData("Domain Controller")]
    public async Task InvalidRoleLists_AreRefused(string roles)
    {
        await using var factory = new ServerFactory();
        factory.StartServer();
        await using var scope = factory.Services.CreateAsyncScope();

        var outcome = await scope.ServiceProvider.GetRequiredService<AssignmentAdministration>()
            .CreateRuleAsync(10, AssignmentTarget.Ring, RingEntity.PilotId, roles, null, null, "admin", CancellationToken.None);

        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public async Task UnknownRule_CannotBeEdited()
    {
        await using var factory = new ServerFactory();
        factory.StartServer();
        await using var scope = factory.Services.CreateAsyncScope();

        var outcome = await scope.ServiceProvider.GetRequiredService<AssignmentAdministration>()
            .UpdateRuleAsync(999, 10, AssignmentTarget.Ring, RingEntity.PilotId, "SqlServer", null, null, "admin", CancellationToken.None);

        Assert.Equal("The rule no longer exists.", outcome.Error);
    }
}
