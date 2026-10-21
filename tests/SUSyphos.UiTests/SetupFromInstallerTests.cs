using Microsoft.Playwright;

using SUSyphos.TestSupport;

namespace SUSyphos.UiTests;

/// <summary>
/// The installer opens the first setup with the token after # in the address: the page fills the token field and removes the
/// token from the address. Uses a central server of its own, because the shared demo lab already has an administrator.
/// </summary>
public sealed class SetupFromInstallerTests(UiFixture ui)
{
    private const string Token = "Fq3kVt9wQ2mZ7xR1bN5cL8pH4sJ6dG0yAeUiOoTn-_w";

    [Fact]
    public async Task TokenInTheAddress_FillsTheField_AndLeavesTheAddress()
    {
        Assert.SkipWhen(ui.Unavailable is not null, ui.Unavailable ?? string.Empty);
        await using var server = new KestrelServerFactory();
        server.InstallerToken.Value = Token;
        await using var context = await ui.Browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();

        await page.GotoAsync(new Uri(server.BaseAddress, "setup#token=" + Token).ToString());

        await Assertions.Expect(page.Locator("input[data-token-from-address]")).ToHaveValueAsync(Token);
        Assert.DoesNotContain(Token, page.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("#", page.Url, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("setup#token=short")]
    [InlineData("setup#token=%3Cscript%3Ealert(1)%3C%2Fscript%3Exxxxxxxxxxxxxxxxxxxxxxx")]
    [InlineData("setup#other=Fq3kVt9wQ2mZ7xR1bN5cL8pH4sJ6dG0yAeUiOoTn-_w")]
    public async Task MalformedOrMissingToken_LeavesTheFieldEmpty(string address)
    {
        Assert.SkipWhen(ui.Unavailable is not null, ui.Unavailable ?? string.Empty);
        await using var server = new KestrelServerFactory();
        await using var context = await ui.Browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();

        await page.GotoAsync(new Uri(server.BaseAddress, address).ToString());

        await Assertions.Expect(page.Locator("input[data-token-from-address]")).ToHaveValueAsync(string.Empty);
        Assert.DoesNotContain("#", page.Url, StringComparison.Ordinal);
    }
}
