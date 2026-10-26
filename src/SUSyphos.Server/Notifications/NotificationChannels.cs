using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using MailKit.Security;

using MimeKit;

namespace SUSyphos.Server.Notifications;

/// <summary>
/// One notification: the new events since the last one, with what the e-mail header and footer show (central server,
/// version, dashboard address for links).
/// </summary>
internal sealed record NotificationMessage(string Subject, IReadOnlyList<NotificationEvent> Events, DateTime CreatedAtUtc)
{
    /// <summary>Base address for links; null leaves them out.</summary>
    public string? DashboardUrl { get; init; }

    /// <summary>Name of the central server in header and footer.</summary>
    public string ServerName { get; init; } = Administration.ServerIdentity.MachineFqdn;

    public string ServerVersion { get; init; } = Administration.ServerVersion.Display;

    /// <summary>The most important category among the events, for the header tag.</summary>
    public NotificationCategory Category => Events.Count == 0 ? NotificationCategory.Optional : Events.Min(e => NotificationCatalog.Of(e.Kind).Category);

    /// <summary>Subject with the prefix: the event itself, or how many events and the most important category.</summary>
    public static NotificationMessage Create(IReadOnlyList<NotificationEvent> events, DateTime createdAtUtc, string? prefix, string? dashboardUrl)
    {
        ArgumentNullException.ThrowIfNull(events);
        var subject = events.Count == 1
            ? events[0].Text
            : $"{events.Count} new events ({NotificationCatalog.Severity(events.Min(e => NotificationCatalog.Of(e.Kind).Category)).ToLowerInvariant()})";
        subject = (prefix ?? string.Empty) + subject;
        return new NotificationMessage(subject.Length <= 200 ? subject : subject[..199] + "…", events, createdAtUtc) { DashboardUrl = dashboardUrl };
    }

    /// <summary>Plain text body, also the alternative part of the HTML e-mail and what filter rules see.</summary>
    public string FormatText()
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{NotificationCatalog.Severity(Category)} · {ServerName} · {SUSyphos.Server.Components.DisplayTime.Format(CreatedAtUtc)} ({SUSyphos.Server.Components.DisplayTime.Zone.StandardName})").AppendLine().AppendLine();
        foreach (var item in Events)
        {
            var info = NotificationCatalog.Of(item.Kind);
            text.Append(CultureInfo.InvariantCulture, $"[{NotificationCatalog.Severity(info.Category)}] {info.Label}").AppendLine();
            text.AppendLine(item.Text);
            if (item.Affected is { } affected)
            {
                text.Append("Affected: ").AppendLine(affected);
            }

            if (item.Impact is { } impact)
            {
                text.Append("Impact: ").AppendLine(impact);
            }

            if (Link(DashboardUrl, item.Path) is { } link)
            {
                text.AppendLine(link);
            }

            text.AppendLine();
        }

        text.Append(CultureInfo.InvariantCulture, $"SUSyphos {ServerVersion} on {ServerName}");
        if (DashboardUrl is { Length: > 0 } dashboard)
        {
            text.Append(" · ").Append(dashboard).AppendLine().Append("Notification settings: ").Append(Link(dashboard, "administration/notifications"));
        }

        return text.ToString();
    }

    public static string? Link(string? dashboardUrl, string? path) =>
        string.IsNullOrWhiteSpace(dashboardUrl) || path is null ? null : $"{dashboardUrl.TrimEnd('/')}/{path}";
}

/// <summary>A way to deliver notifications; throws when delivery fails, so the events are sent again on the next run.</summary>
internal interface INotificationChannel
{
    string Name { get; }

    bool IsEnabled(NotificationSettings settings);

    /// <summary>The event kinds this channel sends.</summary>
    IReadOnlyCollection<NotificationKind> Kinds(NotificationSettings settings);

    /// <summary>Delivers the message; returns what the receiver answered, e.g. "HTTP 202 Accepted", for the test.</summary>
    Task<string> SendAsync(NotificationMessage message, NotificationSettings settings, NotificationSecrets secrets, CancellationToken cancellationToken);
}

