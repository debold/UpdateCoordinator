using System.Net;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using SUSyphos.Server.Administration;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

/// <summary>
/// A central server whose Negotiate scheme is replaced by a test handler: the header X-Test-Windows carries
/// "SID|DOMAIN\name|group SID,group SID", so the Windows sign-in can be tested without a domain.
/// </summary>
internal sealed class WindowsServerFactory : ServerFactory
{
    public const string Header = FakeWindowsAuthentication.Header;

    public bool WindowsSwitchInConfiguration { get; init; } = true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Authentication:Windows:Enabled", WindowsSwitchInConfiguration ? "true" : "false");
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IAccountResolver>(new FakeResolver());
        });
    }

    private sealed class FakeResolver : IAccountResolver
    {
        public (string Sid, string Name)? Resolve(string name) =>
            string.Equals(name, @"LAB\SUSyphos-Operators", StringComparison.OrdinalIgnoreCase) ? ("S-1-5-21-1-2-3-1101", @"LAB\SUSyphos-Operators") : null;
    }

}

public sealed class WindowsSignInTests
{
    private const string Password = "correct horse battery";
    private const string Admins = "S-1-5-21-1-2-3-1100";
    private const string Operators = "S-1-5-21-1-2-3-1101";
    private const string Alice = "S-1-5-21-1-2-3-2001";

