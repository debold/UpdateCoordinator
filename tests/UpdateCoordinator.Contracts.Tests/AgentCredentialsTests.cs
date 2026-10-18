using UpdateCoordinator.Contracts.Agents;

namespace UpdateCoordinator.Contracts.Tests;

public sealed class AgentCredentialsTests
{
    [Fact]
    public void ToHeaderAndTryParse_RoundTrip()
    {
        var clientId = Guid.NewGuid();

        var header = AgentCredentials.ToHeader(clientId, "s3cr:et");

        Assert.Equal("Basic", header.Scheme);
        Assert.True(AgentCredentials.TryParse(header.Parameter, out var parsedId, out var parsedSecret));
        Assert.Equal(clientId, parsedId);
        Assert.Equal("s3cr:et", parsedSecret);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("bm9jb2xvbg==")] // "nocolon"
    [InlineData("bm90LWEtZ3VpZDpzZWNyZXQ=")] // "not-a-guid:secret"
    [InlineData("MDAwMDAwMDAtMDAwMC0wMDAwLTAwMDAtMDAwMDAwMDAwMDAwOg==")] // "<guid>:" without secret
    public void TryParse_WithMalformedInput_ReturnsFalse(string? parameter)
    {
        Assert.False(AgentCredentials.TryParse(parameter, out _, out _));
    }

    [Fact]
    public void TryParse_WithOverlongInput_ReturnsFalse()
    {
        Assert.False(AgentCredentials.TryParse(new string('A', 1_000), out _, out _));
    }

    [Fact]
    public void ToHeader_WithoutSecret_Throws()
    {
        Assert.Throws<ArgumentException>(() => AgentCredentials.ToHeader(Guid.NewGuid(), ""));
    }
}
