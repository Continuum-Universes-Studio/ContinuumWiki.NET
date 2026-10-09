using Dapper;
using Microsoft.AspNetCore.Identity;
using TightWiki.Library.Database;
using TightWiki.Plugin.Interfaces;
using TightWiki.Plugin.Interfaces.Persistence;
using TightWiki.Plugin.Models;
using TightWiki.Plugin.Models.Defaults;
using TightWiki.Plugin;

namespace TightWiki.Repository.Helpers;

/// <summary>Read-only MySQL health/schema status and explicit unsupported maintenance boundary.</summary>
public sealed class MySqlApplicationMaintenanceRepository : ITwApplicationMaintenanceRepository
{
    private readonly IDatabaseConnectionFactory _connectionFactory;
    private readonly MySqlDefaultContentSeeder _defaultContentSeeder;

    public MySqlApplicationMaintenanceRepository(IDatabaseConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
        _defaultContentSeeder = new MySqlDefaultContentSeeder(connectionFactory);
    }

    public string BackupPath => string.Empty;

    public async Task<IReadOnlyList<TwDatabaseInfo>> GetDatabaseInfoAsync(CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        var rows = await session.Connection.QueryAsync<TwDatabaseInfo>(new CommandDefinition("""
            SELECT TABLE_NAME AS Name,
                   COALESCE((SELECT GROUP_CONCAT(CONCAT(Domain, ':', Version) ORDER BY Domain SEPARATOR ', ') FROM MySqlPageSchemaVersion), 'uninitialized') AS Version,
                   TABLE_ROWS AS PageCount,
                   1 AS PageSize,
                   COALESCE(DATA_LENGTH, 0) + COALESCE(INDEX_LENGTH, 0) AS DatabaseSize
            FROM information_schema.TABLES
            WHERE TABLE_SCHEMA = DATABASE()
              AND TABLE_NAME IN ('Page', 'PageRevision', 'DeletedPageRevision', 'Profile', 'ConfigurationGroup', 'EventLog', 'PageStatistics')
            ORDER BY TABLE_NAME;
            """, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<TwDatabaseMaintenanceStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
            var databaseName = await session.Connection.ExecuteScalarAsync<string>(new CommandDefinition("SELECT DATABASE();", cancellationToken: cancellationToken)) ?? string.Empty;
            var tables = await session.Connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE();", cancellationToken: cancellationToken));
            var schemaVersion = await GetSchemaVersionAsync(session.Connection, cancellationToken);
            if (tables == 0)
                return new TwDatabaseMaintenanceStatus { Provider = "mysql", DatabaseName = databaseName, IsHealthy = true, IsInitialized = false, SchemaVersion = "uninitialized", SeedStatus = "Not seeded.", Message = "The MySQL database is reachable but has not been initialized." };

            await MySqlSchemaCompatibility.ValidateSupportedVersionsAsync(_connectionFactory, cancellationToken);
            return new TwDatabaseMaintenanceStatus { Provider = "mysql", DatabaseName = databaseName, IsHealthy = true, IsInitialized = true, SchemaVersion = schemaVersion, SeedStatus = "MySQL default content, translations, and menus are explicit, idempotent, and preserve existing content.", Message = "MySQL is reachable and its supported schema versions are current." };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new TwDatabaseMaintenanceStatus { Provider = "mysql", IsHealthy = false, IsInitialized = false, Message = ex.GetBaseException().Message };
        }
    }

    public Task<TwMaintenanceResult> ExecuteAsync(TwMaintenanceAction action, string databaseName, CancellationToken cancellationToken = default)
        => Task.FromResult(action == TwMaintenanceAction.Verify
            ? TwMaintenanceResult.Unsupported("MySQL verification is exposed through read-only health and schema status; provider-specific optimize/vacuum actions are unavailable.")
            : TwMaintenanceResult.Unsupported("MySQL optimize and vacuum actions are unavailable; no SQLite fallback is performed."));

    public Task<TwDatabaseBackup> CreateBackupAsync(CancellationToken cancellationToken = default)
        => Task.FromException<TwDatabaseBackup>(new NotSupportedException("MySQL backup is unavailable until a tested logical backup implementation exists; database files are not copied."));

    public IReadOnlyList<TwDatabaseBackup> GetBackups() => [];

    public void DeleteBackup(string fileName) => throw new NotSupportedException("MySQL backup management is unavailable because no MySQL backup artifacts are produced.");

    public async Task<TwMaintenanceResult> ApplySeedDataAsync(ITwSharedLocalizationText localizer, UserManager<IdentityUser> userManager, ITwEngine tightEngine, TwDefaultDataType[] defaultDataTypes, CancellationToken cancellationToken = default)
    {
        var message = await _defaultContentSeeder.SeedAsync(defaultDataTypes, cancellationToken);
        return TwMaintenanceResult.Success(message);
    }

    private static async Task<string> GetSchemaVersionAsync(System.Data.Common.DbConnection connection, CancellationToken cancellationToken)
    {
        var values = new List<string>();
        if (await TableExistsAsync(connection, "MySqlPageSchemaVersion", cancellationToken))
            values.Add($"pages:{await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COALESCE(MAX(Version), 0) FROM MySqlPageSchemaVersion;", cancellationToken: cancellationToken))}");
        if (await TableExistsAsync(connection, "MySqlIdentitySchemaVersion", cancellationToken))
            values.Add($"identity:{await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COALESCE(MAX(Version), 0) FROM MySqlIdentitySchemaVersion;", cancellationToken: cancellationToken))}");
        if (await TableExistsAsync(connection, "MySqlOperationsSchemaVersion", cancellationToken))
            values.Add($"operations:{await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COALESCE(MAX(Version), 0) FROM MySqlOperationsSchemaVersion;", cancellationToken: cancellationToken))}");
        return values.Count == 0 ? "uninitialized" : string.Join(", ", values);
    }

    private static async Task<bool> TableExistsAsync(System.Data.Common.DbConnection connection, string tableName, CancellationToken cancellationToken)
        => await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @TableName;", new { TableName = tableName }, cancellationToken: cancellationToken)) > 0;
}
