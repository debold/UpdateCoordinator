using SUSyphos.Amsi;

namespace SUSyphos.Server.Tests;

/// <summary>
/// The AMSI library without a real detection: a scan of the test file would raise an alert in the antivirus product of
/// the machine, so it is left to the administrator's self test.
/// </summary>
public sealed class AmsiScannerTests
{
    [Theory]
    [InlineData(0u, false)]
    [InlineData(1u, false)]
    [InlineData(16383u, false)]
    [InlineData(16384u, true)]
    [InlineData(20479u, true)]
    [InlineData(32768u, true)]
    public void Malware_StartsAtTheAdministratorBlockRange(uint result, bool malware)
    {
        Assert.Equal(malware, AmsiResult.IsMalware(result));
    }

    [Fact]
    public void Description_TellsAnAdministratorBlockFromADetection()
    {
        Assert.Contains("administrator policy", AmsiScanner.Describe(16400), StringComparison.Ordinal);
        Assert.Contains("event 1116", AmsiScanner.Describe(32768), StringComparison.Ordinal);
    }

    [Fact]
    public void TestVector_IsTheStandardTestFile_UnmaskedOnlyAtRunTime()
    {
        var vector = AmsiScanner.TestVector();

        Assert.Equal(68, vector.Length);
        Assert.Equal((byte)'X', vector[0]);
        Assert.Equal((byte)'*', vector[^1]);
    }

    [Fact]
    public void HarmlessContent_IsCleanOrNotScanned_NeverDetected()
    {
        using var scanner = new AmsiScanner("SUSyphos.Tests");

        var result = scanner.ScanBuffer("just text"u8.ToArray(), "harmless.txt");

        // On a build runner AMSI may refuse the scan (Failed); a harmless buffer must just never count as a threat.
        Assert.NotEqual(ScanOutcome.Detected, result.Outcome);
        Assert.Equal(scanner.IsAvailable, scanner.Problem is null);
        Assert.True(scanner.Providers.Count > 0 || !scanner.IsAvailable);
    }

    [Fact]
    public void Files_TooLargeOrMissing_AreNotScanned()
    {
        using var scanner = new AmsiScanner("SUSyphos.Tests");
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "0123456789");

            var large = scanner.ScanFile(file, maxBytes: 5);
            var missing = scanner.ScanFile(file + ".missing");

            var expected = scanner.IsAvailable ? new[] { ScanOutcome.TooLarge, ScanOutcome.Failed } : [ScanOutcome.Unavailable, ScanOutcome.Unavailable];
            Assert.Equal(expected, new[] { large.Outcome, missing.Outcome });
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Providers_AreReadWithoutThrowing()
    {
        Assert.All(AmsiScanner.EnumerateProviders(), p => Assert.False(string.IsNullOrEmpty(p.Clsid)));
    }

    [Theory]
    [InlineData(0, true, ScanOutcome.Clean)]
    [InlineData(1, true, ScanOutcome.Clean)]
    [InlineData(unchecked((int)0x80004005), true, ScanOutcome.Failed)]
    [InlineData(0, false, ScanOutcome.Detected)]
    [InlineData(unchecked((int)0x800C000E), false, ScanOutcome.Detected)]
    public void AttachmentCheck_IsInterpretedSafely(int hresult, bool fileStillThere, ScanOutcome expected)
    {
        Assert.Equal(expected, AttachmentScanner.Interpret(hresult, fileStillThere).Outcome);
    }

    [Fact]
    public void AttachmentCheck_OfAHarmlessFile_NeverReportsAThreat_AndAMissingFileFails()
    {
        // A temporary file only; Windows may mark it as downloaded, nothing else on the machine changes.
        var file = Path.Combine(Path.GetTempPath(), $"susyphos-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(file, "harmless");

            Assert.NotEqual(ScanOutcome.Detected, AttachmentScanner.ScanFile(file).Outcome);
            Assert.Equal(ScanOutcome.Failed, AttachmentScanner.ScanFile(file + ".missing").Outcome);
        }
        finally
        {
            File.Delete(file);
        }
    }}
