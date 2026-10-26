using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using SUSyphos.Server.Data;

namespace SUSyphos.Server.Security;

/// <summary>The MSRC security data (configuration section <c>Msrc</c>, Marc 2026-10-26).</summary>
internal sealed class MsrcOptions
{
    public const string Section = "Msrc";

    /// <summary>False for a central server without internet access: no MSRC data, no security baseline.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The Security Update Guide API (CVRF, JSON, no key needed).</summary>
    [Required]
    public Uri BaseAddress { get; set; } = new("https://api.msrc.microsoft.com/cvrf/v3.0/");

    /// <summary>How many monthly documents are kept: the current one and the ones before for "behind" and open vulnerabilities.</summary>
    [Range(1, 12)]
    public int Months { get; set; } = 3;

    /// <summary>How often the data is fetched outside the days around the patch day.</summary>
    [Range(typeof(TimeSpan), "01:00:00", "7.00:00:00")]
    public TimeSpan Interval { get; set; } = TimeSpan.FromDays(1);

    /// <summary>How often it is fetched from the patch day (second Tuesday) to two days after, when Microsoft revises the data.</summary>
    [Range(typeof(TimeSpan), "00:15:00", "1.00:00:00")]
    public TimeSpan PatchDayInterval { get; set; } = TimeSpan.FromHours(2);
}

/// <summary>Reads the MSRC Security Update Guide. Separate so tests never go to the internet.</summary>
internal interface IMsrcSource
{
    Task<IReadOnlyList<MsrcUpdate>> GetUpdatesAsync(CancellationToken cancellationToken);

    Task<MsrcDocument> GetDocumentAsync(string id, CancellationToken cancellationToken);
}

/// <summary>The MSRC API over HTTPS; documents are read as a stream into the few fields the central server keeps.</summary>
internal sealed class MsrcApi(HttpClient http, IOptions<MsrcOptions> options) : IMsrcSource
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<MsrcUpdate>> GetUpdatesAsync(CancellationToken cancellationToken) =>
        (await http.GetFromJsonAsync<MsrcUpdateList>(new Uri(options.Value.BaseAddress, "updates"), Json, cancellationToken).ConfigureAwait(false))?.Value ?? [];

    public async Task<MsrcDocument> GetDocumentAsync(string id, CancellationToken cancellationToken)
    {
        // The identifier comes from the list of the same API; it is still checked before it goes into the address.
        if (!MsrcSync.IsDocumentId(id))
        {
            throw new ArgumentException("Invalid MSRC document id.", nameof(id));
        }

        using var response = await http.GetAsync(new Uri(options.Value.BaseAddress, "cvrf/" + id), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<MsrcDocument>(stream, Json, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"The MSRC document {id} is empty.");
    }
}

/// <summary>When the data was last fetched and what came of it.</summary>
internal sealed record MsrcStatus(DateTime? LastSuccessUtc, DateTime? LastAttemptUtc, string? LastError, IReadOnlyList<string> Months);

