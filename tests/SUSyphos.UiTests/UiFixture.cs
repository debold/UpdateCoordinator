using Microsoft.Playwright;

using SUSyphos.TestSupport;

[assembly: AssemblyFixture(typeof(SUSyphos.UiTests.UiFixture))]

namespace SUSyphos.UiTests;

/// <summary>
/// One central server on a real HTTPS port with the demo lab, and one headless Microsoft Edge driven by Playwright, shared by
/// all UI tests. Edge ships with Windows and the GitHub runners, so no browser is downloaded.
/// </summary>
public sealed class UiFixture : IAsyncLifetime
{
    public const string Password = "correct horse battery";

    private readonly KestrelServerFactory _server = new();
    private IPlaywright? _playwright;

    public IBrowser Browser { get; private set; } = null!;

    /// <summary>Why the browser could not be started; tests are skipped then.</summary>
    public string? Unavailable { get; private set; }

    public Uri BaseAddress => _server.BaseAddress;

    public Guid MainServerId { get; private set; }

    /// <summary>Cookies of a signed-in administrator, so each test starts on the page it checks.</summary>
    public string AdminState { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        await _server.CreateUserAsync("marc", Password, "Admin");
        _server.AddDirectoryGroup(@"LAB\SUSyphos-Admins", "S-1-5-21-1-2-3-1100");
        _server.AddDirectoryGroup(@"LAB\SUSyphos-Operators", "S-1-5-21-1-2-3-1101");
        _server.AddDirectoryGroup(@"LAB\Domain Admins", "S-1-5-21-1-2-3-512");
        MainServerId = await DemoData.SeedAsync(_server);

        try
        {
            _playwright = await Playwright.CreateAsync();
            Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Channel = "msedge", Headless = true });
        }
        catch (PlaywrightException ex)
        {
            Unavailable = $"Microsoft Edge could not be started for the UI tests: {ex.Message}";
            return;
        }

        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true, BaseURL = BaseAddress.ToString() });
        var page = await context.NewPageAsync();
        await page.GotoAsync("/login");
        await page.FillAsync("input[autocomplete='username']", "marc");
        await page.FillAsync("input[autocomplete='current-password']", Password);
        await page.ClickAsync("button[type='submit']");
        await page.WaitForURLAsync(url => !url.Contains("/login", StringComparison.Ordinal));
        AdminState = await context.StorageStateAsync();
    }

    /// <summary>A browser context signed in as administrator with the given window size.</summary>
    public async Task<IBrowserContext> NewContextAsync(int width = 1440, int height = 900)
    {
        Assert.SkipWhen(Unavailable is not null, Unavailable ?? string.Empty);
        return await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BaseURL = BaseAddress.ToString(),
            StorageState = AdminState,
            ViewportSize = new ViewportSize { Width = width, Height = height },
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (Browser is not null)
        {
            await Browser.DisposeAsync();
        }

        _playwright?.Dispose();
        await _server.DisposeAsync();
    }
}
