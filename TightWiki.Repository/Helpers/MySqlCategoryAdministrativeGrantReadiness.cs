using Dapper;
using TightWiki.Library.Database;

namespace TightWiki.Repository.Helpers;

/// <summary>Read-only proof required before explicit administrative-grant activation.</summary>
public static class MySqlCategoryAdministrativeGrantReadiness
{
    public static async Task<MySqlCategoryAdministrativeGrantReadinessProof> ValidateAsync(
        IDatabaseConnectionFactory connectionFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        await MySqlRuntimeReadiness.ValidateServerAsync(connectionFactory, cancellationToken);
        await using var session = await connectionFactory.OpenSessionAsync(cancellationToken);
        var version = await session.Connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT Version FROM MySqlCategorySchemaVersion WHERE Domain = 'category-admin-grants';",
            cancellationToken: cancellationToken));
        if (version is not >= MySqlCategoryAdministrativeGrantSchemaInitializer.CurrentSchemaVersion)
            throw new InvalidOperationException("The administrative-grant schema is not at the required version.");

        var tables = (await session.Connection.QueryAsync<string>(new CommandDefinition("""
            SELECT TABLE_NAME FROM information_schema.TABLES
            WHERE TABLE_SCHEMA = DATABASE()
              AND TABLE_NAME IN ('TwCategoryAdministrativeGrant', 'TwCategoryAdministrativeGrantAudit');
            """, cancellationToken: cancellationToken))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!tables.Contains("TwCategoryAdministrativeGrant") || !tables.Contains("TwCategoryAdministrativeGrantAudit"))
            throw new InvalidOperationException("The administrative-grant tables are incomplete.");

        var columns = (await session.Connection.QueryAsync<ColumnRow>(new CommandDefinition("""
            SELECT TABLE_NAME AS TableName, COLUMN_NAME AS ColumnName
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
              AND TABLE_NAME IN ('TwCategoryAdministrativeGrant', 'TwCategoryAdministrativeGrantAudit');
            """, cancellationToken: cancellationToken)))
            .Select(column => $"{column.TableName}.{column.ColumnName}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var required in new[]
                 {
                     "TwCategoryAdministrativeGrant.GrantId",
                     "TwCategoryAdministrativeGrant.ScopeFingerprint",
                     "TwCategoryAdministrativeGrant.GrantState",
                     "TwCategoryAdministrativeGrant.Version",
                     "TwCategoryAdministrativeGrant.ReplayMode",
                     "TwCategoryAdministrativeGrant.LastAuditSequence",
                     "TwCategoryAdministrativeGrantAudit.GrantId",
                     "TwCategoryAdministrativeGrantAudit.EventKey",
                     "TwCategoryAdministrativeGrantAudit.EventKind",
                     "TwCategoryAdministrativeGrantAudit.CorrelationId"
                 })
        {
            if (!columns.Contains(required))
                throw new InvalidOperationException("The administrative-grant columns are incomplete.");
        }

        var database = await session.Connection.ExecuteScalarAsync<string>(new CommandDefinition("SELECT DATABASE();", cancellationToken: cancellationToken));
        if (string.IsNullOrWhiteSpace(database))
            throw new InvalidOperationException("The administrative-grant connection has no selected database.");
        return new(database, MySqlCategoryAdministrativeGrantSchemaInitializer.CurrentSchemaVersion);
    }

    private sealed record ColumnRow(string TableName, string ColumnName);
}

public sealed record MySqlCategoryAdministrativeGrantReadinessProof(string DatabaseName, int SchemaVersion);
