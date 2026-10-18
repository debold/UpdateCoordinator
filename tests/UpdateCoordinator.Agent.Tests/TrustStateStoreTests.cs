using Microsoft.Extensions.Logging.Abstractions;

using UpdateCoordinator.Agent.Trust;
using UpdateCoordinator.Common;

namespace UpdateCoordinator.Agent.Tests;

public sealed class TrustStateStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "UpdateCoordinator.Tests", Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, "trust.dat");

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var state = new TrustState(Convert.ToBase64String(new byte[91]), 3, Convert.ToBase64String(new byte[32]), "token", true);
        var store = Create();

        store.Save(state);

        Assert.Equal(state, store.Load());
    }

    [Fact]
    public void Load_WithoutFile_ReturnsEmpty()
    {
        Assert.Equal(TrustState.Empty, Create().Load());
    }

    private static readonly TrustState[] InconsistentStates =
    [
        new TrustState("not base64!", 1, null, null, false),
        new TrustState(null, 1, "!!!", null, false),
        new TrustState(null, 1, Convert.ToBase64String(new byte[31]), null, false),
        new TrustState(null, -5, null, null, false),
        new TrustState(new string('A', 4_000), 1, null, null, false),
        new TrustState(null, 1, null, new string('x', 3_000), false),
    ];

    public static TheoryData<int> InconsistentStateIndexes() => [.. Enumerable.Range(0, InconsistentStates.Length)];

    [Theory]
    [MemberData(nameof(InconsistentStateIndexes))]
    public void Load_WithInconsistentContent_ReturnsEmptyInsteadOfFailing(int index)
    {
        Create().Save(InconsistentStates[index]);

        Assert.Equal(TrustState.Empty, Create().Load());
    }

    [Fact]
    public void Load_WithForeignJson_ReturnsEmpty()
    {
        new ProtectedFile(FilePath, "UpdateCoordinator.Agent.Trust.v1", NullLogger.Instance).Write("[1,2,3]"u8);

        Assert.Equal(TrustState.Empty, Create().Load());
    }

    private ProtectedTrustStateStore Create() => new(FilePath, NullLogger<ProtectedTrustStateStore>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