    [Fact]
    public async Task SwitchedOff_TheRouteIsMissing_AndTheSignInPageShowsNoButton()
    {
        await using var factory = new WindowsServerFactory();
        using var browser = new DashboardClient(factory);

        using var route = await browser.GetAsync(WindowsSignIn.Route);
        var page = await (await browser.GetAsync("/login")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, route.StatusCode);
        Assert.DoesNotContain("Sign in with Windows", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MappedGroupMembers_SignIn_WithTheHighestMappedRole_AndOthersAreRefused()
    {
        await using var factory = new WindowsServerFactory();
        await ConfigureAsync(factory, (Operators, AdminRoles.Operator), (Admins, AdminRoles.Admin));
        using var browser = new DashboardClient(factory);

        var page = await (await browser.GetAsync("/login")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Sign in with Windows", page, StringComparison.Ordinal);

        // Without Windows credentials the browser is asked for them.
        using (var challenge = await browser.GetAsync(WindowsSignIn.Route))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, challenge.StatusCode);
            Assert.Equal("Negotiate", challenge.Headers.WwwAuthenticate.ToString());
        }

        using (var signedIn = await WindowsGetAsync(browser, $"{WindowsSignIn.Route}?returnUrl=%2Fservers", Alice, @"LAB\alice", Operators, Admins))
        {
            Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
            Assert.Equal("/servers", signedIn.Headers.Location?.OriginalString);
        }

        // The dashboard cookie works like a local sign-in; the role is Admin because one of the groups maps to it.
        using (var users = await browser.GetAsync("/administration/users"))
        {
            Assert.Equal(HttpStatusCode.OK, users.StatusCode);
            var html = await users.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains(@"LAB\alice", html, StringComparison.Ordinal);
            Assert.Contains("from AD groups", html, StringComparison.Ordinal);
        }

        var account = await WithUsersAsync(factory, async users =>
        {
            var user = await users.FindByLoginAsync(WindowsSignIn.LoginProvider, Alice);
            return (user!.UserName, await users.GetRolesAsync(user));
        });
        Assert.Equal(@"LAB\alice", account.UserName);
        Assert.Equal([AdminRoles.Admin], account.Item2);

        using var stranger = new DashboardClient(factory);
        using (var refused = await WindowsGetAsync(stranger, WindowsSignIn.Route, "S-1-5-21-1-2-3-3001", @"LAB\bob", "S-1-5-21-1-2-3-999"))
        {
            Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
            Assert.Equal("/login?windows=norole", refused.Headers.Location?.OriginalString);
        }

        var message = await (await stranger.GetAsync("/login?windows=norole")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("in none of the AD groups", message, StringComparison.Ordinal);
        Assert.True(await factory.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "Login.WindowsNoRole", TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task RoleFollowsTheGroups_AtEverySignIn_AndALockedAccountIsRefused()
    {
        await using var factory = new WindowsServerFactory();
        await ConfigureAsync(factory, (Operators, AdminRoles.Operator), (Admins, AdminRoles.Admin));

        using (var first = new DashboardClient(factory))
        using (await WindowsGetAsync(first, WindowsSignIn.Route, Alice, @"LAB\alice", Admins))
        {
        }

        using (var second = new DashboardClient(factory))
        using (await WindowsGetAsync(second, WindowsSignIn.Route, Alice, @"LAB\alice-renamed", Operators))
        {
        }

        var account = await WithUsersAsync(factory, async users =>
        {
            var user = await users.FindByLoginAsync(WindowsSignIn.LoginProvider, Alice);
            return (user!.UserName, await users.GetRolesAsync(user), user.Id);
        });
        Assert.Equal(@"LAB\alice-renamed", account.UserName);
        Assert.Equal([AdminRoles.Operator], account.Item2);

        await factory.WithDbAsync(async db =>
        {
            var user = await db.Users.SingleAsync(u => u.Id == account.Id, TestContext.Current.CancellationToken);
            user.LockoutEnabled = true;
            user.LockoutEnd = DateTimeOffset.MaxValue;
            return await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

        using var third = new DashboardClient(factory);
        using var locked = await WindowsGetAsync(third, WindowsSignIn.Route, Alice, @"LAB\alice-renamed", Operators);
        Assert.Equal("/login?windows=locked", locked.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task WindowsAccounts_KeepTheirRoleFromAd_AndTheLastLocalAdministratorStays()
    {
        await using var factory = new WindowsServerFactory();
        await ConfigureAsync(factory, (Admins, AdminRoles.Admin));
        using (var windows = new DashboardClient(factory))
        using (await WindowsGetAsync(windows, WindowsSignIn.Route, Alice, @"LAB\alice", Admins))
        {
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var administration = scope.ServiceProvider.GetRequiredService<UserAdministration>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var alice = await users.FindByLoginAsync(WindowsSignIn.LoginProvider, Alice);
        var local = await users.FindByNameAsync("admin");

        Assert.False((await administration.SetRoleAsync(alice!.Id, AdminRoles.Reader, "admin", CancellationToken.None)).Succeeded);
        Assert.False((await administration.ResetPasswordAsync(alice.Id, "another long passphrase", "admin", CancellationToken.None)).Succeeded);

        // An administrator from AD does not replace the last local one.
        Assert.False((await administration.DeleteAsync(local!.Id, @"LAB\alice", CancellationToken.None)).Succeeded);
        Assert.True((await administration.SetLockedAsync(alice.Id, true, "admin", CancellationToken.None)).Succeeded);

        Assert.False((await administration.CreateAsync(@"LAB\mallory", "a long enough passphrase", AdminRoles.Admin, "admin", CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task Mappings_ResolveNames_RefuseUnknownGroups_AndCannotBeSwitchedOnEmpty()
    {
        await using var factory = new WindowsServerFactory();
        await factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        using var admin = new DashboardClient(factory);
        using (await admin.LoginAsync("admin", Password))
        {
        }

        async Task<string> PostAsync(Dictionary<string, string> fields)
        {
            fields["_handler"] = "windows-sign-in";
            using var response = await admin.PostFormAsync("/administration/users", fields);
            return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        }

        Assert.Contains("Map at least one AD group", await PostAsync(new() { ["WindowsAction"] = "enable" }), StringComparison.Ordinal);
        Assert.Contains("does not know the group", await PostAsync(new() { ["WindowsAction"] = "add", ["WindowsGroup"] = @"LAB\Nobody", ["WindowsRole"] = AdminRoles.Admin }), StringComparison.Ordinal);
        Assert.Contains("The role is invalid", await PostAsync(new() { ["WindowsAction"] = "add", ["WindowsGroup"] = @"LAB\SUSyphos-Operators", ["WindowsRole"] = "God" }), StringComparison.Ordinal);

        var mapped = await PostAsync(new() { ["WindowsAction"] = "add", ["WindowsGroup"] = @"LAB\SUSyphos-Operators", ["WindowsRole"] = AdminRoles.Operator });
        Assert.Contains("S-1-5-21-1-2-3-1101", mapped, StringComparison.Ordinal);
        Assert.Contains("Windows sign-in is on.", await PostAsync(new() { ["WindowsAction"] = "enable" }), StringComparison.Ordinal);

        // Removing the last group switches Windows sign-in off again.
        await PostAsync(new() { ["WindowsAction"] = $"remove:{Operators}" });
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<WindowsSignIn>().IsEnabledAsync(CancellationToken.None));
        Assert.True(await factory.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "WindowsSignIn.GroupMapped", TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task GroupFilteredByUac_IsNamed_InTheMessageAndTheAuditLog()
    {
        const string DomainAdmins = "S-1-5-21-1-2-3-512";
        await using var factory = new WindowsServerFactory();
        await ConfigureAsync(factory, (DomainAdmins, AdminRoles.Admin));
        using var browser = new DashboardClient(factory);

        using (var refused = await WindowsGetAsync(browser, WindowsSignIn.Route, Alice, @"LAB\alice", "S-1-5-21-1-2-3-513", "!" + DomainAdmins))
        {
            Assert.Equal("/login?windows=filteredgroup", refused.Headers.Location?.OriginalString);
        }

        var message = await (await browser.GetAsync("/login?windows=filteredgroup")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("filtered by User Account Control", message, StringComparison.Ordinal);
        var details = await factory.WithDbAsync(db => db.AuditEntries.Where(a => a.Action == "Login.WindowsNoRole").Select(a => a.Details).SingleAsync(TestContext.Current.CancellationToken));
        Assert.Contains("authentication Negotiate, 1 groups in the token", details, StringComparison.Ordinal);
        Assert.Contains("mapped but filtered by UAC (deny only): S-1-5-21-1-2-3-512", details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MappingAPrivilegedGroup_Warns()
    {
        await using var factory = new WindowsServerFactory();
        await factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        using var admin = new DashboardClient(factory);
        using (await admin.LoginAsync("admin", Password))
        {
        }

        using var response = await admin.PostFormAsync("/administration/users", new Dictionary<string, string> { ["_handler"] = "windows-sign-in", ["WindowsAction"] = "add", ["WindowsGroup"] = "S-1-5-21-1-2-3-512", ["WindowsRole"] = AdminRoles.Admin });

        Assert.Contains("Map a dedicated group instead.", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GroupSearch_FindsGroupsForAdministratorsOnly()
    {
        await using var factory = new WindowsServerFactory();
        factory.AddDirectoryGroup(@"LAB\SUSyphos-Admins", "S-1-5-21-1-2-3-1100");
        factory.AddDirectoryGroup(@"LAB\SUSyphos-Operators", "S-1-5-21-1-2-3-1101");
        factory.AddDirectoryGroup(@"LAB\Web servers", "S-1-5-21-1-2-3-1200");
        await factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        await factory.CreateUserAsync("operator", Password, AdminRoles.Operator);

        using var admin = new DashboardClient(factory);
        using (await admin.LoginAsync("admin", Password))
        {
        }

        static async Task<string> SearchAsync(DashboardClient client, string term)
        {
            using var response = await client.GetAsync($"{WindowsSignInEndpoints.GroupSearchRoute}?q={Uri.EscapeDataString(term)}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        }

        var found = await SearchAsync(admin, "susy");
        Assert.Contains(@"LAB\\SUSyphos-Admins", found, StringComparison.Ordinal);
        Assert.Contains("S-1-5-21-1-2-3-1101", found, StringComparison.Ordinal);
        Assert.DoesNotContain("Web servers", found, StringComparison.Ordinal);
        Assert.Contains("Web servers", await SearchAsync(admin, @"LAB\web"), StringComparison.Ordinal);
        Assert.Equal("[]", await SearchAsync(admin, "s"));
        Assert.Equal("[]", await SearchAsync(admin, new string('x', 65)));
        Assert.Equal("[]", await SearchAsync(admin, "su\u0001sy"));

        using var operatorClient = new DashboardClient(factory);
        using (await operatorClient.LoginAsync("operator", Password))
        {
        }

        using var refused = await operatorClient.GetAsync($"{WindowsSignInEndpoints.GroupSearchRoute}?q=susy");
        Assert.NotEqual(HttpStatusCode.OK, refused.StatusCode);
        using var anonymous = await new DashboardClient(factory).GetAsync($"{WindowsSignInEndpoints.GroupSearchRoute}?q=susy");
        Assert.NotEqual(HttpStatusCode.OK, anonymous.StatusCode);
    }

    [Fact]
    public void GroupSearch_EscapesTheTermForLdap() =>
        Assert.Equal(@"a\2a\28b\29\5c", SUSyphos.Server.Approval.LdapDirectoryLookup.EscapeFilterValue(@"a*(b)\"));

    [Theory]
    [InlineData("S-1-5-21-1-2-3-512", true)]
    [InlineData("S-1-5-21-1-2-3-518", true)]
    [InlineData("S-1-5-21-1-2-3-519", true)]
    [InlineData("S-1-5-32-544", true)]
    [InlineData("S-1-5-21-1-2-3-513", false)]
    [InlineData("S-1-5-21-1-2-3-1512", false)]
    [InlineData("S-1-5-32-545", false)]
    public void PrivilegedGroups_AreKnownAsFilteredByUac(string sid, bool expected) =>
        Assert.Equal(expected, WindowsSignIn.IsFilteredByUac(sid));

    [Fact]
    public async Task ConfigurationSwitch_TurnsWindowsSignInOff()
    {
        await using var factory = new WindowsServerFactory { WindowsSwitchInConfiguration = false };
        await ConfigureAsync(factory, (Admins, AdminRoles.Admin));
        using var browser = new DashboardClient(factory);

        using var route = await WindowsGetAsync(browser, WindowsSignIn.Route, Alice, @"LAB\alice", Admins);

        Assert.Equal(HttpStatusCode.NotFound, route.StatusCode);
    }

    [Fact]
    public async Task ForeignReturnUrls_EndOnTheOverview()
    {
        await using var factory = new WindowsServerFactory();
        await ConfigureAsync(factory, (Admins, AdminRoles.Admin));
        using var browser = new DashboardClient(factory);

        using var response = await WindowsGetAsync(browser, $"{WindowsSignIn.Route}?returnUrl=https%3A%2F%2Fevil.example%2F", Alice, @"LAB\alice", Admins);

        Assert.Equal("/", response.Headers.Location?.OriginalString);
    }

    /// <summary>A local admin, the given group mappings and Windows sign-in switched on.</summary>
    private static async Task ConfigureAsync(WindowsServerFactory factory, params (string Sid, string Role)[] mappings)
    {
        await factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        await using var scope = factory.Services.CreateAsyncScope();
        var windows = scope.ServiceProvider.GetRequiredService<WindowsSignIn>();
        foreach (var (sid, role) in mappings)
        {
            Assert.True((await windows.AddMappingAsync(sid, role, "admin", CancellationToken.None)).Succeeded);
        }

        Assert.True((await windows.SetEnabledAsync(true, "admin", CancellationToken.None)).Succeeded);
    }

    private static async Task<HttpResponseMessage> WindowsGetAsync(DashboardClient browser, string path, string sid, string name, params string[] groups)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Add(WindowsServerFactory.Header, $"{sid}|{name}|{string.Join(',', groups)}");
        return await browser.Http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<T> WithUsersAsync<T>(WindowsServerFactory factory, Func<UserManager<IdentityUser>, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>());
    }
}
