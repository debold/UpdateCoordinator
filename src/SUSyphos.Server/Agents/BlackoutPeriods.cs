using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Agents;

/// <summary>A time span in UTC in which no installation starts anywhere, e.g. over the holidays.</summary>
internal sealed record BlackoutPeriod(Guid Id, DateTime StartUtc, DateTime EndUtc, string Reason);

/// <summary>
/// Blackout periods (docs/04-central-server.md, section 3): planned global pauses. While one is active, the heartbeat tells
/// the agents to start no installation, exactly like the global pause; search and reporting continue. Periods that ended
/// more than 30 days ago are dropped when the list changes.
/// </summary>
internal sealed partial class BlackoutPeriods(ServerDbContext db, AuditLog audit, TimeProvider timeProvider)
{
    public const string SettingKey = "Installation.Blackouts";
    public const int MaxPeriods = 50;
    public const int MaxReasonLength = 200;
    public static readonly TimeSpan MaxLength = TimeSpan.FromDays(90);

    public async Task<IReadOnlyList<BlackoutPeriod>> ListAsync(CancellationToken cancellationToken) =>
        [.. (await LoadAsync(cancellationToken).ConfigureAwait(false)).OrderBy(p => p.StartUtc)];

    /// <summary>The period active now, if any.</summary>
    public async Task<BlackoutPeriod?> GetActiveAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return (await LoadAsync(cancellationToken).ConfigureAwait(false)).Where(p => p.StartUtc <= now && now < p.EndUtc).OrderBy(p => p.EndUtc).LastOrDefault();
    }

    public async Task<Outcome> AddAsync(DateTime startUtc, DateTime endUtc, string? reason, string actor, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var text = reason?.Trim();
        if (endUtc <= startUtc)
        {
            return new Outcome("The end must lie after the start.");
        }

        if (endUtc - startUtc > MaxLength)
        {
            return new Outcome($"A blackout period may last at most {MaxLength.TotalDays} days.");
        }

        if (endUtc <= now)
        {
            return new Outcome("The blackout period lies in the past.");
        }

        if (string.IsNullOrEmpty(text) || text.Length > MaxReasonLength || !PrintableRegex().IsMatch(text))
        {
            return new Outcome($"Enter a reason of at most {MaxReasonLength} characters.");
        }

        var periods = (await LoadAsync(cancellationToken).ConfigureAwait(false)).Where(p => p.EndUtc > now.AddDays(-30)).ToList();
        if (periods.Count >= MaxPeriods)
        {
            return new Outcome($"At most {MaxPeriods} blackout periods can be planned.");
        }

        var period = new BlackoutPeriod(Guid.NewGuid(), DateTime.SpecifyKind(startUtc, DateTimeKind.Utc), DateTime.SpecifyKind(endUtc, DateTimeKind.Utc), text);
        periods.Add(period);
        await SaveAsync(periods, cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "Installation.BlackoutAdded", text, $"{Format(period.StartUtc)} to {Format(period.EndUtc)} UTC", cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    public async Task<Outcome> RemoveAsync(Guid id, string actor, CancellationToken cancellationToken)
    {
        var periods = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var period = periods.FirstOrDefault(p => p.Id == id);
        if (period is null)
        {
            return new Outcome("The blackout period no longer exists.");
        }

        periods.Remove(period);
        await SaveAsync(periods, cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "Installation.BlackoutRemoved", period.Reason, $"{Format(period.StartUtc)} to {Format(period.EndUtc)} UTC", cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    private static string Format(DateTime value) => value.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    private async Task<List<BlackoutPeriod>> LoadAsync(CancellationToken cancellationToken)
    {
        var value = await db.Settings.AsNoTracking().Where(s => s.Key == SettingKey).Select(s => s.Value).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return value is null ? [] : JsonSerializer.Deserialize<List<BlackoutPeriod>>(value) ?? [];
    }

    private async Task SaveAsync(List<BlackoutPeriod> periods, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(periods);
        var setting = await db.Settings.SingleOrDefaultAsync(s => s.Key == SettingKey, cancellationToken).ConfigureAwait(false);
        if (setting is null)
        {
            db.Settings.Add(new SettingEntity { Key = SettingKey, Value = json });
        }
        else
        {
            setting.Value = json;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    [GeneratedRegex(ValidationPatterns.PrintableText, RegexOptions.None, 1000)]
    private static partial Regex PrintableRegex();
}
