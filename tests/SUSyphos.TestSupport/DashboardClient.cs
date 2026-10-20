using System.Net;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Mvc.Testing;

namespace SUSyphos.TestSupport;

/// <summary>
/// Browser-like client for the dashboard: HTTPS base address (secure cookies), cookies kept,
/// redirects not followed, and form posts including the hidden antiforgery and handler fields.
/// </summary>
public sealed partial class DashboardClient : IDisposable
{
    private readonly HttpClient _http;

    public DashboardClient(ServerFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _http = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
    }

    public HttpClient Http => _http;

    public Task<HttpResponseMessage> GetAsync(string path) =>
        _http.GetAsync(new Uri(path, UriKind.Relative), CancellationToken.None);

    /// <summary>Loads the page, takes its hidden form fields and posts them together with <paramref name="fields"/>.</summary>
    public async Task<HttpResponseMessage> PostFormAsync(string path, IReadOnlyDictionary<string, string> fields, string? pageToRead = null)
    {
        using var page = await GetAsync(pageToRead ?? path).ConfigureAwait(false);
        var html = await page.Content.ReadAsStringAsync().ConfigureAwait(false);

        var form = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in HiddenInputRegex().Matches(html))
        {
            form[WebUtility.HtmlDecode(match.Groups["name"].Value)] = WebUtility.HtmlDecode(match.Groups["value"].Value);
        }

        foreach (var (name, value) in fields)
        {
            form[name] = value;
        }

        using var content = new FormUrlEncodedContent(form);
        return await _http.PostAsync(new Uri(path, UriKind.Relative), content).ConfigureAwait(false);
    }

    public Task<HttpResponseMessage> LoginAsync(string userName, string password, string path = "/login") =>
        PostFormAsync(path, new Dictionary<string, string> { ["Input.UserName"] = userName, ["Input.Password"] = password }, pageToRead: "/login");

    public void Dispose() => _http.Dispose();

    [GeneratedRegex("<input[^>]*type=\"hidden\"[^>]*name=\"(?<name>[^\"]+)\"[^>]*value=\"(?<value>[^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex HiddenInputRegex();
}
