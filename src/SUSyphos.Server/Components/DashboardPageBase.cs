using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace SUSyphos.Server.Components;

/// <summary>
/// Common base of dashboard pages: the signed-in user as audit actor and a status message.
/// </summary>
public abstract class DashboardPageBase : ComponentBase
{
    /// <summary>Address value that carries the success message of an action across the redirect after it.</summary>
    public const string DoneParameter = "done";

    private static readonly TimeSpan DoneLifetime = TimeSpan.FromMinutes(2);

    private string? _message;
    private bool _doneRead;

    [CascadingParameter]
    private Task<AuthenticationState>? AuthenticationState { get; set; }

    [Inject]
    private IAuthorizationService Authorization { get; set; } = default!;

    [Inject]
    private IDataProtectionProvider DataProtection { get; set; } = default!;

    [Inject]
    private NavigationManager PageNavigation { get; set; } = default!;

    /// <summary>
    /// Result message of the last action, shown at the top of the page; after <see cref="ShowResultAndReload"/> it comes
    /// from the protected <c>done</c> value of the address.
    /// </summary>
    protected string? Message
    {
        get => _message ?? ReadDoneMessage();
        set => _message = value;
    }

    /// <summary>True if <see cref="Message"/> reports a refused action.</summary>
    protected bool MessageIsError { get; set; }

    /// <summary>The current address without the message of a finished action, e.g. for the automatic refresh.</summary>
    protected string AddressWithoutMessage => PageNavigation.GetUriWithQueryParameter(DoneParameter, (string?)null);

    private ITimeLimitedDataProtector Protector => DataProtection.CreateProtector("SUSyphos.Dashboard.DoneMessage.v1").ToTimeLimitedDataProtector();

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

    /// <summary>
    /// After a successful action, loads the page again with GET (post/redirect/get), so reloading the page (F5 or the
    /// automatic refresh) never posts the form a second time. The success message travels in the address, protected
    /// against changes and valid for two minutes, so nobody can put their own text on the page with a link.
    /// An error stays on the posted page, which keeps the input.
    /// </summary>
    protected void ShowResultAndReload(string? error, string success)
    {
        if (error is not null)
        {
            ShowResult(error, success);
            return;
        }

        var token = Protector.Protect(success, DoneLifetime);
        PageNavigation.NavigateTo(PageNavigation.GetUriWithQueryParameters(new Dictionary<string, object?> { [DoneParameter] = token, ["dialog"] = null }));
    }

    private string? ReadDoneMessage()
    {
        if (_doneRead)
        {
            return null;
        }

        _doneRead = true;
        var query = QueryHelpers.ParseQuery(new Uri(PageNavigation.Uri).Query);
        if (!query.TryGetValue(DoneParameter, out var values) || values.ToString() is not { Length: > 0 and <= 2048 } token)
        {
            return null;
        }

        try
        {
            _message = Protector.Unprotect(token);
            MessageIsError = false;
            return _message;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Changed, expired or from another installation: show nothing.
            return null;
        }
    }
}
