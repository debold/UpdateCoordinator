using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts;
using SUSyphos.Contracts.Scripts;
using SUSyphos.Server.Agents;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Scripts;

/// <summary>
/// The agent side of scripts (Marc, 2026-11-13): approved agents download an approved revision with its signatures, and
/// report every run with its output. Any approved agent may fetch any approved revision while scripts are switched on:
/// script texts hold no secrets (values travel separately), every dashboard role reads them too, and the signatures make
/// them tamper-proof.
/// </summary>
internal static class ScriptEndpoints
{
    /// <summary>Largest run report: the output limit plus room for the JSON around it.</summary>
    public const long MaxRunReportBytes = ScriptLimits.MaxOutputBytes * 2L + 64 * 1024;

    public static IEndpointRouteBuilder MapScriptEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(ApiRoutes.Script, ScriptAsync)
            .RequireAuthorization()
            .AddEndpointFilter(new MessagePolicyFilter(MessageType.Script))
            .WithName("AgentScript");

        endpoints.MapPost(ApiRoutes.AgentScriptRuns, ScriptRunAsync)
            .RequireAuthorization()
            .AddEndpointFilter(new MessagePolicyFilter(MessageType.ScriptRun))
            .WithMetadata(new RequestSizeLimitAttribute(MaxRunReportBytes))
            .WithName("AgentScriptRun");

        endpoints.MapGet(DownloadRoute, DownloadAsync)
            .RequireAuthorization(Administration.AdminRoles.Policies.Read)
            .WithName("ScriptDownload");
        return endpoints;
    }

    /// <summary>Download of an approved revision as .ps1 from the script page (every role may read scripts).</summary>
    public const string DownloadRoute = "/software-scripts/{packageId}/{phase}/{revision:int}";

    public static string DownloadPath(string packageId, ScriptPhase phase, int revision) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"/software-scripts/{Uri.EscapeDataString(packageId)}/{Components.ScriptText.Slug(phase)}/{revision}");

    internal static async Task<IResult> DownloadAsync(string packageId, string phase, int revision, ServerDbContext db, CancellationToken cancellationToken)
    {
        var parsed = Components.ScriptText.FromSlug(phase);
        var content = parsed is null ? null : await (from r in db.PackageScriptRevisions.AsNoTracking()
                                                     join s in db.PackageScripts.AsNoTracking() on r.ScriptId equals s.Id
                                                     where s.PackageId == packageId && s.Phase == parsed && r.Revision == revision
                                                     select r.Content).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (content is null)
        {
            return TypedResults.NotFound();
        }

        var safe = string.Concat(packageId.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_'));
        return TypedResults.File(content, "text/plain; charset=utf-8", string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{safe}-{phase}-r{revision}.ps1"));
    }

    internal static async Task<Results<Ok<ScriptPackage>, NotFound>> ScriptAsync(int id, int revision, ScriptAdministration scripts, CancellationToken cancellationToken) =>
        await scripts.IsEnabledAsync(cancellationToken).ConfigureAwait(false) && await scripts.PackageAsync(id, revision, cancellationToken).ConfigureAwait(false) is { } package
            ? TypedResults.Ok(package)
            : TypedResults.NotFound();

    /// <summary>Stores a run once (by its run ID); the output is cut to the limit and masked again for safety.</summary>
    internal static async Task<Results<NoContent, BadRequest<string>>> ScriptRunAsync(ScriptRunReport report, HttpContext httpContext, ServerDbContext db, CancellationToken cancellationToken)
    {
        if (report is null || !Enum.IsDefined(report.Phase) || !Enum.IsDefined(report.Action) || !Enum.IsDefined(report.Result)
            || string.IsNullOrWhiteSpace(report.PackageId) || report.PackageId.Length > PackageEntity.MaxIdLength || report.PackageId.Any(char.IsControl)
            || report.FinishedAt < report.StartedAt)
        {
            return TypedResults.BadRequest("The script run is invalid.");
        }

        var clientId = httpContext.User.GetClientId();
        if (await db.ScriptRuns.AnyAsync(r => r.Id == report.RunId, cancellationToken).ConfigureAwait(false))
        {
            return TypedResults.NoContent();
        }

        var (output, truncated) = ScriptOutput.Limit(report.Output);
        var (lines, errors, warnings) = ScriptOutput.Count(output);
        db.ScriptRuns.Add(new ScriptRunEntity
        {
            Id = report.RunId,
            ClientId = clientId,
            PackageId = report.PackageId,
            ScriptId = report.ScriptId,
            Revision = report.Revision,
            Phase = report.Phase,
            Action = report.Action,
            StartedAtUtc = report.StartedAt.UtcDateTime,
            FinishedAtUtc = report.FinishedAt.UtcDateTime,
            ExitCode = report.ExitCode,
            Result = report.Result,
            Output = output,
            Truncated = truncated || report.Truncated,
            Lines = lines,
            Errors = errors,
            Warnings = warnings,
            Detail = report.Detail is null ? null : new string([.. report.Detail.Where(c => !char.IsControl(c)).Take(512)]),
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.NoContent();
    }
}
