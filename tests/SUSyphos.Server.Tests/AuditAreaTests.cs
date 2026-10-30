using SUSyphos.Server.Administration;

namespace SUSyphos.Server.Tests;

/// <summary>The areas the views of the audit log sort actions into.</summary>
public sealed class AuditAreaTests
{
    [Theory]
    [InlineData("Login.Succeeded", AuditAreas.SignIns)]
    [InlineData("Logout", AuditAreas.SignIns)]
    [InlineData("Setup.Completed", AuditAreas.SignIns)]
    [InlineData("Rollout.Started", AuditAreas.Rollouts)]
    [InlineData("Update.Held", AuditAreas.Rollouts)]
    [InlineData("Client.Approved", AuditAreas.Rollouts)]
    [InlineData("AgentPackage.Released", AuditAreas.Rollouts)]
    [InlineData("Rollout.WithdrawnActionChanged", AuditAreas.Settings)]
    [InlineData("Ring.Renamed", AuditAreas.Settings)]
    [InlineData("Client.RebootPolicyChanged", AuditAreas.Settings)]
    [InlineData("OneTimeWindow.Added", AuditAreas.Settings)]
    public void Actions_LandInTheirArea(string action, string area) =>
        Assert.Equal(area, AuditAreas.Of(action));

    [Theory]
    [InlineData("Action.Queued")]
    [InlineData("Backup.Created")]
    [InlineData("Client.HardDeleted")]
    [InlineData("login.succeeded")]
    [InlineData("")]
    public void OtherActions_AreInNoArea(string action) =>
        Assert.Null(AuditAreas.Of(action));
}
