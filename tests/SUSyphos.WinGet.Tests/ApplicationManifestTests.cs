using System.Text;

namespace SUSyphos.WinGet.Tests;

/// <summary>
/// The programs that run the WinGet engine declare Windows 10 support in their application manifest. Without it Windows
/// reports 6.2 (Windows 8) to the process, and WinGet rejects every installer with a minimum Windows version (lab,
/// 2026-11-21: BGInfo and Visual C++ 2013 failed with 0x8A150010 on Windows Server 2019, 2022 and 2025).
/// </summary>
public sealed class ApplicationManifestTests
{
    /// <summary>The supportedOS id of Windows 10, 11 and Windows Server 2016 and later.</summary>
    private const string Windows10 = "{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}";

    [Theory]
    [InlineData("SUSyphos.Agent.WinGet.exe")]
    [InlineData("susyphos-wingetprobe.exe")]
    public void WinGetPrograms_DeclareWindows10(string program)
    {
        var manifest = EmbeddedManifest(Path.Combine(AppContext.BaseDirectory, program));

        Assert.Contains("<compatibility", manifest, StringComparison.Ordinal);
        Assert.Contains($"<supportedOS Id=\"{Windows10}\"", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("level=\"asInvoker\"", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void ManifestCheck_FindsAMissingEntry()
    {
        // The default manifest of the .NET app host has no compatibility section; the check has to tell the difference.
        const string Default = "<assembly xmlns=\"urn:schemas-microsoft-com:asm.v1\" manifestVersion=\"1.0\"><trustInfo /></assembly>";
        var file = Path.Combine(Path.GetTempPath(), $"manifest-{Guid.NewGuid():N}.exe");
        try
        {
            File.WriteAllBytes(file, [.. new byte[64], .. Encoding.UTF8.GetBytes(Default), .. new byte[64]]);
            Assert.DoesNotContain(Windows10, EmbeddedManifest(file), StringComparison.OrdinalIgnoreCase);
            File.WriteAllBytes(file, new byte[256]);
            Assert.Throws<InvalidDataException>(() => EmbeddedManifest(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>The application manifest embedded in a program (the XML of its manifest resource).</summary>
    private static string EmbeddedManifest(string path)
    {
        var text = Encoding.UTF8.GetString(File.ReadAllBytes(path));
        var start = text.IndexOf("<assembly", StringComparison.Ordinal);
        var end = start < 0 ? -1 : text.IndexOf("</assembly>", start, StringComparison.Ordinal);
        return start < 0 || end < 0 ? throw new InvalidDataException($"{path} has no application manifest.") : text[start..(end + "</assembly>".Length)];
    }
}
