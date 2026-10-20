using Microsoft.Extensions.Logging.Abstractions;

using SUSyphos.Agent.Identity;

namespace SUSyphos.Agent.Tests;

public sealed class DpapiAgentIdentityStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SUSyphos.Tests", Guid.NewGuid().ToString("N"));
    private readonly DpapiAgentIdentityStore _store;

    public DpapiAgentIdentityStoreTests()
    {
        _store = new DpapiAgentIdentityStore(FilePath, NullLogger<DpapiAgentIdentityStore>.Instance);
    }

    private string FilePath => Path.Combine(_directory, "identity.dat");

    [Fact]
    public void SaveAndLoad_RoundTripsIdentity()
    {
        var identity = new AgentIdentity(Guid.NewGuid(), "secret-value");

        _store.Save(identity);

        Assert.Equal(identity, _store.Load());
    }

    [Fact]
    public void Save_DoesNotWriteSecretInPlainText()
    {
        _store.Save(new AgentIdentity(Guid.NewGuid(), "plain-text-marker"));

        var content = File.ReadAllText(FilePath);
        Assert.DoesNotContain("plain-text-marker", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_WithoutFile_ReturnsNull()
    {
        Assert.Null(_store.Load());
    }

    [Fact]
    public void Load_WithCorruptFile_ReturnsNull()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(FilePath, [1, 2, 3, 4]);

        Assert.Null(_store.Load());
    }

    [Fact]
    public void Delete_RemovesIdentity()
    {
        _store.Save(new AgentIdentity(Guid.NewGuid(), "secret-value"));

        _store.Delete();

        Assert.Null(_store.Load());
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Delete_WithoutFile_DoesNotThrow()
    {
        var exception = Record.Exception(_store.Delete);

        Assert.Null(exception);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
