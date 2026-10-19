using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using MimeKit;

using UpdateCoordinator.Contracts.Agents;
using UpdateCoordinator.Server.Administration;
using UpdateCoordinator.Server.Data;
using UpdateCoordinator.Server.Notifications;
using UpdateCoordinator.TestSupport;

namespace UpdateCoordinator.Server.Tests;

public sealed class NotificationTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task Events_CoverPendingClonesAlarmsGatesHaltedGroupsAndCertificate()
    {
        await using var factory = new ServerFactory();
        factory.StartServer();
        await SeedConditionsAsync(factory);

        var events = await WithScopeAsync(factory, s => s.GetRequiredService<NotificationEvents>().CollectAsync(CancellationToken.None));

        Assert.Contains(events, e => e.Kind == NotificationKind.ServerPendingApproval && e.Text.Contains("srv01.lab.local", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Kind == NotificationKind.CloneSuspected && e.Text.Contains("srv02", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Kind == NotificationKind.RolloutAlarm && e.Text.Contains("KB5031364", StringComparison.Ordinal) && e.Text.Contains("success rate 40 %", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Kind == NotificationKind.GateWaiting && e.Path!.StartsWith("updates/", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Kind == NotificationKind.LeasesHalted && e.Text.Contains("SQL cluster", StringComparison.Ordinal));

        // The test certificate is valid for one day only, so it is reported as expiring.
        Assert.Contains(events, e => e.Kind == NotificationKind.CertificateExpiring);
        Assert.Equal(events.Count, events.Select(e => e.Key).Distinct().Count());
    }

    [Fact]
    public async Task EachCondition_IsSentOnce_AndAgainAfterItReturned()
    {
        await using var factory = new ServerFactory();
        var channel = new RecordingChannel();
        await EnableAsync(factory, email: true);
        var client = await AddClientAsync(factory, "srv01", RegistrationState.Pending);

        Assert.True(await RunAsync(factory, channel) > 0);
        Assert.Single(channel.Messages);
        Assert.Contains(channel.Messages[0].Events, e => e.Key == $"client.pending:{client}");

        Assert.Equal(0, await RunAsync(factory, channel));
        Assert.Single(channel.Messages);

        await SetStateAsync(factory, client, RegistrationState.Approved);
        Assert.Equal(0, await RunAsync(factory, channel));
        await SetStateAsync(factory, client, RegistrationState.Pending);
        Assert.Equal(1, await RunAsync(factory, channel));
        Assert.Equal(2, channel.Messages.Count);
    }

    [Fact]
    public async Task WithoutChannel_EventsWaitUntilOneIsConfigured()
    {
        await using var factory = new ServerFactory();
        var channel = new RecordingChannel();
        await AddClientAsync(factory, "srv01", RegistrationState.Pending);

        Assert.Equal(0, await RunAsync(factory, channel));
        Assert.Empty(channel.Messages);

        await EnableAsync(factory, email: true);
        Assert.True(await RunAsync(factory, channel) > 0);
    }

    [Fact]
    public async Task FailedDelivery_IsRetried_UnlessAnotherChannelDelivered()
    {
        await using var factory = new ServerFactory();
        await EnableAsync(factory, email: true);
        await AddClientAsync(factory, "srv01", RegistrationState.Pending);
        var failing = new RecordingChannel { Fail = true };

        Assert.Equal(0, await RunAsync(factory, failing));
        Assert.Equal(0, await RunAsync(factory, failing));
        Assert.Equal(2, failing.Attempts);

        var working = new RecordingChannel { ChannelName = "Webhook" };
        Assert.True(await RunAsync(factory, failing, working) > 0);
        Assert.Equal(0, await RunAsync(factory, failing, working));
    }

    [Fact]
    public async Task Email_GoesToEveryRecipient_WithLinksToTheDashboard()
    {
        var transport = new RecordingTransport();
        var settings = ValidSettings();
        var message = new NotificationMessage("UpdateCoordinator: test", [new NotificationEvent("k", NotificationKind.ServerPendingApproval, "New server waiting for approval: srv01", "approvals")], new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc));

        await new EmailChannel(transport).SendAsync(message, settings, new NotificationSecrets("smtp-secret", null), CancellationToken.None);

        var mail = Assert.Single(transport.Sent);
        Assert.Equal(["ops@lab.local", "lead@lab.local"], mail.To.Mailboxes.Select(m => m.Address));
        Assert.Equal("uc01@lab.local", mail.From.Mailboxes.Single().Address);
        Assert.Contains("https://uc01.lab.local:8443/approvals", mail.TextBody, StringComparison.Ordinal);
        Assert.Equal("smtp-secret", transport.Password);
    }

    [Fact]
    public async Task Webhook_PostsSignedJson_AndFailsOnErrorStatus()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        var settings = ValidSettings();
        settings.WebhookEnabled = true;
        settings.WebhookUrl = "https://hooks.lab.local/uc";
        var message = new NotificationMessage("UpdateCoordinator: 1 new event", [new NotificationEvent("group.halted:1", NotificationKind.LeasesHalted, "Installations stopped", "administration/groups")], DateTime.UtcNow);

        await new WebhookChannel(http).SendAsync(message, settings, new NotificationSecrets(null, "hook-secret"), CancellationToken.None);

        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal("LeasesHalted", json.RootElement.GetProperty("events")[0].GetProperty("kind").GetString());
        Assert.Equal("https://uc01.lab.local:8443/administration/groups", json.RootElement.GetProperty("events")[0].GetProperty("link").GetString());
        var expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData("hook-secret"u8.ToArray(), handler.Body!));
        Assert.Equal(expected, handler.Signature);

        using var failingHttp = new HttpClient(new RecordingHandler(HttpStatusCode.InternalServerError));
        await Assert.ThrowsAsync<HttpRequestException>(() => new WebhookChannel(failingHttp).SendAsync(message, settings, new NotificationSecrets(null, null), CancellationToken.None));
    }

    public static TheoryData<string, string> InvalidSettings => new()
    {
        { "SmtpHost", "mail.lab.local; rm -rf" },
        { "SmtpHost", "" },
        { "SmtpPort", "0" },
        { "SmtpPort", "70000" },
        { "From", "not an address" },
        { "From", "a@b.c\r\nBcc: x@evil" },
        { "Recipients", "" },
        { "Recipients", "ops@lab.local, broken" },
        { "Recipients", string.Join(",", Enumerable.Range(0, 21).Select(i => $"user{i}@lab.local")) },
        { "WebhookUrl", "http://hooks.lab.local/uc" },
        { "WebhookUrl", "https://user:pass@hooks.lab.local/uc" },
        { "WebhookUrl", "not a url" },
        { "DashboardUrl", "javascript:alert(1)" },
    };

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public void Settings_RejectInvalidInput(string field, string value)
    {
        var settings = ValidSettings();
        settings.WebhookEnabled = true;
        settings.WebhookUrl = "https://hooks.lab.local/uc";
        switch (field)
        {
            case "SmtpHost": settings.SmtpHost = value; break;
            case "SmtpPort": settings.SmtpPort = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
            case "From": settings.From = value; break;
            case "Recipients": settings.Recipients = value; break;
            case "WebhookUrl": settings.WebhookUrl = value; break;
            case "DashboardUrl": settings.DashboardUrl = value; break;
        }

        Assert.False(Validator.TryValidateObject(settings, new ValidationContext(settings), [], validateAllProperties: true));
    }

    [Fact]
    public void Settings_AcceptValidInput_AndDisabledChannelsNeedNothing()
    {
        var valid = ValidSettings();
        Assert.True(Validator.TryValidateObject(valid, new ValidationContext(valid), [], validateAllProperties: true));
        var empty = new NotificationSettings();
        Assert.True(Validator.TryValidateObject(empty, new ValidationContext(empty), [], validateAllProperties: true));
    }

    [Fact]
    public async Task Secrets_AreKeptReplacedOrRemoved_OutsideTheDatabase()
    {
        await using var factory = new ServerFactory();
        factory.StartServer();
        await WithScopeAsync(factory, async s =>
        {
            var store = s.GetRequiredService<NotificationSettingsStore>();
            await store.SaveAsync(ValidSettings(), "first", "hook", CancellationToken.None);
            await store.SaveAsync(ValidSettings(), null, null, CancellationToken.None);
            Assert.Equal(new NotificationSecrets("first", "hook"), store.LoadSecrets());

            await store.SaveAsync(ValidSettings(), "second", string.Empty, CancellationToken.None);
            Assert.Equal(new NotificationSecrets("second", null), store.LoadSecrets());
            return 0;
        });

        var stored = await factory.WithDbAsync(db => db.Settings.SingleAsync(s => s.Key == NotificationSettingsStore.SettingKey, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("second", stored.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Page_NeedsAdministrator_ValidatesAndSaves()
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("operator", Password, AdminRoles.Operator);
        await factory.CreateUserAsync("admin", Password, AdminRoles.Admin);

        using (var operatorBrowser = new DashboardClient(factory))
        {
            using (await operatorBrowser.LoginAsync("operator", Password))
            {
            }

            using var refused = await operatorBrowser.GetAsync("/administration/notifications");
            Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        }

        using var admin = new DashboardClient(factory);
        using (await admin.LoginAsync("admin", Password))
        {
        }

        using (var invalid = await PostAsync(admin, new() { ["Action"] = "save", ["EmailEnabled"] = "yes", ["SmtpHost"] = "mail.lab.local", ["SmtpPort"] = "587", ["SecurityMode"] = "StartTls", ["From"] = "broken", ["Recipients"] = "ops@lab.local" }))
        {
            Assert.Contains("valid sender address", await invalid.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        using (var test = await PostAsync(admin, new() { ["Action"] = "test" }))
        {
            Assert.Contains("Enable e-mail or the webhook first.", await test.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        using (var saved = await PostAsync(admin, new() { ["Action"] = "save", ["EmailEnabled"] = "yes", ["SmtpHost"] = "mail.lab.local", ["SmtpPort"] = "25", ["SecurityMode"] = "None", ["From"] = "uc01@lab.local", ["Recipients"] = "ops@lab.local", ["SmtpPassword"] = "relay-password" }))
        {
            Assert.Contains("Notification settings saved.", await saved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        var settings = await WithScopeAsync(factory, s => s.GetRequiredService<NotificationSettingsStore>().LoadAsync(CancellationToken.None));
        Assert.True(settings.EmailEnabled);
        Assert.Equal(SmtpSecurity.None, settings.SmtpSecurity);
        Assert.Equal(25, settings.SmtpPort);
        Assert.True(await factory.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Action == "Notifications.Changed", TestContext.Current.CancellationToken)));
    }

    private static Task<HttpResponseMessage> PostAsync(DashboardClient client, Dictionary<string, string> fields)
    {
        fields["_handler"] = "notifications";
        return client.PostFormAsync("/administration/notifications", fields);
    }

    private static NotificationSettings ValidSettings() => new()
    {
        EmailEnabled = true,
        SmtpHost = "mail.lab.local",
        SmtpPort = 587,
        From = "uc01@lab.local",
        Recipients = "ops@lab.local; lead@lab.local",
        DashboardUrl = "https://uc01.lab.local:8443/",
    };

    private static async Task EnableAsync(ServerFactory factory, bool email)
    {
        factory.StartServer();
        var settings = ValidSettings();
        settings.EmailEnabled = email;
        await WithScopeAsync(factory, async s =>
        {
            await s.GetRequiredService<NotificationSettingsStore>().SaveAsync(settings, null, null, CancellationToken.None);
            return 0;
        });
    }

    private static Task<int> RunAsync(ServerFactory factory, params INotificationChannel[] channels)
    {
        factory.StartServer();
        return WithScopeAsync(factory, s => new NotificationService(
                s.GetRequiredService<ServerDbContext>(),
                s.GetRequiredService<NotificationEvents>(),
                s.GetRequiredService<NotificationSettingsStore>(),
                channels,
                s.GetRequiredService<AuditLog>(),
                TimeProvider.System,
                NullLogger<NotificationService>.Instance)
            .RunAsync(CancellationToken.None));
    }

    private static async Task<T> WithScopeAsync<T>(ServerFactory factory, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    private static async Task<Guid> AddClientAsync(ServerFactory factory, string host, RegistrationState state, string? domain = null)
    {
        factory.StartServer();
        var id = Guid.NewGuid();
        await factory.WithDbAsync(async db =>
        {
            db.Clients.Add(new ClientEntity { Id = id, SecretHash = [1], HostName = host, DomainName = domain, OsVersion = "10.0.20348.1", AgentVersion = "0.2.0", State = state });
            return await db.SaveChangesAsync();
        });
        return id;
    }

    private static Task<int> SetStateAsync(ServerFactory factory, Guid id, RegistrationState state) =>
        factory.WithDbAsync(async db =>
        {
            (await db.Clients.SingleAsync(c => c.Id == id)).State = state;
            return await db.SaveChangesAsync();
        });

    private static async Task SeedConditionsAsync(ServerFactory factory)
    {
        await AddClientAsync(factory, "srv01", RegistrationState.Pending, "lab.local");
        await AddClientAsync(factory, "srv02", RegistrationState.Blocked);
        await factory.WithDbAsync(async db =>
        {
            var alarm = Guid.NewGuid();
            var gate = Guid.NewGuid();
            db.Updates.Add(new UpdateEntity { UpdateId = alarm, Revision = 1, KbArticle = "KB5031364", Title = "2026-10 Cumulative Update" });
            db.Updates.Add(new UpdateEntity { UpdateId = gate, Revision = 1, Title = "Defender definitions" });
            db.Rollouts.Add(new RolloutEntity { UpdateId = alarm, Revision = 1, StartedBy = "system" });
            db.Rollouts.Add(new RolloutEntity { UpdateId = gate, Revision = 1, StartedBy = "system" });
            db.RolloutRings.Add(new RolloutRingEntity { UpdateId = alarm, Revision = 1, RingId = RingEntity.TestId, State = RingRolloutState.Alarm, Reason = "success rate 40 %" });
            db.RolloutRings.Add(new RolloutRingEntity { UpdateId = gate, Revision = 1, RingId = RingEntity.ProductionId, State = RingRolloutState.AwaitingApproval });
            db.Groups.Add(new GroupEntity { Name = "SQL cluster", LeasesHalted = true });
            return await db.SaveChangesAsync();
        });
    }

    private sealed class RecordingChannel : INotificationChannel
    {
        public string ChannelName { get; init; } = "E-mail";

        public bool Fail { get; init; }

        public int Attempts { get; private set; }

        public List<NotificationMessage> Messages { get; } = [];

        public string Name => ChannelName;

        public bool IsEnabled(NotificationSettings settings) => settings.EmailEnabled;

        public Task SendAsync(NotificationMessage message, NotificationSettings settings, NotificationSecrets secrets, CancellationToken cancellationToken)
        {
            Attempts++;
            if (Fail)
            {
                throw new IOException("mail server unreachable");
            }

            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingTransport : IMailTransport
    {
        public List<MimeMessage> Sent { get; } = [];

        public string? Password { get; private set; }

        public Task SendAsync(MimeMessage message, NotificationSettings settings, string? password, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            Password = password;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public byte[]? Body { get; private set; }

        public string? Signature { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            Signature = request.Headers.TryGetValues(WebhookChannel.SignatureHeader, out var values) ? values.Single() : null;
            return new HttpResponseMessage(status) { Content = new StringContent(Encoding.UTF8.GetString(Body)) };
        }
    }
}
