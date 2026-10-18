using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;

using UpdateCoordinator.Server.Data;

namespace UpdateCoordinator.Server.Administration;

/// <summary>
/// Writes and verifies the audit log. Separate from regular logging and reporting; entries are never changed.
/// </summary>
internal sealed class AuditLog(ServerDbContext db, TimeProvider timeProvider)
{
    public const string SystemActor = "system";

    private const int MaxActor = 256;
    private const int MaxAction = 64;
    private const int MaxTarget = 256;
    private const int MaxDetails = 2048;

    private static readonly byte[] GenesisHash = new byte[32];

    public async Task WriteAsync(string actor, string action, string? target = null, string? details = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(actor);
        ArgumentException.ThrowIfNullOrEmpty(action);

        // Serialized through a transaction so the chain cannot fork under concurrent writes.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var previous = await db.AuditEntries.AsNoTracking()
            .OrderByDescending(e => e.Id)
            .Select(e => e.Hash)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false) ?? GenesisHash;

        var entry = new AuditEntryEntity
        {
            AtUtc = timeProvider.GetUtcNow().UtcDateTime,
            Actor = Truncate(actor, MaxActor)!,
            Action = Truncate(action, MaxAction)!,
            Target = Truncate(target, MaxTarget),
            Details = Truncate(details, MaxDetails),
            PreviousHash = previous,
            Hash = [],
        };
        entry.Hash = ComputeHash(entry);

        db.AuditEntries.Add(entry);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns the ID of the first entry whose hash does not match, or null if the chain is intact.</summary>
    public async Task<long?> FindFirstBrokenEntryAsync(CancellationToken cancellationToken = default)
    {
        var previous = GenesisHash;
        await foreach (var entry in db.AuditEntries.AsNoTracking().OrderBy(e => e.Id).AsAsyncEnumerable().WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (!entry.PreviousHash.AsSpan().SequenceEqual(previous) || !entry.Hash.AsSpan().SequenceEqual(ComputeHash(entry)))
            {
                return entry.Id;
            }

            previous = entry.Hash;
        }

        return null;
    }

    private static byte[] ComputeHash(AuditEntryEntity entry)
    {
        // Length-prefixed fields, so no combination of values can produce the same input.
        var builder = new StringBuilder();
        foreach (var field in new[] { entry.AtUtc.Ticks.ToString(CultureInfo.InvariantCulture), entry.Actor, entry.Action, entry.Target ?? string.Empty, entry.Details ?? string.Empty })
        {
            builder.Append(field.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(field).Append('|');
        }

        var content = Encoding.UTF8.GetBytes(builder.ToString());
        return SHA256.HashData([.. entry.PreviousHash, .. content]);
    }

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
