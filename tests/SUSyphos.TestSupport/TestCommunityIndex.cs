using System.IO.Compression;

using Microsoft.Data.Sqlite;

namespace SUSyphos.TestSupport;

/// <summary>A package of a test index: identifier, name, short name, newest version, tags and product codes.</summary>
public sealed record TestIndexPackage(string Id, string Name, string LatestVersion, string? Moniker = null, string[]? Tags = null, string[]? ProductCodes = null);

/// <summary>
/// Builds a small index of the WinGet community source with the tables of schema version 2 that SUSyphos reads, so no
/// test downloads the real one.
/// </summary>
public static class TestCommunityIndex
{
    public static string Create(string path, IEnumerable<TestIndexPackage> packages, string majorVersion = "2")
    {
        ArgumentNullException.ThrowIfNull(packages);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Delete(path);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            connection.Open();
            Execute(connection, """
                CREATE TABLE metadata(name TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL) WITHOUT ROWID;
                CREATE TABLE packages(rowid INTEGER PRIMARY KEY, id TEXT NOT NULL, name TEXT NOT NULL, moniker TEXT, latest_version TEXT NOT NULL, arp_min_version TEXT, arp_max_version TEXT, hash BLOB);
                CREATE TABLE tags2(rowid INTEGER PRIMARY KEY, tag TEXT NOT NULL);
                CREATE TABLE tags2_map(tag INT64 NOT NULL, package INT64 NOT NULL, PRIMARY KEY(tag, package)) WITHOUT ROWID;
                CREATE TABLE productcodes2(productcode TEXT NOT NULL, package INT64 NOT NULL, PRIMARY KEY(productcode, package)) WITHOUT ROWID;
                """);
            Execute(connection, "INSERT INTO metadata VALUES ('majorVersion', $v), ('minorVersion', '1'), ('lastwritetime', '1794163375')", ("$v", majorVersion));
            var row = 0L;
            var tagRow = 0L;
            foreach (var package in packages)
            {
                row++;
                Execute(connection, "INSERT INTO packages(rowid, id, name, moniker, latest_version) VALUES ($r, $id, $name, $moniker, $version)", ("$r", row), ("$id", package.Id), ("$name", package.Name), ("$moniker", (object?)package.Moniker ?? DBNull.Value), ("$version", package.LatestVersion));
                foreach (var tag in package.Tags ?? [])
                {
                    tagRow++;
                    Execute(connection, "INSERT INTO tags2(rowid, tag) VALUES ($t, $tag)", ("$t", tagRow), ("$tag", tag));
                    Execute(connection, "INSERT INTO tags2_map VALUES ($t, $r)", ("$t", tagRow), ("$r", row));
                }

                foreach (var code in package.ProductCodes ?? [])
                {
                    Execute(connection, "INSERT OR IGNORE INTO productcodes2 VALUES ($c, $r)", ("$c", code.ToLowerInvariant()), ("$r", row));
                }
            }
        }

        return path;
    }

    /// <summary>A source2.msix-like package holding the index at Public/index.db.</summary>
    public static string CreatePackage(string packagePath, string databasePath)
    {
        File.Delete(packagePath);
        using var zip = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        zip.CreateEntryFromFile(databasePath, "Public/index.db");
        zip.CreateEntry("AppxManifest.xml");
        return packagePath;
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }
}
