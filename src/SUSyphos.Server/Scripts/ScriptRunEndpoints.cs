using Microsoft.EntityFrameworkCore;

using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Scripts;

/// <summary>Download of the output of one script run as a .log file (every role may read script output; secrets are masked).</summary>
internal static class ScriptRunEndpoints
{
    public const string DownloadRoute = "/servers/{clientId:guid}/script-runs/{runId:guid}/log";

    public static string DownloadPath(Guid clientId, Guid runId) => $"/servers/{clientId:D}/script-runs/{runId:D}/log";

    public static IEndpointRouteBuilder MapScriptRunEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(DownloadRoute, DownloadAsync).RequireAuthorization(AdminRoles.Policies.Read).WithName("ScriptRunLog");
        return endpoints;
    }

    internal static async Task<IResult> DownloadAsync(Guid clientId, Guid runId, ServerDbContext db, CancellationToken cancellationToken)
    {
        var run = await db.ScriptRuns.AsNoTracking().Where(r => r.Id == runId && r.ClientId == clientId)
            .Join(db.Clients, r => r.ClientId, c => c.Id, (r, c) => new { r.PackageId, r.Phase, r.Result, r.ExitCode, r.Detail, r.Output, r.FinishedAtUtc, c.HostName })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (run is null)
        {
            return TypedResults.NotFound();
        }

        var text = $"{run.HostName} {run.PackageId} {run.Phase}: {run.Result}, exit code {run.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}{(run.Detail is null ? null : " - " + run.Detail)}{Environment.NewLine}{run.Output}";
        var safe = string.Concat(run.PackageId.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_'));

        // The host name passed validation (letters, digits, hyphen), so it is safe in the file name.
        return TypedResults.File(System.Text.Encoding.UTF8.GetBytes(text), "text/plain; charset=utf-8", string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{run.HostName}-{safe}-{run.Phase}-{run.FinishedAtUtc:yyyyMMdd-HHmmss}.log"));
    }
}
