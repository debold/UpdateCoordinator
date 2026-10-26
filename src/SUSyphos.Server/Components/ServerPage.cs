namespace SUSyphos.Server.Components;

/// <summary>
/// The tabs of the server detail page and the box each anchor lives in (docs/06-dashboard.md, section 8 "Tabs"), so every
/// link to a part of the page (from other pages, tiles, notifications) opens the right tab.
/// </summary>
public static class ServerPage
{
    public const string Overview = "overview";
    public const string Updates = "updates";
    public const string Security = "security";
    public const string History = "history";
    public const string Configuration = "configuration";
    public const string System = "system";
    public const string Activity = "activity";

    /// <summary>The tabs in their order.</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> Tabs =
        [(Overview, "Overview"), (Updates, "Updates"), (Security, "Security"), (History, "History"), (Configuration, "Configuration"), (System, "System"), (Activity, "Activity")];

    /// <summary>The tab of a box anchor; the overview for anchors it does not know.</summary>
    public static string TabOf(string anchor) => anchor switch
    {
        "missing" => Updates,
        "security" or "vulnerabilities" => Security,
        "history" or "windows-history" => History,
        "assignment" or "agent-settings" => Configuration,
        "system" => System,
        "activity" or "remote-actions" => Activity,
        _ => Overview,
    };

    /// <summary>A known tab key, or the overview.</summary>
    public static string Normalize(string? tab) => Tabs.Any(t => t.Key == tab) ? tab! : Overview;

    /// <summary>Address of a tab of the server page, relative to the dashboard root without leading slash.</summary>
    public static string TabPath(Guid id, string tab) => tab == Overview ? $"servers/{id}" : $"servers/{id}?tab={tab}";

    /// <summary>Address of a box of the server page in its tab, relative to the dashboard root without leading slash.</summary>
    public static string Path(Guid id, string anchor) => $"{TabPath(id, TabOf(anchor))}#{anchor}";

    /// <summary>Link to a box of the server page in its tab.</summary>
    public static string Link(Guid id, string anchor) => "/" + Path(id, anchor);
}
