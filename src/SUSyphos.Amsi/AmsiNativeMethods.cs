using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

namespace SUSyphos.Amsi;

/// <summary>
/// Results of the AMSI scan functions (<c>AMSI_RESULT</c> in amsi.h). Everything from the "blocked by admin" range up
/// must be blocked, like the <c>AmsiResultIsMalware</c> macro, which has no exported counterpart.
/// </summary>
internal static class AmsiResult
{
    internal const uint Clean = 0;
    internal const uint NotDetected = 1;
    internal const uint BlockedByAdminStart = 16384;
    internal const uint BlockedByAdminEnd = 20479;
    internal const uint Detected = 32768;

    internal static bool IsMalware(uint result) => result >= BlockedByAdminStart;
}

/// <summary>Handle of <c>AmsiInitialize</c>, released with <c>AmsiUninitialize</c>, which cannot fail.</summary>
internal sealed class AmsiContextHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal AmsiContextHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle()
    {
        AmsiNativeMethods.AmsiUninitialize(handle);
        return true;
    }
}

/// <summary>
/// The flat AMSI API of <c>amsi.dll</c> (Windows Server 2016 and later). <c>amsi.dll</c> is no KnownDLL, so every
/// import is pinned to System32: a planted <c>amsi.dll</c> next to the program would otherwise switch the scan off and
/// run as SYSTEM inside the service.
/// </summary>
internal static class AmsiNativeMethods
{
    private const string AmsiDll = "amsi.dll";

    internal const int Ok = 0;

    [DllImport(AmsiDll, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int AmsiInitialize(string appName, out AmsiContextHandle amsiContext);

    [DllImport(AmsiDll)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern void AmsiUninitialize(IntPtr amsiContext);

    [DllImport(AmsiDll)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int AmsiOpenSession(AmsiContextHandle amsiContext, out IntPtr amsiSession);

    [DllImport(AmsiDll)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern void AmsiCloseSession(AmsiContextHandle amsiContext, IntPtr amsiSession);

    /// <summary>Scans a buffer within a session; the result says whether it is malware.</summary>
    /// <param name="amsiContext">Context of <see cref="AmsiInitialize"/>.</param>
    /// <param name="buffer">Content to scan.</param>
    /// <param name="length">Bytes of <paramref name="buffer"/> to scan.</param>
    /// <param name="contentName">File name; providers use it as a hint, so the real name improves detection.</param>
    /// <param name="amsiSession">Session of <see cref="AmsiOpenSession"/>.</param>
    /// <param name="result">AMSI result, see <see cref="AmsiResult"/>.</param>
    [DllImport(AmsiDll, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int AmsiScanBuffer(AmsiContextHandle amsiContext, [In] byte[] buffer, uint length, string contentName, IntPtr amsiSession, out uint result);
}
