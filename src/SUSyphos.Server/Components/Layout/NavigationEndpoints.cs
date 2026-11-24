namespace SUSyphos.Server.Components.Layout;

/// <summary>
/// Hides or shows the navigation of the dashboard in this browser (Marc, 2026-11-24, admin center layout): a link that
/// sets a cookie and goes back to the page, so it works without the dashboard script; the script toggles it in place.
/// The cookie holds no personal data and only changes the layout.
/// </summary>
internal static class NavigationEndpoints
{
    public const string Path = "/dashboard/navigation";

    public static IEndpointRouteBuilder MapNavigationEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Path, (string? hidden, string? @return, HttpContext context) =>
        {
            context.Response.Cookies.Append(MainLayout.NavCookie, hidden == "1" ? "hidden" : "shown", new CookieOptions
            {
                HttpOnly = false,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                IsEssential = true,
                MaxAge = TimeSpan.FromDays(365),
                Path = "/",
            });
            return Results.LocalRedirect(SafeReturn(@return));
        }).RequireAuthorization(Administration.AdminRoles.Policies.Read);
        return endpoints;
    }

    /// <summary>Only a path of this site, never another host ("//", "/\", a scheme).</summary>
    internal static string SafeReturn(string? target) =>
        target is { Length: > 0 and <= 2000 } path && path[0] == '/' && (path.Length == 1 || (path[1] != '/' && path[1] != '\\')) && !path.Contains('\n') && !path.Contains('\r')
            ? path
            : "/";
}
