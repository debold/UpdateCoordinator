using UpdateCoordinator.Server.Data;

namespace UpdateCoordinator.Server.Rollouts;

/// <summary>How updates are named in the dashboard and the audit log.</summary>
internal static class UpdateText
{
    /// <summary>"KB5040000 Title", or the update ID if the update is not in the catalog.</summary>
    public static string Title(UpdateEntity? update, Guid updateId) =>
        update is null ? updateId.ToString("D") : Title(update.KbArticle, update.Title);

    public static string Title(string? kbArticle, string title) =>
        kbArticle is null ? title : $"KB{kbArticle} {title}";
}
