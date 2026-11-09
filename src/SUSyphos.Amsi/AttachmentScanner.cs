using System.Runtime.InteropServices;

namespace SUSyphos.Amsi;

/// <summary>
/// Scans a file through <c>IAttachmentExecute::Save</c>, the Windows interface for downloaded files that calls every
/// registered antivirus product on the file itself (the same path browsers and WinGet use). Used for installers too large
/// to read into memory for AMSI. The interface reports a refusal only as a failed HRESULT; a product that removed the
/// file counts as a detection.
/// </summary>
public static partial class AttachmentScanner
{
    private static readonly Guid ClientId = new("6a3f8a8e-2f8d-4c3b-9c51-5b2f0d3c7e41");

    /// <summary>Scans the file; it must lie on a local drive.</summary>
    public static ScanResult ScanFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            return new ScanResult(ScanOutcome.Failed, "The file does not exist.");
        }

        int result;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("4125dd96-e03a-4103-8f70-e0597d803b9c"), throwOnError: true)!;
            var instance = Activator.CreateInstance(type)!;
            try
            {
                var execute = (IAttachmentExecute)instance;
                execute.SetClientGuid(ClientId);
                execute.SetClientTitle("SUSyphos");
                execute.SetLocalPath(path);
                execute.SetFileName(Path.GetFileName(path));
                result = execute.Save();
            }
            finally
            {
                Marshal.FinalReleaseComObject(instance);
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or TypeLoadException or ArgumentException)
        {
            return new ScanResult(ScanOutcome.Failed, $"Windows could not scan the file: {exception.Message}");
        }

        return Interpret(result, File.Exists(path));
    }

    /// <summary>S_OK with the file in place is clean; a removed file is a detection; anything else counts as not scanned.</summary>
    internal static ScanResult Interpret(int hresult, bool fileStillThere) =>
        !fileStillThere ? new ScanResult(ScanOutcome.Detected, "The antivirus product removed the file while scanning it; its name for the threat is in the product's own log.")
        : hresult >= 0 ? new ScanResult(ScanOutcome.Clean, "No threat found.")
        : new ScanResult(ScanOutcome.Failed, $"The antivirus check of Windows refused the file or failed (HRESULT 0x{hresult:X8}).");

    [ComImport]
    [Guid("73db1241-1e85-4581-8e4f-a81e1d0f8c57")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAttachmentExecute
    {
        void SetClientTitle([MarshalAs(UnmanagedType.LPWStr)] string title);

        void SetClientGuid([MarshalAs(UnmanagedType.LPStruct)] Guid guid);

        void SetLocalPath([MarshalAs(UnmanagedType.LPWStr)] string localPath);

        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string fileName);

        void SetSource([MarshalAs(UnmanagedType.LPWStr)] string source);

        void SetReferrer([MarshalAs(UnmanagedType.LPWStr)] string referrer);

        [PreserveSig]
        int CheckPolicy();

        [PreserveSig]
        int Prompt(IntPtr parentWindow, int prompt, out int action);

        [PreserveSig]
        int Save();
    }
}
