using SUSyphos.Server.Data;

namespace SUSyphos.Server.Rollouts;

/// <summary>How updates are named in the dashboard and the audit log.</summary>
internal static class UpdateText
{
    /// <summary>"KB5040000 Title", or the update ID if the update is not in the catalog.</summary>
    public static string Title(UpdateEntity? update, Guid updateId) =>
        update is null ? updateId.ToString("D") : Title(update.KbArticle, update.Title);

    /// <summary>"KB5040000 Title"; the title alone when it names the KB already, as most Microsoft titles do.</summary>
    public static string Title(string? kbArticle, string title) =>
        kbArticle is null || title.Contains($"KB{kbArticle}", StringComparison.OrdinalIgnoreCase) ? title : $"KB{kbArticle} {title}";

    /// <summary>Restart behaviour as Windows Update reports it, e.g. "No restart"; "Unknown" for older reports.</summary>
    public static string Restart(int? rebootBehavior) => rebootBehavior switch
    {
        (int)SUSyphos.Contracts.Agents.RebootBehavior.NeverReboots => "No restart",
        (int)SUSyphos.Contracts.Agents.RebootBehavior.AlwaysRequiresReboot => "Restart required",
        (int)SUSyphos.Contracts.Agents.RebootBehavior.CanRequestReboot => "May restart",
        _ => "Unknown",
    };

    /// <summary>A KB number as Windows Update reports it (digits only); anything else gets no link.</summary>
    public static bool IsKb(string? kb) => kb is { Length: >= 6 and <= 8 } && kb.All(char.IsAsciiDigit);

    /// <summary>The KB number in a title such as "2026-10 Cumulative Update … (KB5066835)"; null if there is none.</summary>
    public static string? KbIn(string? title) =>
        title is null ? null : System.Text.RegularExpressions.Regex.Match(title, @"\bKB(\d{6,8})\b", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100)) is { Success: true } match ? match.Groups[1].Value : null;

    /// <summary>
    /// KB numbers of Microsoft Defender updates that have no support article (security intelligence, platform and engine,
    /// older Defender and Security Essentials definitions); their notes are on one Microsoft Learn page (Marc, 2026-10-27).
    /// </summary>
    private static readonly HashSet<string> DefenderKbs = ["2267602", "4052623", "2461484", "915597"];

    /// <summary>Where Microsoft describes the Defender updates (security intelligence, platform and engine).</summary>
    public const string DefenderNotes = "https://learn.microsoft.com/defender-endpoint/microsoft-defender-antivirus-updates";

    /// <summary>Whether the release notes of a KB are the Defender update page rather than a support article.</summary>
    public static bool IsDefender(string? kb) => kb is not null && DefenderKbs.Contains(kb);

    /// <summary>Microsoft's support article (release notes) of a KB, the Defender update page for Defender updates; null for an invalid number.</summary>
    public static string? ReleaseNotesLink(string? kb) => !IsKb(kb) ? null : IsDefender(kb) ? DefenderNotes : $"https://support.microsoft.com/help/{kb}";

    /// <summary>The search for a KB in the Microsoft Update Catalog (download packages); null for an invalid number.</summary>
    public static string? CatalogLink(string? kb) => IsKb(kb) ? $"https://catalog.update.microsoft.com/Search.aspx?q=KB{kb}" : null;
}
