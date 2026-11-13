using System.Globalization;

namespace SUSyphos.Server.Components;

/// <summary>Addresses of the page of a server group (Marc, 2026-11-13: a page of its own instead of the edit dialog).</summary>
public static class GroupPage
{
    public static string Link(int groupId) => string.Create(CultureInfo.InvariantCulture, $"/administration/groups/{groupId}");

    public static string Link(int groupId, string anchor) => $"{Link(groupId)}#{anchor}";
}
