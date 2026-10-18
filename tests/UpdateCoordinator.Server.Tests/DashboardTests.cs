using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using UpdateCoordinator.Contracts;
using UpdateCoordinator.Contracts.Agents;
using UpdateCoordinator.Server.Administration;
using UpdateCoordinator.TestSupport;

namespace UpdateCoordinator.Server.Tests;

public sealed class SetupTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task Setup_WithTokenFromFile_CreatesFirstAdministratorAndClosesSetup()
    {
        await using var factory = new ServerFactory();
        using var browser = new DashboardClient(factory);
        using (await browser.GetAsync("/setup"))
        {
        }

        var token = await File.ReadAllTextAsync(factory.SetupTokenPath, TestContext.Current.CancellationToken);

        using var response = await PostSetupAsync(browser, token, "admin", Password);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location?.AbsolutePath);
        Assert.False(File.Exists(factory.SetupTokenPath));
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var admin = await users.FindByNameAsync("admin");
        Assert.NotNull(admin);
        Assert.True(await users.IsInRoleAsync(admin, AdminRoles.Admin));

        using var again = await PostSetupAsync(browser, token, "second", Password);
        Assert.Null(await users.FindByNameAsync("second"));
        using var page = await browser.GetAsync("/setup");
        Assert.Contains("already been completed", await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("wrong-token")]
    [InlineData("")]
    public async Task Setup_WithInvalidToken_CreatesNothing(string token)
    {
        await using var factory = new ServerFactory();
        using var browser = new DashboardClient(factory);

        using var response = await PostSetupAsync(browser, token, "admin", Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, await factory.WithDbAsync(db => db.Users.CountAsync(TestContext.Current.CancellationToken)));
        Assert.True(File.Exists(factory.SetupTokenPath));
    }

    [Theory]
    [InlineData("admin", "short")]
    [InlineData("a", "correct horse battery")]
    [InlineData("bad name!", "correct horse battery")]
    public async Task Setup_WithInvalidAccount_CreatesNothing(string userName, string password)
    {
        await using var factory = new ServerFactory();
        using var browser = new DashboardClient(factory);
        using (await browser.GetAsync("/setup"))
        {
        }

        var token = await File.ReadAllTextAsync(factory.SetupTokenPath, TestContext.Current.CancellationToken);

        using var response = await PostSetupAsync(browser, token, userName, password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, await factory.WithDbAsync(db => db.Users.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task SetupToken_IsNotCreatedWhenAccountsExist()
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("existing", Password, AdminRoles.Admin);

        // A restart with existing accounts must not reopen the setup.
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SetupService>()
            .InitializeAsync(scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>(), TestContext.Current.CancellationToken);

        Assert.False(File.Exists(factory.SetupTokenPath));
        Assert.False(scope.ServiceProvider.GetRequiredService<SetupService>().IsAvailable);
    }

    private static Task<HttpResponseMessage> PostSetupAsync(DashboardClient browser, string token, string userName, string password) =>
        browser.PostFormAsync("/setup", new Dictionary<string, string>
        {
            ["Input.Token"] = token,
            ["Input.UserName"] = userName,
            ["Input.Password"] = password,
            ["Input.ConfirmPassword"] = password,
        });
}

public sealed class LoginTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task Dashboard_WithoutLogin_RedirectsToLogin()
    {
        await using var factory = new ServerFactory();
        using var browser = new DashboardClient(factory);

        using var response = await browser.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("https://localhost/login", response.Headers.Location?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_WithValidCredentials_OpensDashboardAndIsAudited()
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("reader", Password, AdminRoles.Reader);
        using var browser = new DashboardClient(factory);

        using var login = await browser.LoginAsync("reader", Password);
        using var overview = await browser.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
        Assert.Contains("<h1>Overview</h1>", await overview.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.True(await factory.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Actor == "reader" && e.Action == "Login.Succeeded", TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("reader", "wrong password!!")]
    [InlineData("nobody", "correct horse battery")]
    public async Task Login_WithInvalidCredentials_ShowsSameErrorAndIsAudited(string userName, string password)
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("reader", Password, AdminRoles.Reader);
        using var browser = new DashboardClient(factory);

        using var response = await browser.LoginAsync(userName, password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Invalid user name or password.", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.True(await factory.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Actor == userName && e.Action == "Login.Failed", TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task RepeatedFailures_LockTheAccount()
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("reader", Password, AdminRoles.Reader);
        using var browser = new DashboardClient(factory);

        for (var i = 0; i < 5; i++)
        {
            using var failed = await browser.LoginAsync("reader", "wrong password!!");
        }

        using var response = await browser.LoginAsync("reader", Password);

        Assert.Contains("locked", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        using var overview = await browser.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, overview.StatusCode);
    }

    [Theory]
    [InlineData("https://evil.example/", "/")]
    [InlineData("//evil.example/", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("/administration/audit", "/administration/audit")]
    public async Task ReturnUrl_IsLimitedToLocalPaths(string returnUrl, string expected)
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        using var browser = new DashboardClient(factory);

        using var response = await browser.LoginAsync("admin", Password, "/login?ReturnUrl=" + Uri.EscapeDataString(returnUrl));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("localhost", response.Headers.Location?.Host);
        Assert.Equal(expected, response.Headers.Location?.PathAndQuery);
    }

    [Fact]
    public async Task Logout_EndsTheSession()
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("reader", Password, AdminRoles.Reader);
        using var browser = new DashboardClient(factory);
        using (await browser.LoginAsync("reader", Password))
        {
        }

        using var logout = await browser.PostFormAsync("/logout", new Dictionary<string, string>(), pageToRead: "/");
        using var overview = await browser.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, overview.StatusCode);
    }

    [Fact]
    public async Task FormPost_WithoutAntiforgeryToken_IsRejected()
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("reader", Password, AdminRoles.Reader);
        using var browser = new DashboardClient(factory);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "login",
            ["Input.UserName"] = "reader",
            ["Input.Password"] = Password,
        });

        using var response = await browser.Http.PostAsync(new Uri("/login", UriKind.Relative), content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

public sealed class DashboardAuthorizationTests
{
    private const string Password = "correct horse battery";

    [Theory]
    [InlineData(AdminRoles.Reader, HttpStatusCode.Redirect)]
    [InlineData(AdminRoles.Operator, HttpStatusCode.Redirect)]
    [InlineData(AdminRoles.Admin, HttpStatusCode.OK)]
    public async Task AuditLog_IsOnlyForAdministrators(string role, HttpStatusCode expected)
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("user", Password, role);
        using var browser = new DashboardClient(factory);
        using (await browser.LoginAsync("user", Password))
        {
        }

        using var response = await browser.GetAsync("/administration/audit");

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.Redirect)
        {
            Assert.StartsWith("https://localhost/access-denied", response.Headers.Location?.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AgentCredentials_DoNotOpenTheDashboard()
    {
        await using var factory = new ServerFactory();
        using var browser = new DashboardClient(factory);
        var credentials = await RegisterTests.RegisterAsync(browser.Http);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/", UriKind.Relative));
        request.Headers.Authorization = AgentCredentials.ToHeader(credentials.ClientId, credentials.ClientSecret);

        using var response = await browser.Http.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task DashboardSession_DoesNotOpenAgentEndpoints()
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        using var browser = new DashboardClient(factory);
        using (await browser.LoginAsync("admin", Password))
        {
        }

        using var response = await browser.Http.PostAsJsonAsync(
            new Uri(ApiRoutes.AgentHeartbeat, UriKind.Relative),
            new HeartbeatRequest { AgentVersion = "1.0.0" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Pages_SendSecurityHeaders()
    {
        await using var factory = new ServerFactory();
        using var browser = new DashboardClient(factory);

        using var response = await browser.GetAsync("/login");

        Assert.Equal(SecurityHeaders.ContentSecurityPolicy, response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }
}

public sealed class AuditLogTests
{
    [Fact]
    public async Task Entries_FormAnIntactChain()
    {
        await using var factory = new ServerFactory();
        await WriteAsync(factory, "admin", "Test.First");
        await WriteAsync(factory, "admin", "Test.Second", "target", "details");

        Assert.Null(await VerifyAsync(factory));
    }

    [Theory]
    [InlineData("UPDATE AuditEntries SET Details = 'changed'")]
    [InlineData("DELETE FROM AuditEntries")]
    public async Task Entries_CannotBeChangedOrDeleted(string sql)
    {
        await using var factory = new ServerFactory();
        await WriteAsync(factory, "admin", "Test.Entry");

        await Assert.ThrowsAnyAsync<Exception>(() => factory.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(sql, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await factory.WithDbAsync(db => db.AuditEntries.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task ForgedEntry_IsDetected()
    {
        await using var factory = new ServerFactory();
        await WriteAsync(factory, "admin", "Test.Entry");

        // An entry inserted directly into the database file cannot carry a valid hash.
        await factory.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO AuditEntries (AtUtc, Actor, Action, PreviousHash, Hash) VALUES ('2026-10-18 12:00:00', 'attacker', 'Forged', zeroblob(32), zeroblob(32))",
            TestContext.Current.CancellationToken));

        Assert.NotNull(await VerifyAsync(factory));
    }

    [Fact]
    public async Task LongValues_AreTruncated()
    {
        await using var factory = new ServerFactory();

        await WriteAsync(factory, new string('a', 500), "Test.Long", new string('t', 500), new string('d', 5000));

        var entry = await factory.WithDbAsync(db => db.AuditEntries.SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(256, entry.Actor.Length);
        Assert.Equal(2048, entry.Details!.Length);
        Assert.Null(await VerifyAsync(factory));
    }

    private static async Task WriteAsync(ServerFactory factory, string actor, string action, string? target = null, string? details = null)
    {
        factory.StartServer();
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AuditLog>().WriteAsync(actor, action, target, details, TestContext.Current.CancellationToken);
    }

    private static async Task<long?> VerifyAsync(ServerFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuditLog>().FindFirstBrokenEntryAsync(TestContext.Current.CancellationToken);
    }
}

public sealed class LocalReturnUrlTests
{
    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("relative", "/")]
    [InlineData("http://evil.example", "/")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("/path?next=https://evil.example", "/")]
    [InlineData("/", "/")]
    [InlineData("/administration/audit", "/administration/audit")]
    public void Sanitize(string? input, string expected)
    {
        Assert.Equal(expected, LocalReturnUrl.Sanitize(input));
    }
}
