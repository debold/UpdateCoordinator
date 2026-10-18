using UpdateCoordinator.Contracts.Agents;
using UpdateCoordinator.Server.Agents;

namespace UpdateCoordinator.Server.Tests;

public sealed class MessagePolicyTests
{
    [Fact]
    public void Register_IsAllowedOnlyForUnregisteredCallers()
    {
        Assert.True(MessagePolicy.IsAllowed(MessageType.Register, null));
        Assert.All(Enum.GetValues<RegistrationState>(), state => Assert.False(MessagePolicy.IsAllowed(MessageType.Register, state)));
    }

    [Theory]
    [InlineData(RegistrationState.Pending, true)]
    [InlineData(RegistrationState.Approved, true)]
    [InlineData(RegistrationState.Rejected, false)]
    [InlineData(RegistrationState.Blocked, false)]
    [InlineData(RegistrationState.Deleted, false)]
    public void Heartbeat_IsAllowedForPendingAndApprovedOnly(RegistrationState state, bool expected)
    {
        Assert.Equal(expected, MessagePolicy.IsAllowed(MessageType.Heartbeat, state));
    }

    [Fact]
    public void Heartbeat_IsNotAllowedForUnregisteredCallers()
    {
        Assert.False(MessagePolicy.IsAllowed(MessageType.Heartbeat, null));
    }
}

public sealed class ClientSecretsTests
{
    [Fact]
    public void Create_ReturnsDistinctUrlSafeSecrets()
    {
        var first = ClientSecrets.Create();
        var second = ClientSecrets.Create();

        Assert.NotEqual(first, second);
        Assert.Equal(43, first.Length);
        Assert.DoesNotContain(first, c => c is '+' or '/' or '=');
    }

    [Fact]
    public void Verify_WithMatchingSecret_ReturnsTrue()
    {
        var secret = ClientSecrets.Create();

        Assert.True(ClientSecrets.Verify(secret, ClientSecrets.Hash(secret)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("other-secret")]
    public void Verify_WithDifferentSecret_ReturnsFalse(string presented)
    {
        Assert.False(ClientSecrets.Verify(presented, ClientSecrets.Hash(ClientSecrets.Create())));
    }

    [Fact]
    public void Verify_WithOverlongSecret_ReturnsFalse()
    {
        var overlong = new string('a', ClientSecrets.MaxSecretLength + 1);

        Assert.False(ClientSecrets.Verify(overlong, ClientSecrets.Hash(overlong)));
    }
}
