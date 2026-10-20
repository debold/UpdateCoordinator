using Microsoft.AspNetCore.Http.HttpResults;

using SUSyphos.Contracts;
using SUSyphos.Contracts.Trust;

namespace SUSyphos.Server.Trust;

internal static class TrustEndpoints
{
    public static IEndpointRouteBuilder MapTrustEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Public and self-authenticating: agents fetch it before they trust the TLS certificate.
        endpoints.MapGet(ApiRoutes.TrustBinding, GetBindingAsync)
            .AllowAnonymous()
            .WithName("GetTrustBinding");
        return endpoints;
    }

    internal static async Task<Results<Ok<TrustBinding>, ProblemHttpResult>> GetBindingAsync(
        TrustBindingService bindings,
        CancellationToken cancellationToken)
    {
        var binding = await bindings.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        return binding is null
            ? TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "No trust binding available.")
            : TypedResults.Ok(binding);
    }
}
