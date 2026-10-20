using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace SUSyphos.Server.Administration;

/// <summary>Backup download from the dashboard (Admin role; the form carries an antiforgery token).</summary>
internal static class BackupEndpoints
{
    public const string Route = "/administration/backup/download";

    public static IEndpointRouteBuilder MapBackupEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Route, DownloadAsync)
            .RequireAuthorization(AdminRoles.Policies.Administer)
            .DisableRequestTimeout()
            .WithName("BackupDownload");
        return endpoints;
    }

    internal static async Task<IResult> DownloadAsync(
        [FromForm] string? password,
        [FromForm] string? passwordRepeat,
        HttpContext httpContext,
        BackupService backup,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(password, passwordRepeat, StringComparison.Ordinal))
        {
            return Refuse("The two passwords differ.");
        }

        if (BackupService.CheckPassword(password) is { } error)
        {
            return Refuse(error);
        }

        // The archive is written to a temporary file first, so a failure never sends a truncated backup.
        var path = Path.Combine(Path.GetTempPath(), $"SUSyphos-{Guid.NewGuid():N}.zip");
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
        try
        {
            var outcome = await backup.CreateAsync(stream, password, httpContext.User.Identity?.Name ?? "unknown", cancellationToken).ConfigureAwait(false);
            if (!outcome.Succeeded)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                return Refuse(outcome.Error!);
            }

            stream.Position = 0;
            var fileName = $"susyphos-backup-{timeProvider.GetUtcNow().UtcDateTime:yyyyMMdd-HHmm}.zip";
            return TypedResults.File(stream, "application/zip", fileName);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static RedirectHttpResult Refuse(string message) =>
        TypedResults.Redirect($"/administration/backup?error={Uri.EscapeDataString(message)}");
}