/// <summary>Sends a built e-mail; separate so tests never contact a mail server.</summary>
internal interface IMailTransport
{
    /// <summary>Sends the e-mail; returns the answer of the mail server.</summary>
    Task<string> SendAsync(MimeMessage message, NotificationSettings settings, string? password, CancellationToken cancellationToken);
}

/// <summary>SMTP through MailKit; the certificate of the mail server is validated as usual.</summary>
internal sealed class SmtpMailTransport : IMailTransport
{
    public async Task<string> SendAsync(MimeMessage message, NotificationSettings settings, string? password, CancellationToken cancellationToken)
    {
        using var client = new MailKit.Net.Smtp.SmtpClient { Timeout = 30_000 };
        var security = settings.SmtpSecurity switch
        {
            SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            SmtpSecurity.None => SecureSocketOptions.None,
            _ => SecureSocketOptions.StartTls,
        };
        await client.ConnectAsync(settings.SmtpHost ?? throw new InvalidOperationException("No SMTP server is configured."), settings.SmtpPort, security, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(settings.SmtpUser))
        {
            await client.AuthenticateAsync(settings.SmtpUser, password ?? string.Empty, cancellationToken).ConfigureAwait(false);
        }

        var answer = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);
        return answer;
    }
}

internal sealed class EmailChannel(IMailTransport transport) : INotificationChannel
{
    public string Name => "E-mail";

    public bool IsEnabled(NotificationSettings settings) => settings.EmailEnabled;

    public IReadOnlyCollection<NotificationKind> Kinds(NotificationSettings settings) => settings.EmailKinds;

    public async Task<string> SendAsync(NotificationMessage message, NotificationSettings settings, NotificationSecrets secrets, CancellationToken cancellationToken)
    {
        var mail = new MimeMessage { Subject = message.Subject };
        mail.From.Add(MailboxAddress.Parse(settings.From ?? throw new InvalidOperationException("No sender address is configured.")));
        foreach (var recipient in settings.RecipientList)
        {
            mail.To.Add(MailboxAddress.Parse(recipient));
        }

        mail.Body = new BodyBuilder { TextBody = message.FormatText(), HtmlBody = NotificationEmail.Html(message) }.ToMessageBody();
        var answer = await transport.SendAsync(mail, settings, secrets.SmtpPassword, cancellationToken).ConfigureAwait(false);
        return $"accepted by {settings.SmtpHost} for {settings.RecipientList.Count} recipients{(string.IsNullOrWhiteSpace(answer) ? string.Empty : $" ({answer.Trim()})")}";
    }
}

/// <summary>
/// Generic webhook: POST of a JSON document. With a secret, the header <c>X-SUSyphos-Signature</c> carries
/// <c>sha256=</c> and the hex HMAC-SHA256 of the body, so the receiver can verify the sender.
/// </summary>
internal sealed class WebhookChannel(HttpClient http) : INotificationChannel
{
    public const string SignatureHeader = "X-SUSyphos-Signature";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public string Name => "Webhook";

    public bool IsEnabled(NotificationSettings settings) => settings.WebhookEnabled;

    public IReadOnlyCollection<NotificationKind> Kinds(NotificationSettings settings) => settings.WebhookKinds;

    public async Task<string> SendAsync(NotificationMessage message, NotificationSettings settings, NotificationSecrets secrets, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(settings.WebhookUrl))
        {
            throw new InvalidOperationException("No webhook address is saved.");
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                message.Subject,
                message.CreatedAtUtc,
                Severity = NotificationCatalog.Severity(message.Category),
                Server = message.ServerName,
                Version = message.ServerVersion,
                Events = message.Events.Select(e => new
                {
                    e.Kind,
                    Category = NotificationCatalog.Of(e.Kind).Category,
                    Severity = NotificationCatalog.Severity(NotificationCatalog.Of(e.Kind).Category),
                    Title = NotificationCatalog.Of(e.Kind).Label,
                    e.Key,
                    e.Text,
                    e.Affected,
                    e.Impact,
                    Link = NotificationMessage.Link(message.DashboardUrl, e.Path),
                }),
            },
            Json);

        using var request = new HttpRequestMessage(HttpMethod.Post, settings.WebhookUrl) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        if (secrets.WebhookSecret is { Length: > 0 } secret)
        {
            request.Headers.Add(SignatureHeader, "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)));
        }

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
    }
}
