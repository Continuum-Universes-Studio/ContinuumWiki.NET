using Dapper;
using TightWiki.Library.Database;

namespace TightWiki.Repository.Helpers;

public static class MySqlSchemaCompatibility
{
    public static async Task ValidateSupportedVersionsAsync(IDatabaseConnectionFactory connectionFactory, CancellationToken cancellationToken)
    {
        await using var session = await connectionFactory.OpenSessionAsync(cancellationToken);
        await EnsureNotNewerAsync(session, "SchemaVersion", "SELECT COALESCE(MAX(VersionNumber), 0) FROM SchemaVersion;", 1, cancellationToken);
        await EnsureNotNewerAsync(session, "MySqlIdentitySchemaVersion", "SELECT COALESCE(MAX(Version), 0) FROM MySqlIdentitySchemaVersion;", 1, cancellationToken);
        await EnsureNotNewerAsync(session, "MySqlPageSchemaVersion", "SELECT COALESCE(MAX(Version), 0) FROM MySqlPageSchemaVersion;", 3, cancellationToken);
        await EnsureNotNewerAsync(session, "MySqlOperationsSchemaVersion", "SELECT COALESCE(MAX(Version), 0) FROM MySqlOperationsSchemaVersion;", 1, cancellationToken);
        await EnsureNotNewerAsync(session, "MySqlDefaultsSchemaVersion", "SELECT COALESCE(MAX(Version), 0) FROM MySqlDefaultsSchemaVersion;", 2, cancellationToken);
        await EnsureNotNewerAsync(session, "MediaWikiImportSchemaVersion", "SELECT COALESCE(MAX(Version), 0) FROM MediaWikiImportSchemaVersion;", 2, cancellationToken);
    }

    private static async Task EnsureNotNewerAsync(IDatabaseSession session, string tableName, string versionSql, int supportedVersion, CancellationToken cancellationToken)
    {
        var exists = await session.Connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @TableName;", new { TableName = tableName }, cancellationToken: cancellationToken));
        if (exists == 0) return;
        var version = await session.Connection.ExecuteScalarAsync<int>(new CommandDefinition(versionSql, cancellationToken: cancellationToken));
        if (version > supportedVersion)
            throw new InvalidOperationException($"MySQL schema table '{tableName}' is at version {version}, but this application supports only version {supportedVersion}.");
    }
}
