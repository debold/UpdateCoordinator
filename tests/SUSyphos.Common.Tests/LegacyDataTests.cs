namespace SUSyphos.Common.Tests;

public sealed class LegacyDataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SUSyphos.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void LegacyFolder_IsMovedOnce()
    {
        var legacy = Path.Combine(_root, "UpdateCoordinator", "Agent");
        var target = Path.Combine(_root, "SUSyphos", "Agent");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "identity.dat"), "x");

        Assert.True(LegacyData.MoveDataFolder(legacy, target));
        Assert.True(File.Exists(Path.Combine(target, "identity.dat")));
        Assert.False(Directory.Exists(legacy));
        Assert.False(LegacyData.MoveDataFolder(legacy, target));
    }

    [Fact]
    public void ExistingData_IsNeverOverwritten()
    {
        var legacy = Path.Combine(_root, "UpdateCoordinator", "Server");
        var target = Path.Combine(_root, "SUSyphos", "Server");
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "server.db"), "new");

        Assert.False(LegacyData.MoveDataFolder(legacy, target));
        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "server.db")));
        Assert.True(Directory.Exists(legacy));
    }

    [Fact]
    public void NoLegacyFolder_DoesNothing()
    {
        Assert.False(LegacyData.MoveDataFolder(Path.Combine(_root, "missing"), Path.Combine(_root, "target")));
        Assert.False(Directory.Exists(Path.Combine(_root, "target")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
