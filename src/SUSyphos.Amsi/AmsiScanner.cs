using System.IO.MemoryMappedFiles;

using Microsoft.Win32;

namespace SUSyphos.Amsi;

/// <summary>An AMSI provider registered on this machine (antivirus product).</summary>
/// <param name="Clsid">COM class id under the AMSI providers key.</param>
/// <param name="Name">Display name of the COM class; empty when unnamed.</param>
/// <param name="DllPath">In-process server; empty when the COM registration is incomplete.</param>
public sealed record AmsiProvider(string Clsid, string Name, string DllPath)
{
    /// <summary>"Name (dll)" for logs and the dashboard.</summary>
    public string Describe()
    {
        var file = string.IsNullOrEmpty(DllPath) ? "unknown DLL" : Path.GetFileName(DllPath);
        return string.IsNullOrEmpty(Name) ? $"{Clsid} ({file})" : $"{Name} ({file})";
    }
}

/// <summary>What a scan found.</summary>
public enum ScanOutcome
{
    /// <summary>The providers found nothing.</summary>
    Clean,

    /// <summary>A provider reported malware (or an administrator blocked the content).</summary>
    Detected,

    /// <summary>No provider is registered or AMSI could not start: nothing was scanned.</summary>
    Unavailable,

    /// <summary>The file was larger than the limit and was not scanned.</summary>
    TooLarge,

    /// <summary>The scan failed (file unreadable, AMSI error).</summary>
    Failed,
}

/// <summary>Result of a scan; <see cref="Detail"/> is phrased for the dashboard and the log.</summary>
public sealed record ScanResult(ScanOutcome Outcome, string Detail, uint? ResultCode = null);

/// <summary>
/// Scans files and buffers through AMSI. A scan never yields a threat name, only the AMSI result; the name exists in
/// the antivirus product (for Defender: event 1116, <c>Get-MpThreat</c>). Without a registered provider AMSI answers
/// "not detected" for everything, so <see cref="IsAvailable"/> is false then and every scan says
/// <see cref="ScanOutcome.Unavailable"/> instead of a worthless "clean". Scans run one at a time.
/// </summary>
public sealed class AmsiScanner : IDisposable
{
    /// <summary>Files up to this size are scanned (through a memory-mapped view, no copy in memory); larger ones are not.</summary>
    public const long DefaultMaxBytes = 2048L * 1024 * 1024;

    internal const string ProvidersKeyPath = @"SOFTWARE\Microsoft\AMSI\Providers";

    private readonly AmsiContextHandle? _context;
    private readonly Lock _gate = new();

    /// <summary>Starts AMSI for the application name (shown in the telemetry of the providers).</summary>
    public AmsiScanner(string appName)
    {
        ArgumentException.ThrowIfNullOrEmpty(appName);
        Providers = EnumerateProviders();
        if (Providers.Count == 0)
        {
            Problem = "No AMSI provider is registered on this machine; install an antivirus product with AMSI (Microsoft Defender registers one).";
            return;
        }

        var result = AmsiNativeMethods.AmsiInitialize(appName, out var context);
        if (result != AmsiNativeMethods.Ok || context.IsInvalid)
        {
            context.Dispose();
            Problem = $"AMSI could not be started (HRESULT 0x{result:X8}).";
            return;
        }

        _context = context;
        IsAvailable = true;
    }

    /// <summary>Whether a provider is registered and AMSI started.</summary>
    public bool IsAvailable { get; }

    /// <summary>Why scanning is not available; null when it is.</summary>
    public string? Problem { get; }

    /// <summary>The registered providers.</summary>
    public IReadOnlyList<AmsiProvider> Providers { get; }

