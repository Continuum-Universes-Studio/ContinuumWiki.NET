using MySqlConnector;

namespace TightWiki.Repository.Helpers;

public static class MySqlBackupSafety
{
    public static string ValidateArtifactDestination(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("An explicit backup destination is required.", nameof(path));
        var full = Path.GetFullPath(path);
        if (Directory.Exists(full)) throw new ArgumentException("The backup destination must be a file, not a directory.", nameof(path));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return full;
    }

    public static void ValidateRestoreTarget(string sourceDatabase, string targetDatabase)
    {
        if (string.IsNullOrWhiteSpace(targetDatabase)) throw new ArgumentException("A restore target database is required.", nameof(targetDatabase));
        if (string.Equals(sourceDatabase.Trim(), targetDatabase.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Restore target must be a different database from the source.");
        var builder = new MySqlConnectionStringBuilder { Database = targetDatabase };
        if (builder.Database.Contains('`') || builder.Database.Contains(';')) throw new ArgumentException("Invalid restore target database identifier.", nameof(targetDatabase));
    }
}
