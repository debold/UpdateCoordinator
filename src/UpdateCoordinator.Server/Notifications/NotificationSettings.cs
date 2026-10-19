using System.ComponentModel.DataAnnotations;
using System.Net.Mail;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using UpdateCoordinator.Common;
using UpdateCoordinator.Server.Data;

namespace UpdateCoordinator.Server.Notifications;

/// <summary>Service settings for notifications. Bound from the "Notifications" configuration section.</summary>
internal sealed class NotificationOptions
{
    public const string SectionName = "Notifications";

    /// <summary>False stops all notifications (e.g. in tests).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the state is checked for new events.</summary>
    [Range(typeof(TimeSpan), "00:00:10", "01:00:00")]
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>DPAPI-protected file with the SMTP password and the webhook secret; deliberately not part of the backup.</summary>
    [Required]
    public string SecretsPath { get; set; } = Path.Combine(DataPaths.Server, "notification-secrets.dat");
}

/// <summary>How the connection to the SMTP server is secured.</summary>
internal enum SmtpSecurity
{
    /// <summary>Plain connection upgraded with STARTTLS (port 587); fails if the server does not offer it.</summary>
    StartTls = 0,

    /// <summary>TLS from the first byte (port 465).</summary>
    SslOnConnect = 1,

    /// <summary>No encryption; only for an internal relay that offers nothing else.</summary>
    None = 2,
}

/// <summary>Notification settings that administrators change in the dashboard (no secrets).</summary>
internal sealed class NotificationSettings : IValidatableObject
{
    public const int MaxRecipients = 20;

    public bool EmailEnabled { get; set; }

    [MaxLength(253)]
    [RegularExpression(@"^[A-Za-z0-9]([A-Za-z0-9\-\.]*[A-Za-z0-9])?$", ErrorMessage = "The SMTP server must be a host name or IP address.")]
    public string? SmtpHost { get; set; }

    [Range(1, 65535)]
    public int SmtpPort { get; set; } = 587;

    [EnumDataType(typeof(SmtpSecurity))]
    public SmtpSecurity SmtpSecurity { get; set; } = SmtpSecurity.StartTls;

    [MaxLength(256)]
    public string? SmtpUser { get; set; }

    [MaxLength(254)]
    public string? From { get; set; }

    /// <summary>Recipient addresses separated by comma, semicolon or line breaks.</summary>
    [MaxLength(4000)]
    public string? Recipients { get; set; }

    public bool WebhookEnabled { get; set; }

    [MaxLength(2000)]
    public string? WebhookUrl { get; set; }

    /// <summary>Base address of the dashboard for links in notifications, e.g. https://uc01.lab.local:8443/; optional.</summary>
    [MaxLength(2000)]
    public string? DashboardUrl { get; set; }

    public IReadOnlyList<string> RecipientList =>
        (Recipients ?? string.Empty).Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (EmailEnabled)
        {
            if (string.IsNullOrWhiteSpace(SmtpHost))
            {
                yield return new ValidationResult("Enter the SMTP server.", [nameof(SmtpHost)]);
            }

            if (!IsMailAddress(From))
            {
                yield return new ValidationResult("Enter a valid sender address.", [nameof(From)]);
            }

            var recipients = RecipientList;
            if (recipients.Count is 0 or > MaxRecipients)
            {
                yield return new ValidationResult($"Enter between 1 and {MaxRecipients} recipients.", [nameof(Recipients)]);
            }
            else if (recipients.FirstOrDefault(r => !IsMailAddress(r)) is { } invalid)
            {
                yield return new ValidationResult($"'{invalid}' is not a valid e-mail address.", [nameof(Recipients)]);
            }
        }

        if (WebhookEnabled && !IsHttpsUrl(WebhookUrl))
        {
            yield return new ValidationResult("The webhook address must be an absolute https:// URL.", [nameof(WebhookUrl)]);
        }

        if (!string.IsNullOrWhiteSpace(DashboardUrl) && !IsHttpsUrl(DashboardUrl))
        {
            yield return new ValidationResult("The dashboard address must be an absolute https:// URL.", [nameof(DashboardUrl)]);
        }
    }

    private static bool IsMailAddress(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 254 && MailAddress.TryCreate(value, out var address) && address.Address == value;

    private static bool IsHttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo);
}

/// <summary>Secrets of the notification channels, stored DPAPI-protected outside the database.</summary>
internal sealed record NotificationSecrets(string? SmtpPassword, string? WebhookSecret);

/// <summary>Loads and saves the notification settings (database) and secrets (protected file).</summary>
internal sealed class NotificationSettingsStore(ServerDbContext db, IOptions<NotificationOptions> options, ILogger<NotificationSettingsStore> logger)
{
    public const string SettingKey = "Notifications";
    public const int MaxSecretLength = 512;

    public async Task<NotificationSettings> LoadAsync(CancellationToken cancellationToken)
    {
        var json = await db.Settings.AsNoTracking().Where(s => s.Key == SettingKey).Select(s => s.Value).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return json is null ? new NotificationSettings() : JsonSerializer.Deserialize<NotificationSettings>(json) ?? new NotificationSettings();
    }

    public NotificationSecrets LoadSecrets() => SecretsFile().Load() ?? new NotificationSecrets(null, null);

    /// <summary>Saves validated settings; a null secret keeps the stored one, an empty one removes it.</summary>
    public async Task SaveAsync(NotificationSettings settings, string? smtpPassword, string? webhookSecret, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(settings);
        var existing = await db.Settings.FindAsync([SettingKey], cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            db.Settings.Add(new SettingEntity { Key = SettingKey, Value = json });
        }
        else
        {
            existing.Value = json;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (smtpPassword is not null || webhookSecret is not null)
        {
            var current = LoadSecrets();
            SecretsFile().Save(new NotificationSecrets(
                smtpPassword is null ? current.SmtpPassword : NullIfEmpty(smtpPassword),
                webhookSecret is null ? current.WebhookSecret : NullIfEmpty(webhookSecret)));
        }
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private ProtectedJsonFile<NotificationSecrets> SecretsFile() =>
        new(new ProtectedFile(options.Value.SecretsPath, "UpdateCoordinator.Server.NotificationSecrets.v1", logger), logger);
}
