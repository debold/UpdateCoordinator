using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using MailKit.Security;

using MimeKit;

namespace UpdateCoordinator.Server.Notifications;

/// <summary>One notification: a subject and the new events since the last one.</summary>
internal sealed record NotificationMessage(string Subject, IReadOnlyList<NotificationEvent> Events, DateTime CreatedAtUtc)
{
    /// <summary>Plain text body; links point to the dashboard when its address is configured.</summary>
    public string FormatText(string? dashboardUrl)
    {
        var text = new StringBuilder();
        foreach (var item in Events)
        {
            text.Append("- ").AppendLine(item.Text);
            if (Link(dashboardUrl, item.Path) is { } link)
            {
                text.Append("  ").AppendLine(link);
            }
        }

        text.AppendLine().Append(CultureInfo.InvariantCulture, $"UpdateCoordinator central server, {CreatedAtUtc:yyyy-MM-dd HH:mm} UTC");
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

    Task SendAsync(NotificationMessage message, NotificationSettings settings, NotificationSecrets secrets, CancellationToken cancellationToken);
}

/// <summary>Sends a built e-mail; separate so tests never contact a mail server.</summary>
internal interface IMailTransport
{
    Task SendAsync(MimeMessage message, NotificationSettings settings, string? password, CancellationToken cancellationToken);
}

/// <summary>SMTP through MailKit; the certificate of the mail server is validated as usual.</summary>
internal sealed class SmtpMailTransport : IMailTransport
{
    public async Task SendAsync(MimeMessage message, NotificationSettings settings, string? password, CancellationToken cancellationToken)
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

        await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class EmailChannel(IMailTransport transport) : INotificationChannel
{
    public string Name => "E-mail";

    public bool IsEnabled(NotificationSettings settings) => settings.EmailEnabled;

    public Task SendAsync(NotificationMessage message, NotificationSettings settings, NotificationSecrets secrets, CancellationToken cancellationToken)
    {
        var mail = new MimeMessage { Subject = message.Subject };
        mail.From.Add(MailboxAddress.Parse(settings.From ?? throw new InvalidOperationException("No sender address is configured.")));
        foreach (var recipient in settings.RecipientList)
        {
            mail.To.Add(MailboxAddress.Parse(recipient));
        }

        mail.Body = new TextPart("plain") { Text = message.FormatText(settings.DashboardUrl) };
        return transport.SendAsync(mail, settings, secrets.SmtpPassword, cancellationToken);
    }
}

/// <summary>
/// Generic webhook: POST of a JSON document. With a secret, the header <c>X-UpdateCoordinator-Signature</c> carries
/// <c>sha256=</c> and the hex HMAC-SHA256 of the body, so the receiver can verify the sender.
/// </summary>
internal sealed class WebhookChannel(HttpClient http) : INotificationChannel
{
    public const string SignatureHeader = "X-UpdateCoordinator-Signature";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public string Name => "Webhook";

    public bool IsEnabled(NotificationSettings settings) => settings.WebhookEnabled;

    public async Task SendAsync(NotificationMessage message, NotificationSettings settings, NotificationSecrets secrets, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                message.Subject,
                message.CreatedAtUtc,
                Events = message.Events.Select(e => new { e.Kind, e.Key, e.Text, Link = NotificationMessage.Link(settings.DashboardUrl, e.Path) }),
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
    }
}
