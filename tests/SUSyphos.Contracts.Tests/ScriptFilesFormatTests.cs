using System.IO.Compression;
using System.Security.Cryptography;

using SUSyphos.Contracts.Scripts;

namespace SUSyphos.Contracts.Tests;

/// <summary>The ZIP of files of a script revision (Marc, 2026-11-22): checked alike on upload and before unpacking.</summary>
public sealed class ScriptFilesFormatTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"susyphos-files-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    internal static byte[] Zip(params (string Name, string Content)[] entries) => Zip(entries.Select(e => (e.Name, e.Content, 0)).ToArray());

    internal static byte[] Zip((string Name, string Content, int Attributes)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content, attributes) in entries)
            {
                var entry = archive.CreateEntry(name);
                entry.ExternalAttributes = attributes;
                if (!name.EndsWith('/'))
                {
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write(content);
                }
            }
        }

        return buffer.ToArray();
    }

    [Fact]
    public void ValidZip_ListsItsFiles_AndUnpacksIntoTheFolder()
    {
        var zip = Zip(("config.bgi", "BGINFO"), ("sub/", string.Empty), ("sub/readme.txt", "hello"));

        var (files, error) = ScriptFilesFormat.Inspect(new MemoryStream(zip));

        Assert.Null(error);
        Assert.Equal(["config.bgi", "sub/readme.txt"], files.Select(f => f.Path));
        Assert.Equal(6, files[0].Size);
        Assert.Equal(Convert.ToHexString(SHA256.HashData("BGINFO"u8)), files[0].Sha256);
        Assert.Null(ScriptFilesFormat.Extract(new MemoryStream(zip), _folder));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(_folder, "sub", "readme.txt")));
    }

    [Theory]
    [InlineData("../evil.ps1")]
    [InlineData("sub/../../evil.ps1")]
    [InlineData("/etc/evil")]
    [InlineData("C:/Windows/evil.dll")]
    [InlineData("file.txt:stream")]
    [InlineData("sub//file.txt")]
    [InlineData("trailing. ")]
    public void PathOutsideOrOdd_IsRefused_OnUploadAndOnUnpacking(string name)
    {
        var zip = Zip((name, "x"));

        Assert.NotNull(ScriptFilesFormat.Inspect(new MemoryStream(zip)).Error);
        Assert.NotNull(ScriptFilesFormat.Extract(new MemoryStream(zip), _folder));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_folder)!, "evil.ps1")));
    }

    [Fact]
    public void Link_TooManyFiles_EmptyAndNoZip_AreRefused()
    {
        Assert.Contains("link", ScriptFilesFormat.Inspect(new MemoryStream(Zip([("link", "target", unchecked((int)0xA1FF0000))]))).Error, StringComparison.Ordinal);
        Assert.Contains("at most", ScriptFilesFormat.Inspect(new MemoryStream(Zip([.. Enumerable.Range(0, ScriptLimits.MaxFiles + 1).Select(i => ($"f{i}.txt", "x"))]))).Error, StringComparison.Ordinal);
        Assert.Contains("no file", ScriptFilesFormat.Inspect(new MemoryStream(Zip(("only/", string.Empty)))).Error, StringComparison.Ordinal);
        Assert.Contains("no valid ZIP", ScriptFilesFormat.Inspect(new MemoryStream("not a zip"u8.ToArray())).Error, StringComparison.Ordinal);
    }

    [Fact]
    public void FilesHash_IsPartOfTheSignedRevision()
    {
        var signedAt = new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);
        var without = ScriptSigningFormat.RevisionBytes(1, 1, "Microsoft.Sysinternals.BGInfo", ScriptPhase.AfterInstall, ScriptRunOn.Both, ScriptOnFailure.Stop, 300, "AA", "BB", signedAt, ScriptPowerShell.PowerShell7);
        var with = ScriptSigningFormat.RevisionBytes(1, 1, "Microsoft.Sysinternals.BGInfo", ScriptPhase.AfterInstall, ScriptRunOn.Both, ScriptOnFailure.Stop, 300, "AA", "BB", signedAt, ScriptPowerShell.PowerShell7, "cc");

        Assert.EndsWith("|pwsh7", System.Text.Encoding.ASCII.GetString(without), StringComparison.Ordinal);
        Assert.EndsWith("|pwsh7|files:CC", System.Text.Encoding.ASCII.GetString(with), StringComparison.Ordinal);
    }
}
