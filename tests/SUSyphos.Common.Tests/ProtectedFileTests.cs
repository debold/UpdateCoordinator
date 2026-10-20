using Microsoft.Extensions.Logging.Abstractions;

namespace SUSyphos.Common.Tests;

public sealed class ProtectedFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SUSyphos.Tests", Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, "nested", "data.bin");

    [Fact]
    public void WriteAndRead_RoundTrips_AndCreatesDirectory()
    {
        var file = Create("purpose-a");

        file.Write("content"u8);

        Assert.Equal("content"u8.ToArray(), file.Read());
    }

    [Fact]
    public void Write_DoesNotStorePlainText()
    {
        Create("purpose-a").Write("plain-text-marker"u8);

        Assert.True(File.ReadAllBytes(FilePath).AsSpan().IndexOf("plain-text-marker"u8) < 0);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void Read_WithOtherPurpose_ReturnsNull()
    {
        Create("purpose-a").Write("content"u8);

        Assert.Null(Create("purpose-b").Read());
    }

    [Fact]
    public void Read_MissingFile_ReturnsNull()
    {
        Assert.Null(Create("purpose-a").Read());
    }

    [Fact]
    public void Read_TamperedFile_ReturnsNull()
    {
        var file = Create("purpose-a");
        file.Write("content"u8);
        var bytes = File.ReadAllBytes(FilePath);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(FilePath, bytes);

        Assert.Null(file.Read());
    }

    [Fact]
    public void JsonFile_WithInvalidJson_ReturnsNull()
    {
        var file = Create("purpose-a");
        file.Write("{ not json"u8);

        Assert.Null(new ProtectedJsonFile<Sample>(file, NullLogger.Instance).Load());
    }

    [Fact]
    public void JsonFile_RoundTrips()
    {
        var json = new ProtectedJsonFile<Sample>(Create("purpose-a"), NullLogger.Instance);

        json.Save(new Sample("value", 42));

        Assert.Equal(new Sample("value", 42), json.Load());
    }

    [Theory]
    [InlineData("", "purpose")]
    [InlineData("path", "")]
    public void Constructor_WithEmptyArguments_Throws(string path, string purpose)
    {
        Assert.Throws<ArgumentException>(() => new ProtectedFile(path, purpose, NullLogger.Instance));
    }

    public sealed record Sample(string Text, int Number);

    private ProtectedFile Create(string purpose) => new(FilePath, purpose, NullLogger.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
