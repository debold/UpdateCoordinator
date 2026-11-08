using System.Globalization;
using System.IO.Compression;

using Microsoft.Data.Sqlite;

namespace SUSyphos.WinGet.Catalog;

/// <summary>A package as the index of the community source lists it.</summary>
/// <param name="Id">WinGet package identifier.</param>
/// <param name="Name">Package name.</param>
/// <param name="Moniker">Short name, e.g. "7zip"; null if none.</param>
/// <param name="LatestVersion">Newest version in the source.</param>
public sealed record IndexPackage(string Id, string Name, string? Moniker, string LatestVersion);

/// <summary>
/// The package index of the WinGet community source (<c>source2.msix</c>, schema 2): every package with its newest
/// version, short name, tags and the product codes of its installers. The central server reads it without the WinGet
/// engine: to search packages, to see new versions and to match installed programs by product code. Read only; every
/// value goes in as a parameter.
/// </summary>
public sealed class CommunityIndex
{
    /// <summary>The community source of WinGet, refreshed by Microsoft several times a day.</summary>
    public static readonly Uri SourceAddress = new("https://cdn.winget.microsoft.com/cache/source2.msix");

    /// <summary>Path of the database inside the package.</summary>
    public const string EntryName = "Public/index.db";

    /// <summary>The package is a few MB; anything far larger is not the index.</summary>
    public const long MaxPackageBytes = 128L * 1024 * 1024;

    /// <summary>The unpacked database; a few tens of MB today.</summary>
    public const long MaxDatabaseBytes = 512L * 1024 * 1024;

    private readonly string _connectionString;

    private CommunityIndex(string path)
    {
        Path = path;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
    }

    /// <summary>File of the database.</summary>
    public string Path { get; }

    /// <summary>Opens an unpacked index and checks that it is one (schema version 2).</summary>
    /// <exception cref="InvalidDataException">The file is no index of schema version 2.</exception>
    public static CommunityIndex Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var index = new CommunityIndex(path);
        try
        {
            var major = index.Scalar("SELECT value FROM metadata WHERE name = 'majorVersion'");
            if (major != "2")
            {
                throw new InvalidDataException($"The WinGet index has schema version {major ?? "unknown"}; version 2 is supported.");
            }
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException("The file is no WinGet index.", exception);
        }

        return index;
    }

    /// <summary>
    /// Unpacks the database from a downloaded <c>source2.msix</c> and replaces <paramref name="databasePath"/> only when
    /// the new file opens as an index; a broken download leaves the previous index in place.
    /// </summary>
    /// <exception cref="InvalidDataException">The package holds no valid index.</exception>
    public static CommunityIndex Extract(string packagePath, string databasePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(packagePath);
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        var temporary = databasePath + ".new";
        try
        {
            using (var package = ZipFile.OpenRead(packagePath))
            {
                var entry = package.GetEntry(EntryName) ?? throw new InvalidDataException($"The package holds no {EntryName}.");
                if (entry.Length > MaxDatabaseBytes)
                {
                    throw new InvalidDataException("The index in the package is larger than expected.");
                }

                entry.ExtractToFile(temporary, overwrite: true);
            }

            Open(temporary);
            File.Move(temporary, databasePath, overwrite: true);
            return Open(databasePath);
        }
        catch (InvalidDataException)
        {
            File.Delete(temporary);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            File.Delete(temporary);
            throw new InvalidDataException($"The index could not be unpacked: {exception.Message}", exception);
        }
    }

    /// <summary>When Microsoft last wrote the index; null if it does not say.</summary>
    public DateTimeOffset? LastWrite =>
        long.TryParse(Scalar("SELECT value FROM metadata WHERE name = 'lastwritetime'"), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    /// <summary>Number of packages in the index.</summary>
    public int Count => int.Parse(Scalar("SELECT COUNT(*) FROM packages") ?? "0", CultureInfo.InvariantCulture);

    /// <summary>The package with this identifier (without case, like WinGet), or null.</summary>
    public IndexPackage? Find(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return Query("SELECT id, name, moniker, latest_version FROM packages WHERE id = $id COLLATE NOCASE LIMIT 1", ("$id", id)).FirstOrDefault();
    }

    /// <summary>
    /// Packages whose identifier, name, short name or a tag contains the text, best matches first (exact identifier or
    /// short name, then identifier or name starting with the text). At most <paramref name="limit"/> results.
    /// </summary>
    public IReadOnlyList<IndexPackage> Search(string text, int limit = 50)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return [];
        }

        var like = "%" + Escape(trimmed) + "%";
        var prefix = Escape(trimmed) + "%";
        return Query(
            """
            SELECT id, name, moniker, latest_version FROM packages
            WHERE id LIKE $like ESCAPE '\' OR name LIKE $like ESCAPE '\' OR moniker LIKE $like ESCAPE '\'
               OR rowid IN (SELECT m.package FROM tags2_map m JOIN tags2 t ON t.rowid = m.tag WHERE t.tag LIKE $like ESCAPE '\')
            ORDER BY (id = $text COLLATE NOCASE OR moniker = $text COLLATE NOCASE) DESC,
                     (id LIKE $prefix ESCAPE '\' OR name LIKE $prefix ESCAPE '\') DESC,
                     name COLLATE NOCASE
            LIMIT $limit
            """,
            ("$like", like),
            ("$prefix", prefix),
            ("$text", trimmed),
            ("$limit", Math.Clamp(limit, 1, 500)));
    }

    /// <summary>Packages one of whose installers has this product code (the name of an uninstall key); usually one.</summary>
    public IReadOnlyList<IndexPackage> ByProductCode(string productCode)
    {
        ArgumentNullException.ThrowIfNull(productCode);
        return Query(
            """
            SELECT DISTINCT p.id, p.name, p.moniker, p.latest_version FROM productcodes2 c JOIN packages p ON p.rowid = c.package
            WHERE c.productcode = $code COLLATE NOCASE
            LIMIT 10
            """,
            ("$code", productCode.Trim()));
    }

    /// <summary>LIKE wildcards and the escape character itself taken literally.</summary>
    private static string Escape(string value) => value.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal);

    private string? Scalar(string sql)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() is { } value and not DBNull ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
    }

    private List<IndexPackage> Query(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        using var reader = command.ExecuteReader();
        var packages = new List<IndexPackage>();
        while (reader.Read())
        {
            packages.Add(new IndexPackage(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3)));
        }

        return packages;
    }
}