    /// <summary>
    /// Scans a file up to <paramref name="maxBytes"/> as a whole through a read-only memory-mapped view, so even a large
    /// installer needs no copy in memory and every registered product (Defender or another vendor) sees the complete file
    /// (Marc, 2026-11-22: vendor-neutral, no product tool). The file name is passed to the providers as a hint.
    /// </summary>
    public ScanResult ScanFile(string path, long maxBytes = DefaultMaxBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!IsAvailable)
        {
            return new ScanResult(ScanOutcome.Unavailable, Problem!);
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var length = stream.Length;
            if (length > maxBytes || length > MaxScanLength)
            {
                return new ScanResult(ScanOutcome.TooLarge, $"The file has {length / (1024 * 1024)} MB; files above {Math.Min(maxBytes, MaxScanLength) / (1024 * 1024)} MB are not scanned.");
            }

            if (length == 0)
            {
                return ScanBuffer([], Path.GetFileName(path));
            }

            using var map = MemoryMappedFile.CreateFromFile(stream, mapName: null, capacity: 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
            using var view = map.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
            var handle = view.SafeMemoryMappedViewHandle;
            unsafe
            {
                byte* pointer = null;
                handle.AcquirePointer(ref pointer);
                try
                {
                    var start = (IntPtr)(pointer + view.PointerOffset);
                    return Scan(Path.GetFileName(path), (context, session) => (AmsiNativeMethods.AmsiScanMemory(context, start, (uint)length, Path.GetFileName(path), session, out var result), result));
                }
                finally
                {
                    handle.ReleasePointer();
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ScanResult(ScanOutcome.Failed, $"The file could not be read: {exception.Message}");
        }
    }

    /// <summary>The largest file one AMSI call takes (its length is a 32-bit number).</summary>
    public const long MaxScanLength = uint.MaxValue;

    /// <summary>Scans a buffer under a content name (e.g. a file name).</summary>
    public ScanResult ScanBuffer(byte[] data, string contentName)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrEmpty(contentName);
        return !IsAvailable
            ? new ScanResult(ScanOutcome.Unavailable, Problem!)
            : Scan(contentName, (context, session) => (AmsiNativeMethods.AmsiScanBuffer(context, data, (uint)data.Length, contentName, session, out var result), result));
    }

    /// <summary>
    /// One scan in its own session. Only an AMSI result in the malware range counts as a finding; every other answer is
    /// clean or failed, and the detail always names the AMSI result or HRESULT (rc.7 lab, 2026-11-22: a vanished file was
    /// taken for a threat without any finding of the product).
    /// </summary>
    private ScanResult Scan(string contentName, Func<AmsiContextHandle, IntPtr, (int HResult, uint Result)> scan)
    {
        lock (_gate)
        {
            var opened = AmsiNativeMethods.AmsiOpenSession(_context!, out var session);
            if (opened != AmsiNativeMethods.Ok)
            {
                return new ScanResult(ScanOutcome.Failed, $"AMSI could not open a session (HRESULT 0x{opened:X8}).");
            }

            try
            {
                var (scanned, result) = scan(_context!, session);
                if (scanned != AmsiNativeMethods.Ok)
                {
                    return new ScanResult(ScanOutcome.Failed, $"The AMSI scan of {contentName} failed (HRESULT 0x{scanned:X8}).");
                }

                return AmsiResult.IsMalware(result)
                    ? new ScanResult(ScanOutcome.Detected, Describe(result), result)
                    : new ScanResult(ScanOutcome.Clean, $"No threat found (AMSI result {result}).", result);
            }
            finally
            {
                AmsiNativeMethods.AmsiCloseSession(_context!, session);
            }
        }
    }

    public void Dispose() => _context?.Dispose();

    /// <summary>What an AMSI result means, for a result that blocks.</summary>
    internal static string Describe(uint result) => result is >= AmsiResult.BlockedByAdminStart and <= AmsiResult.BlockedByAdminEnd
        ? $"Blocked by an administrator policy of the antivirus product (AMSI result {result})."
        : $"The antivirus product reported a threat (AMSI result {result}); its name is in the product's own log, for Defender in event 1116.";

    /// <summary>
    /// The providers under HKLM\SOFTWARE\Microsoft\AMSI\Providers with their COM registration. Every failure counts as
    /// "no provider", which callers treat as "scanning is not available".
    /// </summary>
    public static IReadOnlyList<AmsiProvider> EnumerateProviders()
    {
        try
        {
            using var providers = Registry.LocalMachine.OpenSubKey(ProvidersKeyPath);
            if (providers is null)
            {
                return [];
            }

            return [.. providers.GetSubKeyNames().Select(clsid =>
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Classes\CLSID\{clsid}");
                using var server = key?.OpenSubKey("InprocServer32");
                var dll = server?.GetValue(null) as string ?? string.Empty;
                return new AmsiProvider(clsid, key?.GetValue(null) as string ?? string.Empty, dll.Length == 0 ? dll : Environment.ExpandEnvironmentVariables(dll).Trim('"'));
            })];
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    /// <summary>
    /// The standard antivirus test file (EICAR), harmless content every product reports as malware, for the self test
    /// an administrator starts. Stored XOR-masked: as a literal it would make the antivirus quarantine this source file
    /// and the compiled assembly.
    /// </summary>
    public static byte[] TestVector()
    {
        ReadOnlySpan<byte> masked =
        [
            0x02, 0x6F, 0x15, 0x7B, 0x0A, 0x7F, 0x1A, 0x1B, 0x0A, 0x01, 0x6E, 0x06,
            0x0A, 0x00, 0x02, 0x6F, 0x6E, 0x72, 0x0A, 0x04, 0x73, 0x6D, 0x19, 0x19,
            0x73, 0x6D, 0x27, 0x7E, 0x1F, 0x13, 0x19, 0x1B, 0x08, 0x77, 0x09, 0x0E,
            0x1B, 0x14, 0x1E, 0x1B, 0x08, 0x1E, 0x77, 0x1B, 0x14, 0x0E, 0x13, 0x0C,
            0x13, 0x08, 0x0F, 0x09, 0x77, 0x0E, 0x1F, 0x09, 0x0E, 0x77, 0x1C, 0x13,
            0x16, 0x1F, 0x7B, 0x7E, 0x12, 0x71, 0x12, 0x70,
        ];
        var plain = new byte[masked.Length];
        for (var i = 0; i < masked.Length; i++)
        {
            plain[i] = (byte)(masked[i] ^ 0x5A);
        }

        return plain;
    }
}
