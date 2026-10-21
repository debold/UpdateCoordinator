using System.ComponentModel.DataAnnotations;
using System.DirectoryServices.Protocols;
using System.Net;

using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Approval;
using SUSyphos.Server.Data;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

public sealed class ManualApprovalTests
{
    private const string Password = "correct horse battery";

    [Theory]
    [InlineData("approve", RegistrationState.Approved, "Client.Approved")]
    [InlineData("reject", RegistrationState.Rejected, "Client.Rejected")]
    public async Task Operator_DecidesPendingClient(string decision, RegistrationState expected, string auditAction)
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("operator", Password, AdminRoles.Operator);
        using var browser = new DashboardClient(factory);
        var credentials = await RegisterTests.RegisterAsync(browser.Http);
        using (await browser.LoginAsync("operator", Password))
        {
        }

        using var response = await PostDecisionAsync(browser, credentials.ClientId.ToString(), decision);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, await StateAsync(factory, credentials.ClientId));
        Assert.True(await factory.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Actor == "operator" && e.Action == auditAction, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task PendingPage_ListsWaitingServers()
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("operator", Password, AdminRoles.Operator);
        using var browser = new DashboardClient(factory);
        await RegisterTests.RegisterAsync(browser.Http);
        using (await browser.LoginAsync("operator", Password))
        {
        }

        using var page = await browser.GetAsync("/approvals");

        Assert.Contains("SRV01", await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reader_CannotOpenOrUseApprovals()
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("reader", Password, AdminRoles.Reader);
        using var browser = new DashboardClient(factory);
        var credentials = await RegisterTests.RegisterAsync(browser.Http);
        using (await browser.LoginAsync("reader", Password))
        {
        }

        using var page = await browser.GetAsync("/approvals");
        using var post = await PostDecisionAsync(browser, credentials.ClientId.ToString(), "approve", pageToRead: "/");

        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, post.StatusCode);
        Assert.Equal(RegistrationState.Pending, await StateAsync(factory, credentials.ClientId));
    }

    [Fact]
    public async Task AlreadyDecidedClient_IsNotChangedAgain()
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("operator", Password, AdminRoles.Operator);
        using var browser = new DashboardClient(factory);
        var credentials = await RegisterTests.RegisterAsync(browser.Http);
        using (await browser.LoginAsync("operator", Password))
        {
        }

        using (await PostDecisionAsync(browser, credentials.ClientId.ToString(), "reject"))
        {
        }

        using var response = await PostDecisionAsync(browser, credentials.ClientId.ToString(), "approve");

        Assert.Contains("already decided", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(RegistrationState.Rejected, await StateAsync(factory, credentials.ClientId));
    }

    [Theory]
    [InlineData("not-a-guid", "approve")]
    [InlineData("6f1f6a5e-2a4b-4e0e-9a59-1c8e1d6c2f10", "delete")]
    [InlineData("6f1f6a5e-2a4b-4e0e-9a59-1c8e1d6c2f10", "")]
    public async Task InvalidDecision_ChangesNothing(string clientId, string decision)
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("operator", Password, AdminRoles.Operator);
        using var browser = new DashboardClient(factory);
        var credentials = await RegisterTests.RegisterAsync(browser.Http);
        using (await browser.LoginAsync("operator", Password))
        {
        }

        using var response = await PostDecisionAsync(browser, clientId, decision);

        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(RegistrationState.Pending, await StateAsync(factory, credentials.ClientId));
    }

    [Fact]
    public async Task UnknownClient_IsReported()
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("operator", Password, AdminRoles.Operator);
        using var browser = new DashboardClient(factory);
        await RegisterTests.RegisterAsync(browser.Http);
        using (await browser.LoginAsync("operator", Password))
        {
        }

        using var response = await PostDecisionAsync(browser, Guid.NewGuid().ToString(), "approve");

        Assert.Contains("no longer exists", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    private static Task<HttpResponseMessage> PostDecisionAsync(DashboardClient browser, string clientId, string decision, string? pageToRead = null) =>
        // The antiforgery token comes from the layout form; the handler name selects the decision form.
        browser.PostFormAsync("/approvals", new Dictionary<string, string> { ["_handler"] = "decision", ["Action"] = $"{decision}:{clientId}" }, pageToRead ?? "/");

    private static Task<RegistrationState> StateAsync(ServerFactory factory, Guid clientId) =>
        factory.WithDbAsync(db => db.Clients.Where(c => c.Id == clientId).Select(c => c.State).SingleAsync(TestContext.Current.CancellationToken));
}

