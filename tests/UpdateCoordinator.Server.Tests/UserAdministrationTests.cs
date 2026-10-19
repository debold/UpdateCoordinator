using System.Net;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using UpdateCoordinator.Server.Administration;
using UpdateCoordinator.TestSupport;

namespace UpdateCoordinator.Server.Tests;

public sealed class UserAdministrationTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task Admin_CreatesAccount_WhichCanSignIn()
    {
        await using var factory = new ServerFactory();
        using var browser = await LoginAsync(factory, "admin", AdminRoles.Admin);

        using (var created = await browser.PostFormAsync("/administration/users", new Dictionary<string, string>
        {
            ["Action"] = "create",
            ["NewName"] = "operator1",
            ["NewPassword"] = "another long passphrase",
            ["NewRole"] = AdminRoles.Operator,
        }))
        {
            Assert.Contains("The account was created.", await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        using var other = new DashboardClient(factory);
        using var login = await other.LoginAsync("operator1", "another long passphrase");
        using var approvals = await other.GetAsync("/approvals");
        Assert.Equal(HttpStatusCode.OK, approvals.StatusCode);
        Assert.True(await factory.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Action == "User.Created" && e.Target == "operator1", TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("valid.name", "short", "Operator")]
    [InlineData("bad name!", "long enough passphrase", "Operator")]
    [InlineData("valid.name", "long enough passphrase", "Superuser")]
    [InlineData("", "long enough passphrase", "Reader")]
    public async Task InvalidAccounts_AreRefused(string name, string password, string role)
    {
        await using var factory = new ServerFactory();

        var outcome = await WithUsersAsync(factory, u => u.CreateAsync(name, password, role, "admin", CancellationToken.None));

        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public async Task DuplicateAccount_IsRefused()
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("alice", Password, AdminRoles.Reader);

        Assert.False((await WithUsersAsync(factory, u => u.CreateAsync("ALICE", Password, AdminRoles.Reader, "admin", CancellationToken.None))).Succeeded);
    }

    [Fact]
    public async Task LastAdministrator_CannotBeDemotedLockedOrDeleted()
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        var id = await IdAsync(factory, "admin");

        Assert.False((await WithUsersAsync(factory, u => u.SetRoleAsync(id, AdminRoles.Reader, "other", CancellationToken.None))).Succeeded);
        Assert.False((await WithUsersAsync(factory, u => u.SetLockedAsync(id, true, "other", CancellationToken.None))).Succeeded);
        Assert.False((await WithUsersAsync(factory, u => u.DeleteAsync(id, "other", CancellationToken.None))).Succeeded);

        await factory.CreateUserAsync("second", Password, AdminRoles.Admin);
        Assert.True((await WithUsersAsync(factory, u => u.SetRoleAsync(id, AdminRoles.Reader, "second", CancellationToken.None))).Succeeded);
    }

    [Fact]
    public async Task OwnAccount_CannotBeLockedOrDeleted()
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        await factory.CreateUserAsync("second", Password, AdminRoles.Admin);
        var id = await IdAsync(factory, "admin");

        Assert.False((await WithUsersAsync(factory, u => u.SetLockedAsync(id, true, "admin", CancellationToken.None))).Succeeded);
        Assert.False((await WithUsersAsync(factory, u => u.DeleteAsync(id, "admin", CancellationToken.None))).Succeeded);
    }

    [Fact]
    public async Task LockedAccount_CannotSignIn_UntilUnlocked()
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        await factory.CreateUserAsync("bob", Password, AdminRoles.Reader);
        var id = await IdAsync(factory, "bob");

        Assert.True((await WithUsersAsync(factory, u => u.SetLockedAsync(id, true, "admin", CancellationToken.None))).Succeeded);
        using (var browser = new DashboardClient(factory))
        {
            using (await browser.LoginAsync("bob", Password))
            {
            }

            using var page = await browser.GetAsync("/");
            Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        }

        Assert.True((await WithUsersAsync(factory, u => u.SetLockedAsync(id, false, "admin", CancellationToken.None))).Succeeded);
        using var unlocked = new DashboardClient(factory);
        using (await unlocked.LoginAsync("bob", Password))
        {
        }

        using var overview = await unlocked.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
    }

    [Fact]
    public async Task PasswordReset_ReplacesThePassword_AndChecksTheRules()
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("bob", Password, AdminRoles.Reader);
        var id = await IdAsync(factory, "bob");

        Assert.False((await WithUsersAsync(factory, u => u.ResetPasswordAsync(id, "short", "admin", CancellationToken.None))).Succeeded);
        Assert.True((await WithUsersAsync(factory, u => u.ResetPasswordAsync(id, "a brand new passphrase", "admin", CancellationToken.None))).Succeeded);

        using var browser = new DashboardClient(factory);
        using (await browser.LoginAsync("bob", "a brand new passphrase"))
        {
        }

        using var overview = await browser.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
    }

    [Theory]
    [InlineData("role:unknown-id")]
    [InlineData("delete:")]
    [InlineData("explode")]
    public async Task MalformedOrUnknownRequests_AreRefused(string action)
    {
        await using var factory = new ServerFactory();
        using var browser = await LoginAsync(factory, "admin", AdminRoles.Admin);

        using var response = await browser.PostFormAsync("/administration/users", new Dictionary<string, string> { ["Action"] = action });

        Assert.Contains("role=\"alert\"", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserPage_IsForAdministratorsOnly()
    {
        await using var factory = new ServerFactory();
        using var browser = await LoginAsync(factory, "operator", AdminRoles.Operator);

        using var response = await browser.GetAsync("/administration/users");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task<DashboardClient> LoginAsync(ServerFactory factory, string name, string role)
    {
        await factory.CreateUserAsync(name, Password, role);
        var browser = new DashboardClient(factory);
        using (await browser.LoginAsync(name, Password))
        {
        }

        return browser;
    }

    private static async Task<Outcome> WithUsersAsync(ServerFactory factory, Func<UserAdministration, Task<Outcome>> action)
    {
        factory.StartServer();
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<UserAdministration>());
    }

    private static async Task<string> IdAsync(ServerFactory factory, string name)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>().FindByNameAsync(name))!.Id;
    }
}
