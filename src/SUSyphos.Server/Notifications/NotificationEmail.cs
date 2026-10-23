using System.Globalization;
using System.Net;
using System.Text;

using SUSyphos.Server.Components;

namespace SUSyphos.Server.Notifications;

/// <summary>
/// The HTML e-mail, styled like the dashboard (Marc, 2026-10-23): a short header with date, time, central server and
/// severity, then each event with its category as heading, the details (what happened, affected, impact) and a button
/// to the page concerned, and a footer with the central server's address and version. Mail programs ignore style
/// sheets, so every style is inline; every text is encoded.
/// </summary>
internal static class NotificationEmail
{
    private const string Text = "#141A22";
    private const string Muted = "#5A6472";
    private const string Line = "#E8ECF1";
    private const string Accent = "#1F5BC4";
    private const string Font = "'Segoe UI', system-ui, -apple-system, Arial, sans-serif";

    public static string Html(NotificationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>")
            .Append(E(message.Subject)).Append("</title></head>");
        html.Append(CultureInfo.InvariantCulture, $"<body style=\"margin:0;padding:24px 12px;background:#EEF1F5;font-family:{Font};color:{Text};\">");
        html.Append("<table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" style=\"max-width:640px;margin:0 auto;border-collapse:collapse;\">");

        // Header: product, date and time in the central server's time zone, server, severity.
        html.Append(CultureInfo.InvariantCulture, $"<tr><td style=\"background:#151B24;color:#FFFFFF;padding:14px 20px;border-radius:10px 10px 0 0;\">");
        html.Append("<table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\"><tr>");
        html.Append("<td style=\"font-size:16px;font-weight:600;\">SUSyphos</td>");
        html.Append("<td align=\"right\">").Append(Tag(NotificationCatalog.Severity(message.Category), message.Category)).Append("</td></tr>");
        html.Append(CultureInfo.InvariantCulture, $"<tr><td colspan=\"2\" style=\"padding-top:6px;font-size:12px;color:#C3CCD8;\">{E(DisplayTime.Format(message.CreatedAtUtc))} ({E(DisplayTime.Zone.StandardName)}) · {E(message.ServerName)}</td></tr>");
        html.Append("</table></td></tr>");

        foreach (var item in message.Events)
        {
            var info = NotificationCatalog.Of(item.Kind);
            html.Append(CultureInfo.InvariantCulture, $"<tr><td style=\"background:#FFFFFF;padding:18px 20px;border-bottom:1px solid {Line};\">");
            html.Append(CultureInfo.InvariantCulture, $"<div style=\"font-size:12px;color:{Muted};text-transform:uppercase;letter-spacing:0.04em;\">").Append(E(NotificationCatalog.Severity(info.Category))).Append("</div>");
            html.Append(CultureInfo.InvariantCulture, $"<h2 style=\"margin:4px 0 8px;font-size:17px;color:{Text};\">").Append(E(info.Label)).Append("</h2>");
            html.Append("<p style=\"margin:0 0 10px;font-size:14px;line-height:1.45;\">").Append(E(item.Text)).Append("</p>");
            if (item.Affected is not null || item.Impact is not null)
            {
                html.Append("<table role=\"presentation\" cellspacing=\"0\" cellpadding=\"0\" style=\"font-size:13px;line-height:1.45;margin-bottom:10px;\">");
                Row(html, "Affected", item.Affected);
                Row(html, "Impact", item.Impact);
                html.Append("</table>");
            }

            if (NotificationMessage.Link(message.DashboardUrl, item.Path) is { } link)
            {
                html.Append(CultureInfo.InvariantCulture, $"<a href=\"{E(link)}\" style=\"display:inline-block;background:{Accent};color:#FFFFFF;text-decoration:none;font-size:13px;font-weight:600;padding:8px 14px;border-radius:7px;\">Open in SUSyphos</a>");
            }

            html.Append("</td></tr>");
        }

        // Footer: where this comes from and how to change it.
        html.Append(CultureInfo.InvariantCulture, $"<tr><td style=\"background:#F6F8FB;padding:14px 20px;border-radius:0 0 10px 10px;font-size:12px;color:{Muted};line-height:1.5;\">");
        html.Append(CultureInfo.InvariantCulture, $"SUSyphos {E(message.ServerVersion)} on {E(message.ServerName)}");
        if (message.DashboardUrl is { Length: > 0 } dashboard)
        {
            html.Append(CultureInfo.InvariantCulture, $" · <a href=\"{E(dashboard)}\" style=\"color:{Accent};\">{E(dashboard)}</a><br>");
            html.Append(CultureInfo.InvariantCulture, $"<a href=\"{E(NotificationMessage.Link(dashboard, "administration/notifications")!)}\" style=\"color:{Accent};\">Change which events are sent</a>");
        }

        html.Append("</td></tr></table></body></html>");
        return html.ToString();
    }

    private static void Row(StringBuilder html, string label, string? value)
    {
        if (value is null)
        {
            return;
        }

        html.Append(CultureInfo.InvariantCulture, $"<tr><td style=\"color:{Muted};padding:2px 12px 2px 0;vertical-align:top;white-space:nowrap;\">{label}</td><td style=\"padding:2px 0;\">{E(value)}</td></tr>");
    }

    private static string Tag(string text, NotificationCategory category)
    {
        var (background, color) = category switch
        {
            NotificationCategory.Critical => ("#FBE7E4", "#C0392B"),
            NotificationCategory.Recommended => ("#FBF1DE", "#8A5A12"),
            _ => ("#E5EEFC", "#2F6FD6"),
        };
        return $"<span style=\"display:inline-block;background:{background};color:{color};font-size:12px;font-weight:600;padding:2px 10px;border-radius:999px;\">{E(text)}</span>";
    }

    private static string E(string value) => WebUtility.HtmlEncode(value);
}
