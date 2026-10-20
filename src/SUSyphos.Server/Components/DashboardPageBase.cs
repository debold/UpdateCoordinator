using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace SUSyphos.Server.Components;

/// <summary>
/// Common base of dashboard pages: the signed-in user as audit actor and a status message.
/// </summary>
public abstract class DashboardPageBase : ComponentBase
{
    [CascadingParameter]
    private Task<AuthenticationState>? AuthenticationState { get; set; }

    [Inject]
    private IAuthorizationService Authorization { get; set; } = default!;

    /// <summary>Result message of the last action, shown at the top of the page.</summary>
    protected string? Message { get; set; }

    /// <summary>True if <see cref="Message"/> reports a refused action.</summary>
    protected bool MessageIsError { get; set; }

    protected async Task<string> GetActorAsync() =>
        AuthenticationState is null ? "unknown" : (await AuthenticationState).User.Identity?.Name ?? "unknown";

    /// <summary>
    /// Checks a policy for actions on pages that are readable with a lower role. Hiding a form is not enough:
    /// every handler checks the permission itself.
    /// </summary>
    protected async Task<bool> IsAuthorizedAsync(string policy)
    {
        if (AuthenticationState is null)
        {
            return false;
        }

        var result = await Authorization.AuthorizeAsync((await AuthenticationState).User, policy);
        return result.Succeeded;
    }

    protected void ShowResult(string? error, string success)
    {
        MessageIsError = error is not null;
        Message = error ?? success;
    }
}
