using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

namespace SUSyphos.Agent.Scripts;

/// <summary>
/// A Windows job object that ends every process in it when it is closed (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE): a script that
/// times out is stopped with every process it started, also those that left the parent tree (Marc, 2026-11-13).
/// </summary>
internal sealed partial class JobObject : IDisposable
{
    private const int ExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;

    private readonly SafeFileHandle _handle;

    public JobObject()
    {
        _handle = CreateJobObjectW(IntPtr.Zero, null);
        if (_handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var info = new ExtendedLimit { BasicLimitInformation = new BasicLimit { LimitFlags = KillOnJobClose } };
        if (!SetInformationJobObject(_handle, ExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<ExtendedLimit>()))
        {
            var error = Marshal.GetLastPInvokeError();
            _handle.Dispose();
            throw new Win32Exception(error);
        }
    }

    /// <summary>Puts a started process into the job; its children follow it.</summary>
    public void Add(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!AssignProcessToJobObject(_handle, process.SafeHandle))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    /// <summary>Ends every process of the job now.</summary>
    public void Terminate(uint exitCode) => _ = TerminateJobObject(_handle, exitCode);

    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimit
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimit
    {
        public BasicLimit BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimit info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(SafeFileHandle job, uint exitCode);
}
