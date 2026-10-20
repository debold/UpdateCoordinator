using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using SUSyphos.Common;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.AgentPackages;

/// <summary>Agent packages and the GitHub check. Bound from the "AgentPackages" configuration section.</summary>
internal sealed class AgentPackageOptions
{
    public const string SectionName = "AgentPackages";

    /// <summary>Folder with the agent MSI shipped by the central server installer.</summary>
    public string BundledDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "agent");

    /// <summary>Folder the central server keeps its agent packages in.</summary>
    public string Directory { get; set; } = Path.Combine(DataPaths.Server, "AgentPackages");

    /// <summary>Checks the GitHub releases for new agent versions.</summary>
    public bool CheckEnabled { get; set; } = true;

    [Range(typeof(TimeSpan), "01:00:00", "30.00:00:00")]
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromDays(1);

    /// <summary>GitHub repository ("owner/name") whose releases carry the agent MSI.</summary>
    [RegularExpression("^[A-Za-z0-9-]{1,39}/[A-Za-z0-9._-]{1,100}$")]
    public string Repository { get; set; } = "debold/UpdateCoordinator";

    /// <summary>Also offers pre-releases; every release before 1.0 is one.</summary>
    public bool IncludePrereleases { get; set; } = true;

    /// <summary>
    /// Agents below this version are updated with the package shipped with the central server even without a release; it can
    /// only raise the built-in minimum (the first version that updates itself). Empty: the built-in minimum.
    /// </summary>
    [RegularExpression(@"^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$")]
    public string? MinimumVersion { get; set; }

    /// <summary>Upper bound for a downloaded package.</summary>
    [Range(1L * 1024 * 1024, 1024L * 1024 * 1024)]
    public long MaxPackageSize { get; set; } = 300L * 1024 * 1024;
}

/// <summary>
/// Holds the agent installers: the one shipped with the central server and those downloaded from GitHub, each verified
/// by its SHA-256 (docs/04-central-server.md, agent updates).
/// </summary>
internal sealed partial class AgentPackageStore(ServerDbContext db, IOptions<AgentPackageOptions> options, TimeProvider timeProvider, ILogger<AgentPackageStore> logger)
{
    /// <summary>Name of a package file: "SUSyphos.Agent-0.4.0.msi".</summary>
    public static string FileNameFor(AgentVersion version) => $"SUSyphos.Agent-{version}.msi";

    public Task<List<AgentPackageEntity>> ListAsync(CancellationToken cancellationToken) =>
        db.AgentPackages.AsNoTracking().ToListAsync(cancellationToken);

    /// <summary>The newest package, released or not; new agents are installed with it.</summary>
    public async Task<AgentPackageEntity?> GetNewestAsync(CancellationToken cancellationToken) =>
        Newest(await ListAsync(cancellationToken).ConfigureAwait(false));

    public static AgentPackageEntity? Newest(IEnumerable<AgentPackageEntity> packages) =>
        packages.Select(p => (Package: p, Version: AgentVersion.TryParse(p.Version, out var v) ? v : null))
            .Where(p => p.Version is not null)
            .OrderByDescending(p => p.Version)
            .Select(p => p.Package)
            .FirstOrDefault();

    /// <summary>Full path of a package file; null if the version is unknown or the file is missing.</summary>
    public async Task<string?> GetPathAsync(string version, CancellationToken cancellationToken)
    {
        var package = await db.AgentPackages.AsNoTracking().SingleOrDefaultAsync(p => p.Version == version, cancellationToken).ConfigureAwait(false);
        if (package is null)
        {
            return null;
        }

        var path = Path.Combine(options.Value.Directory, package.FileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Registers the agent MSI shipped with the central server, copying it into the package folder once.</summary>
    public async Task RegisterBundledAsync(CancellationToken cancellationToken)
    {
        var directory = options.Value.BundledDirectory;
        if (!System.IO.Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in System.IO.Directory.EnumerateFiles(directory, "SUSyphos.Agent-*.msi"))
        {
            var match = BundledNameRegex().Match(Path.GetFileName(file));
            if (!match.Success || !AgentVersion.TryParse(match.Groups["version"].Value, out var version))
            {
                continue;
            }

            if (await db.AgentPackages.AnyAsync(p => p.Version == version.ToString(), cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            await using var stream = File.OpenRead(file);
            await AddAsync(version, AgentPackageSource.Bundled, stream, expectedSha256: null, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stores a package: writes to a temporary file, checks size and (if given) the expected SHA-256, then moves it into place.
    /// Returns false and stores nothing if a check fails.
    /// </summary>
    public async Task<bool> AddAsync(AgentVersion version, AgentPackageSource source, Stream content, string? expectedSha256, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(content);
        var versionText = version.ToString();
        var directory = options.Value.Directory;
        System.IO.Directory.CreateDirectory(directory);
        var fileName = FileNameFor(version);
        var target = Path.Combine(directory, fileName);
        // A name of its own per call, so two registrations of the same package never write into one file.
        var temporary = $"{target}.{Guid.NewGuid():N}.partial";
        string sha256;
        long size;
        try
        {
            await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                size = 0;
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    size += read;
                    if (size > options.Value.MaxPackageSize)
                    {
                        LogTooLarge(versionText);
                        return false;
                    }

                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            if (expectedSha256 is not null && !string.Equals(sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                LogHashMismatch(versionText);
                return false;
            }

            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }

        var entity = db.AgentPackages.Add(new AgentPackageEntity
        {
            Version = versionText,
            Source = source,
            FileName = fileName,
            Sha256 = sha256,
            Size = size,
            AddedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Registered at the same moment by another call (e.g. the start-up registration): the package is there.
            entity.State = EntityState.Detached;
            if (await db.AgentPackages.AsNoTracking().AnyAsync(p => p.Version == versionText, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            throw;
        }

        LogAdded(versionText, source);
        return true;
    }

    [GeneratedRegex(@"^SUSyphos\.Agent-(?<version>[0-9A-Za-z.+-]{5,40})\.msi$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex BundledNameRegex();

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent package {Version} added ({Source}).")]
    private partial void LogAdded(string version, AgentPackageSource source);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent package {Version} rejected: its SHA-256 does not match the published checksum.")]
    private partial void LogHashMismatch(string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent package {Version} rejected: larger than AgentPackages:MaxPackageSize.")]
    private partial void LogTooLarge(string version);
}