/// <summary>
/// Keeps the MSRC data of the last months in the database: a monthly document is fetched again only when Microsoft
/// changed it, replaced as a whole, and months beyond <see cref="MsrcOptions.Months"/> are removed. A failed fetch keeps
/// the data there is; the next run tries again.
/// </summary>
internal sealed partial class MsrcSync(ServerDbContext db, IMsrcSource source, IOptions<MsrcOptions> options, TimeProvider timeProvider, ILogger<MsrcSync> logger)
{
    private const string StatusKey = "Msrc.Status";
    private const string DocumentKeyPrefix = "Msrc.Document.";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>"2026-Oct": year and English month abbreviation, as MSRC names its documents.</summary>
    public static bool IsDocumentId(string? id) =>
        id is { Length: 8 } && DateTime.TryParseExact(id, "yyyy-MMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    public async Task<MsrcStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var stored = await db.Settings.AsNoTracking().Where(s => s.Key == StatusKey).Select(s => s.Value).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return stored is null ? new MsrcStatus(null, null, null, []) : JsonSerializer.Deserialize<MsrcStatus>(stored, Json) ?? new MsrcStatus(null, null, null, []);
        }
        catch (JsonException)
        {
            return new MsrcStatus(null, null, null, []);
        }
    }

    /// <summary>True when the data should be fetched now: daily, every two hours around the patch day, never more often than every 15 minutes.</summary>
    public async Task<bool> IsDueAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            return false;
        }

        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (status.LastAttemptUtc is { } attempt && now - attempt < TimeSpan.FromMinutes(15))
        {
            return false;
        }

        var interval = InPatchDayWindow(now) ? options.Value.PatchDayInterval : options.Value.Interval;
        return status.LastSuccessUtc is not { } success || now - success >= interval;
    }

    /// <summary>From the second Tuesday of a month (patch day) to two days after it.</summary>
    public static bool InPatchDayWindow(DateTime utc)
    {
        var day = PatchDay(utc.Year, utc.Month);
        return utc.Date >= day && utc.Date <= day.AddDays(2);
    }

    /// <summary>The second Tuesday of a month.</summary>
    public static DateTime PatchDay(int year, int month)
    {
        var first = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var tuesday = first.AddDays(((int)DayOfWeek.Tuesday - (int)first.DayOfWeek + 7) % 7);
        return tuesday.AddDays(7);
    }

    /// <summary>Fetches what changed; <paramref name="force"/> fetches every kept document again.</summary>
    public async Task<MsrcStatus> RunAsync(bool force, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var before = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var documents = (await source.GetUpdatesAsync(cancellationToken).ConfigureAwait(false))
                .Where(u => IsDocumentId(u.Id) && u.DocumentTitle?.EndsWith("Security Updates", StringComparison.OrdinalIgnoreCase) == true)
                .OrderByDescending(u => u.InitialReleaseDate)
                .Take(options.Value.Months)
                .ToList();
            foreach (var document in documents)
            {
                var key = DocumentKeyPrefix + document.Id;
                var version = document.CurrentReleaseDate.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
                var stored = await db.Settings.SingleOrDefaultAsync(s => s.Key == key, cancellationToken).ConfigureAwait(false);
                if (!force && stored?.Value == version && await db.SecurityBaselines.AnyAsync(b => b.Month == document.Id, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                var month = MsrcParser.Parse(document.Id, await source.GetDocumentAsync(document.Id, cancellationToken).ConfigureAwait(false));
                await ReplaceAsync(month, cancellationToken).ConfigureAwait(false);
                if (stored is null)
                {
                    db.Settings.Add(new SettingEntity { Key = key, Value = version });
                }
                else
                {
                    stored.Value = version;
                }

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                LogDocument(document.Id, month.Baselines.Count, month.Vulnerabilities.Count);
            }

            var kept = documents.Select(d => d.Id).ToList();
            if (kept.Count > 0)
            {
                await db.SecurityBaselines.Where(b => !kept.Contains(b.Month)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await db.SecurityVulnerabilities.Where(v => !kept.Contains(v.Month)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await db.SecurityFixes.Where(f => !kept.Contains(f.Month)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await db.Settings.Where(s => s.Key.StartsWith(DocumentKeyPrefix) && !kept.Select(k => DocumentKeyPrefix + k).Contains(s.Key)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            }

            return await SaveStatusAsync(new MsrcStatus(now, now, null, kept), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LogFailed(ex);
            db.ChangeTracker.Clear();
            return await SaveStatusAsync(before with { LastAttemptUtc = now, LastError = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReplaceAsync(MsrcMonth month, CancellationToken cancellationToken)
    {
        await db.SecurityBaselines.Where(b => b.Month == month.Month).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.SecurityVulnerabilities.Where(v => v.Month == month.Month).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.SecurityFixes.Where(f => f.Month == month.Month).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        db.SecurityBaselines.AddRange(month.Baselines);
        db.SecurityVulnerabilities.AddRange(month.Vulnerabilities);
        db.SecurityFixes.AddRange(month.Fixes);
    }

    private async Task<MsrcStatus> SaveStatusAsync(MsrcStatus status, CancellationToken cancellationToken)
    {
        var value = JsonSerializer.Serialize(status, Json);
        var stored = await db.Settings.SingleOrDefaultAsync(s => s.Key == StatusKey, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            db.Settings.Add(new SettingEntity { Key = StatusKey, Value = value });
        }
        else
        {
            stored.Value = value;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return status;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "MSRC document {Month} read: {Baselines} Windows Server baselines, {Vulnerabilities} vulnerabilities.")]
    private partial void LogDocument(string month, int baselines, int vulnerabilities);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fetching the MSRC security data failed; the data there is stays and the next run tries again.")]
    private partial void LogFailed(Exception exception);
}

/// <summary>Fetches the MSRC data when it is due (checked every 15 minutes).</summary>
internal sealed partial class MsrcWorker(IServiceScopeFactory scopes, IOptions<MsrcOptions> options, TimeProvider timeProvider, ILogger<MsrcWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested && options.Value.Enabled)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var sync = scope.ServiceProvider.GetRequiredService<MsrcSync>();
                if (await sync.IsDueAsync(stoppingToken).ConfigureAwait(false))
                {
                    await sync.RunAsync(force: false, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogFailed(ex);
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(15), timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The MSRC worker failed; it tries again in 15 minutes.")]
    private partial void LogFailed(Exception exception);
}
