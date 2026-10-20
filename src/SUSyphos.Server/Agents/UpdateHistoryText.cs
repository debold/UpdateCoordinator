using System.Globalization;

using SUSyphos.Contracts.Agents;

namespace SUSyphos.Server.Agents;

/// <summary>
/// Plain-language texts for the Windows Update history (docs/05-agent-design.md, section 5, detection of external
/// updates): who started an installation and what came of it.
/// </summary>
internal static class UpdateHistoryText
{
    /// <summary>True if SUSyphos itself installed it.</summary>
    public static bool IsAgent(string? clientApplicationId) =>
        string.Equals(clientApplicationId, UpdateHistoryEntry.AgentClientApplicationId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Readable name of the application Windows recorded; unknown IDs are shown as reported.</summary>
    public static string Source(string? clientApplicationId) => clientApplicationId switch
    {
        null or "" => "unknown",
        _ when IsAgent(clientApplicationId) => "SUSyphos",
        _ when clientApplicationId.StartsWith("AutomaticUpdates", StringComparison.OrdinalIgnoreCase) => $"Windows Automatic Updates ({clientApplicationId})",
        _ when clientApplicationId.Contains("UpdateOrchestrator", StringComparison.OrdinalIgnoreCase) || clientApplicationId.Contains("UsoClient", StringComparison.OrdinalIgnoreCase)
            => $"Windows Update orchestrator, e.g. Settings ({clientApplicationId})",
        _ when clientApplicationId.Contains("Defender", StringComparison.OrdinalIgnoreCase) || clientApplicationId.Contains("MpSigStub", StringComparison.OrdinalIgnoreCase)
            => $"Microsoft Defender ({clientApplicationId})",
        _ when clientApplicationId.Contains("wusa", StringComparison.OrdinalIgnoreCase) => $"Standalone installer ({clientApplicationId})",
        _ => clientApplicationId,
    };

    /// <summary>Result with error code and its explanation for anything but success.</summary>
    public static string Result(InstallResultCode code, int hresult) =>
        code is InstallResultCode.Succeeded or InstallResultCode.SucceededWithErrors
            ? code.ToString()
            : string.Create(CultureInfo.InvariantCulture, $"{code}, 0x{hresult:X8}: {HResultCatalog.Describe(hresult)}");
}