internal sealed class FakeDirectory : IDirectoryLookup
{
    public ComputerAccountState State { get; set; } = ComputerAccountState.Enabled;

    public Exception? Failure { get; set; }

    public Task<ComputerAccountState> FindComputerAsync(string domain, string hostName, CancellationToken cancellationToken) =>
        Failure is null ? Task.FromResult(State) : Task.FromException<ComputerAccountState>(Failure);

    public List<DirectoryComputer> Servers { get; } = [];

    public Task<IReadOnlyList<DirectoryComputer>> ListServersAsync(string domain, CancellationToken cancellationToken) =>
        Failure is null ? Task.FromResult<IReadOnlyList<DirectoryComputer>>(Servers) : Task.FromException<IReadOnlyList<DirectoryComputer>>(Failure);

    public List<DirectoryGroup> Groups { get; } = [];

    public Task<IReadOnlyList<DirectoryGroup>> FindGroupsAsync(string domain, string term, int max, CancellationToken cancellationToken) =>
        Failure is null ? Task.FromResult<IReadOnlyList<DirectoryGroup>>([.. Groups.Where(g => g.Name.Contains(term, StringComparison.OrdinalIgnoreCase)).Take(max)]) : Task.FromException<IReadOnlyList<DirectoryGroup>>(Failure);
}

internal sealed class FakeDns : IDnsLookup
{
    public List<IPAddress> Addresses { get; } = [IPAddress.Parse("10.0.0.5")];

    public Task<IReadOnlyList<IPAddress>> ResolveAsync(string hostName, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<IPAddress>>(Addresses);
}

public sealed class RuleBasedApproverTests
{
    private readonly FakeDirectory _directory = new();
    private readonly FakeDns _dns = new();

    [Fact]
    public async Task VerifiedDomainMember_IsApproved()
    {
        Assert.Equal(RuleVerdict.Approve, await EvaluateAsync(Client()));
    }

    [Fact]
    public async Task DomainComparison_IsCaseInsensitive()
    {
        Assert.Equal(RuleVerdict.Approve, await EvaluateAsync(Client(domain: "EXAMPLE.LOCAL")));
    }

    [Fact]
    public async Task Ipv4MappedSource_MatchesIpv4Record()
    {
        Assert.Equal(RuleVerdict.Approve, await EvaluateAsync(Client(address: "::ffff:10.0.0.5")));
    }

    [Fact]
    public async Task WithoutConfiguredDomains_NothingIsApproved()
    {
        Assert.Equal(RuleVerdict.NotApplicable, await EvaluateAsync(Client(), domains: []));
    }

    [Fact]
    public async Task WorkgroupServer_IsNotApplicable()
    {
        Assert.Equal(RuleVerdict.NotApplicable, await EvaluateAsync(Client(domain: null)));
    }

    [Fact]
    public async Task OtherDomain_IsNotApproved()
    {
        Assert.Equal(RuleVerdict.DomainNotAllowed, await EvaluateAsync(Client(domain: "evil.local")));
    }

    [Theory]
    [InlineData((int)ComputerAccountState.NotFound, (int)RuleVerdict.ComputerNotFound)]
    [InlineData((int)ComputerAccountState.Disabled, (int)RuleVerdict.ComputerDisabled)]
    public async Task MissingOrDisabledComputerAccount_IsNotApproved(int state, int expected)
    {
        _directory.State = (ComputerAccountState)state;

        Assert.Equal((RuleVerdict)expected, await EvaluateAsync(Client()));
    }

    [Theory]
    [InlineData("10.0.0.99")]
    [InlineData("not-an-ip")]
    public async Task SourceAddressNotInDns_IsNotApproved(string address)
    {
        Assert.Equal(RuleVerdict.AddressMismatch, await EvaluateAsync(Client(address: address)));
    }

    [Fact]
    public async Task DirectoryOutage_Throws()
    {
        _directory.Failure = new LdapException("server down");

        await Assert.ThrowsAsync<LdapException>(() => EvaluateAsync(Client()));
    }

