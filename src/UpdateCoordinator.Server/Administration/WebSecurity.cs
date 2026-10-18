namespace UpdateCoordinator.Server.Administration;

/// <summary>
/// Prevents open redirects after login: only local paths are accepted.
/// </summary>
internal static class LocalReturnUrl
{
    public static string Sanitize(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl)
        && returnUrl.StartsWith('/')
        && !returnUrl.StartsWith("//", StringComparison.Ordinal)
        && !returnUrl.StartsWith("/\\", StringComparison.Ordinal)
        && !returnUrl.Contains("://", StringComparison.Ordinal)
        && returnUrl.Length <= 2048
            ? returnUrl
            : "/";
}

/// <summary>
/// Defensive HTTP headers for the web interface. The dashboard uses no script and no external resources,
/// so the content security policy can be strict.
/// </summary>
internal static class SecurityHeaders
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'none'; style-src 'self'; img-src 'self'; frame-ancestors 'none'; form-action 'self'; base-uri 'self'";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var headers = context.Response.Headers;
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        await next(context).ConfigureAwait(false);
    });
}
