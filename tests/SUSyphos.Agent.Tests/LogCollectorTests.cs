using System.IO.Compression;

using Microsoft.Extensions.Time.Testing;

using SUSyphos.Agent.Actions;
using SUSyphos.Contracts.Agents;

namespace SUSyphos.Agent.Tests;

public sealed class LogCollectorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SUSyphos.Tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));

    public LogCollectorTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void OnlyRecentAgentLogs_ArePacked()
    {
        Write("agent-20261019.log", "today", _time.GetUtcNow().AddHours(-1));
        Write("agent-20261018.log", "yesterday", _time.GetUtcNow().AddDays(-1));
        Write("agent-20261001.log", "old", _time.GetUtcNow().AddDays(-18));
        Write("secrets.txt", "not a log", _time.GetUtcNow());
        Write("other-20261019.log", "other component", _time.GetUtcNow());

        var (archive, files) = new LogCollector(_directory, _time).Collect();

        Assert.Equal(2, files);
        Assert.Equal(["agent-20261019.log", "agent-20261018.log"], Entries(archive));
    }

    [Fact]
    public void LogStillBeingWritten_CanBeRead()
    {
        var path = Write("agent-20261019.log", "line", _time.GetUtcNow());
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        Assert.Equal(1, new LogCollector(_directory, _time).Collect().Files);
    }

    [Fact]
    public void OlderFiles_AreLeftOutToStayBelowTheLimit()
    {
        var random = new Random(1);
        foreach (var (name, age) in new[] { ("agent-a.log", 1), ("agent-b.log", 2) })
        {
            var bytes = new byte[AgentActionList.MaxLogBytes * 3 / 4];
            random.NextBytes(bytes);
            var path = Path.Combine(_directory, name);
            File.WriteAllBytes(path, bytes);
            File.SetLastWriteTimeUtc(path, _time.GetUtcNow().UtcDateTime.AddHours(-age));
        }

        var (archive, files) = new LogCollector(_directory, _time).Collect();

        Assert.Equal(1, files);
        Assert.True(archive.Length <= AgentActionList.MaxLogBytes);
        Assert.Equal(["agent-a.log"], Entries(archive));
    }

    [Fact]
    public void ManyLargeLogs_ArePackedWithoutBuildingSeveralArchives()
    {
        var random = new Random(2);
        for (var age = 1; age <= 5; age++)
        {
            var bytes = new byte[AgentActionList.MaxLogBytes / 2];
            random.NextBytes(bytes);
            var path = Path.Combine(_directory, $"agent-{age}.log");
            File.WriteAllBytes(path, bytes);
            File.SetLastWriteTimeUtc(path, _time.GetUtcNow().UtcDateTime.AddHours(-age));
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var (archive, files) = new LogCollector(_directory, _time).Collect();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(1, files);
        Assert.True(archive.Length <= AgentActionList.MaxLogBytes);

        // One archive (with stream growth and the final copy) at most; repacking would allocate several times the limit.
        Assert.True(allocated < 5L * AgentActionList.MaxLogBytes, $"Allocated {allocated / (1024 * 1024)} MB.");
    }

    [Fact]
    public void MissingFolder_GivesEmptyArchive()
    {
        var (archive, files) = new LogCollector(Path.Combine(_directory, "missing"), _time).Collect();

        Assert.Equal(0, files);
        Assert.Empty(Entries(archive));
    }

    private string Write(string name, string content, DateTimeOffset modified)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, modified.UtcDateTime);
        return path;
    }

    private static List<string> Entries(byte[] archive)
    {
        using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
        return [.. zip.Entries.Select(e => e.Name)];
    }
}
