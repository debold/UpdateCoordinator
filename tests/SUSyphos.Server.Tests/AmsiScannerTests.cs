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


    [Fact]
    public async Task LargeFile_IsScannedAsAWhole_ThroughAMappedView_FromAnyThread()
    {
        // Since 0.9.0-rc.8 every installer goes through AMSI (rc.7 lab: the Windows check for downloaded files took a
        // vanished file for a threat). A harmless file of a few megabytes is never a finding; the detail names the result.
        using var scanner = new AmsiScanner("SUSyphos.Tests");
        var file = Path.Combine(Path.GetTempPath(), $"susyphos-{Guid.NewGuid():N}.bin");
        var empty = file + ".empty";
        try
        {
            await File.WriteAllBytesAsync(file, new byte[3 * 1024 * 1024 + 17], TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(empty, [], TestContext.Current.CancellationToken);

            var result = await Task.Run(() => scanner.ScanFile(file), TestContext.Current.CancellationToken);
            var nothing = scanner.ScanFile(empty);

            Assert.NotEqual(ScanOutcome.Detected, result.Outcome);
            Assert.NotEqual(ScanOutcome.Detected, nothing.Outcome);
            if (result.Outcome == ScanOutcome.Clean)
            {
                Assert.Contains("AMSI result", result.Detail, StringComparison.Ordinal);
            }
            else
            {
                Assert.True(result.Outcome is ScanOutcome.Unavailable || result.Detail.Contains("HRESULT", StringComparison.Ordinal), result.Detail);
            }

            // The file is not locked after the scan.
            File.Delete(file);
        }
        finally
        {
            File.Delete(file);
            File.Delete(empty);
        }
    }

    [Fact]
    public void FileAboveTheLimitOfOneScan_IsNotScanned()
    {
        Assert.Equal(uint.MaxValue, AmsiScanner.MaxScanLength);
        Assert.True(AmsiScanner.DefaultMaxBytes <= AmsiScanner.MaxScanLength);
    }
}
