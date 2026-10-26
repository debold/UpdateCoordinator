using SUSyphos.Server.Data;

namespace SUSyphos.Server.Rollouts;

/// <summary>How updates are named in the dashboard and the audit log.</summary>
internal static class UpdateText
{
    /// <summary>"KB5040000 Title", or the update ID if the update is not in the catalog.</summary>
    public static string Title(UpdateEntity? update, Guid updateId) =>
        update is null ? updateId.ToString("D") : Title(update.KbArticle, update.Title);

    public static string Title(string? kbArticle, string title) =>
        kbArticle is null ? title : $"KB{kbArticle} {title}";

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

    /// <summary>Microsoft's support article (release notes) of a KB; null for an invalid number.</summary>
    public static string? ReleaseNotesLink(string? kb) => IsKb(kb) ? $"https://support.microsoft.com/help/{kb}" : null;

    /// <summary>The search for a KB in the Microsoft Update Catalog (download packages); null for an invalid number.</summary>
    public static string? CatalogLink(string? kb) => IsKb(kb) ? $"https://catalog.update.microsoft.com/Search.aspx?q=KB{kb}" : null;
}
