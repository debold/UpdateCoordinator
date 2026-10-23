using System.Collections.Frozen;
using System.Globalization;

namespace SUSyphos.Server.Agents;

/// <summary>
/// Translates Windows Update error codes into plain English text. Done centrally on the server, so the
/// text does not depend on the language of each managed server (docs/03-communication-protocol.md, section 9).
/// </summary>
internal static class HResultCatalog
{
    private static readonly FrozenDictionary<int, string> Descriptions = new Dictionary<int, string>
    {
        [0] = "Success.",
        [unchecked((int)0x80240001)] = "Windows Update Agent was unable to provide the service.",
        [unchecked((int)0x80240009)] = "Another Windows Update operation was in progress (for example Windows installing on its own at the same time).",
        [unchecked((int)0x80240016)] = "Operation tried to install while another installation was in progress.",
        [unchecked((int)0x80240017)] = "Operation was not performed because there are no applicable updates.",
        [unchecked((int)0x8024001E)] = "Operation did not complete because the service or system was being shut down.",
        [unchecked((int)0x80240022)] = "Operation failed for all the updates.",
        [unchecked((int)0x80240024)] = "There are no updates (the update is not offered by the update source).",
        [unchecked((int)0x8024002E)] = "Access to an unmanaged server is not allowed.",
        [unchecked((int)0x80240031)] = "The format of the update metadata is invalid.",
        [unchecked((int)0x8024200B)] = "The installation of the update failed (installation handler error).",
        [unchecked((int)0x80242016)] = "The state of the update after its post-reboot operation was unexpected.",
        [unchecked((int)0x8024401B)] = "The update server requires proxy authentication.",
        [unchecked((int)0x8024402C)] = "The proxy server or target server name cannot be resolved.",
        [unchecked((int)0x8024402F)] = "External cabinet file processing did not complete.",
        [unchecked((int)0x80244010)] = "The number of round trips to the server exceeded the maximum limit.",
        [unchecked((int)0x80244019)] = "The update server returned HTTP 404 (not found).",
        [unchecked((int)0x80244022)] = "The update server returned HTTP 503 (service unavailable).",
        [unchecked((int)0x8024A000)] = "Automatic Updates was unable to service incoming requests.",
        [unchecked((int)0x80246007)] = "The update has not been downloaded.",
        [unchecked((int)0x80246008)] = "The download failed because BITS could not be started.",
        [unchecked((int)0x80070002)] = "The system cannot find the file specified.",
        [unchecked((int)0x80070005)] = "Access is denied.",
        [unchecked((int)0x80070070)] = "There is not enough space on the disk.",
        [unchecked((int)0x80070103)] = "Already installed: the same or a better driver is installed, nothing to do.",
        [unchecked((int)0x80070643)] = "Fatal error during installation (the installer of the update failed; see its log, e.g. for .NET or Defender updates).",
        [unchecked((int)0x80070BC2)] = "The requested operation is successful, but changes will not take effect until the system is rebooted.",
        [unchecked((int)0x800705B4)] = "The operation timed out.",
        [unchecked((int)0x800F081F)] = "The source files could not be found (component store corruption, also seen while another installation runs at the same time). If it persists, repair with DISM /Online /Cleanup-Image /RestoreHealth.",
        [unchecked((int)0x800F0922)] = "The update could not be installed (e.g. not enough space in the system reserved partition).",
        [unchecked((int)0x80073712)] = "The component store is corrupted.",
        [unchecked((int)0x80040154)] = "The Windows Update Agent API is not registered.",
    }.ToFrozenDictionary();

    /// <summary>Plain text for a code; unknown codes are shown as hex so they can be looked up.</summary>
    public static string Describe(int hresult) =>
        Descriptions.TryGetValue(hresult, out var text)
            ? text
            : string.Create(CultureInfo.InvariantCulture, $"Unknown error 0x{hresult:X8}.");
}