    private static ClientEntity Client(string? domain = "example.local", string address = "10.0.0.5") => new()
    {
        Id = Guid.NewGuid(),
        SecretHash = new byte[32],
        HostName = "SRV01",
        DomainName = domain,
        OsVersion = "10.0.20348.1",
        AgentVersion = "1.0.0",
        RegisteredFromAddress = address,
    };

    private Task<RuleVerdict> EvaluateAsync(ClientEntity client, List<string>? domains = null) =>
        new RuleBasedApprover(_directory, _dns).EvaluateAsync(client, domains ?? ["example.local"], TestContext.Current.CancellationToken);
}

public sealed class RuleBasedApprovalWorkerTests
{
    [Fact]
    public async Task PendingDomainMember_IsApprovedByRuleAndAudited()
    {
        var directory = new FakeDirectory();
        await using var factory = CreateFactory(directory);
        var clientId = await AddPendingClientAsync(factory, "127.0.0.1");

        await RunWorkerAsync(factory);

        Assert.Equal(RegistrationState.Approved, await WithDbAsync(factory, db => db.Clients.Where(c => c.Id == clientId).Select(c => c.State).SingleAsync(TestContext.Current.CancellationToken)));
        Assert.True(await WithDbAsync(factory, db => db.AuditEntries.AnyAsync(e => e.Actor == ClientApprovalService.RuleActor && e.Action == "Client.Approved", TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task DirectoryOutage_LeavesClientPending()
    {
        var directory = new FakeDirectory { Failure = new LdapException("server down") };
        await using var factory = CreateFactory(directory);
        var clientId = await AddPendingClientAsync(factory, "127.0.0.1");

        await RunWorkerAsync(factory);

        Assert.Equal(RegistrationState.Pending, await WithDbAsync(factory, db => db.Clients.Where(c => c.Id == clientId).Select(c => c.State).SingleAsync(TestContext.Current.CancellationToken)));
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> CreateFactory(FakeDirectory directory)
    {
        var dns = new FakeDns();
        dns.Addresses.Add(IPAddress.Loopback);
        return new ServerFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Approval:AutoApproveDomains:0", "example.local");
            builder.UseSetting("Approval:CheckInterval", "01:00:00");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IDirectoryLookup>(directory);
                services.AddSingleton<IDnsLookup>(dns);
            });
        });
    }

    private static async Task<Guid> AddPendingClientAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, string address)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
        var client = new ClientEntity
        {
            Id = Guid.NewGuid(),
            SecretHash = new byte[32],
            HostName = "SRV01",
            DomainName = "example.local",
            OsVersion = "10.0.20348.1",
            AgentVersion = "1.0.0",
            RegisteredFromAddress = address,
            State = RegistrationState.Pending,
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return client.Id;
    }

    private static async Task<T> WithDbAsync<T>(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, Func<ServerDbContext, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<ServerDbContext>());
    }

    private static async Task RunWorkerAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        var worker = factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<RuleBasedApprovalWorker>().Single();
        await worker.RunOnceAsync(TestContext.Current.CancellationToken);
    }
}

public sealed class ApprovalOptionsTests
{
    [Theory]
    [InlineData("example..local")]
    [InlineData("-bad.local")]
    [InlineData("")]
    public void InvalidDomain_IsRejected(string domain)
    {
        var options = new ApprovalOptions { AutoApproveDomains = [domain] };
        var results = new List<ValidationResult>();

        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);

        Assert.NotEmpty(results);
    }

    [Fact]
    public void ValidDomain_IsAccepted()
    {
        var options = new ApprovalOptions { AutoApproveDomains = ["corp.example.local"] };
        var results = new List<ValidationResult>();

        Assert.True(Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true));
    }
}

public sealed class LdapFilterTests
{
    [Theory]
    [InlineData("SRV01$", "SRV01$")]
    [InlineData("a*b", "a\\2ab")]
    [InlineData("a(b)c", "a\\28b\\29c")]
    [InlineData("a\\b", "a\\5cb")]
    [InlineData("*)(objectClass=*", "\\2a\\29\\28objectClass=\\2a")]
    public void Values_AreEscaped(string input, string expected)
    {
        Assert.Equal(expected, LdapDirectoryLookup.EscapeFilterValue(input));
    }
}
