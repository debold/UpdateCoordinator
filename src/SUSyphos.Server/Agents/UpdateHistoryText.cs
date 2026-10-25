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

    /// <summary>
    /// Tag for who started an installation, the same everywhere: SUSyphos green, a definition update grey "expected"
    /// (whoever installs signatures first is normal, see <see cref="Rollouts.DefinitionUpdates"/>), anything else amber.
    /// </summary>
    public static (string Text, string Kind, string? Detail) StartedByTag(string? clientApplicationId, bool definition) =>
        IsAgent(clientApplicationId) ? ("SUSyphos", "ok", null)
        : definition ? ("expected", "neutral", $"definition update installed by {Source(clientApplicationId)}")
        : ("outside SUSyphos", "queue", Source(clientApplicationId));

    /// <summary>Short result for a tag: "Succeeded", "Succeeded with errors", "Already installed", "Failed" or "Aborted".</summary>
    public static string ResultLabel(InstallResultCode code, int hresult) => code switch
    {
        InstallResultCode.Succeeded => "Succeeded",
        InstallResultCode.SucceededWithErrors => "Succeeded with errors",
        _ when hresult == Rollouts.InstallEvidence.AlreadyInstalled => "Already installed",
        InstallResultCode.Aborted => "Aborted",
        InstallResultCode.Failed => "Failed",
        _ => code.ToString(),
    };

    /// <summary>Colour of the result tag, the same everywhere: green, yellow for success with errors, red for failures, grey when aborted.</summary>
    public static string ResultClass(InstallResultCode code, int hresult) => code switch
    {
        InstallResultCode.Succeeded => "ok",
        InstallResultCode.SucceededWithErrors => "queue",
        _ when hresult == Rollouts.InstallEvidence.AlreadyInstalled => "ok",
        InstallResultCode.Failed => "fail",
        _ => "neutral",
    };

    /// <summary>Error code and its explanation next to the tag; null for a success.</summary>
    public static string? ResultDetail(InstallResultCode code, int hresult) =>
        code is InstallResultCode.Succeeded or InstallResultCode.SucceededWithErrors
            ? null
            : string.Create(CultureInfo.InvariantCulture, $"0x{hresult:X8}: {HResultCatalog.Describe(hresult)}");

    /// <summary>Result with error code and its explanation for anything but success.</summary>
    public static string Result(InstallResultCode code, int hresult) =>
        code is InstallResultCode.Succeeded or InstallResultCode.SucceededWithErrors
            ? code.ToString()
            : string.Create(CultureInfo.InvariantCulture, $"{code}, 0x{hresult:X8}: {HResultCatalog.Describe(hresult)}");
}
