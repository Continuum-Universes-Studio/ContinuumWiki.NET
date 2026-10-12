using Dapper;
using TightWiki.Library.Database;

namespace TightWiki.Repository.Helpers;

/// <summary>Read-only proof required before explicit provider activation.</summary>
public static class MySqlCategoryRecoveryDurabilityReadiness
{
    public static async Task<MySqlCategoryRecoveryDurabilityReadinessProof> ValidateAsync(
        IDatabaseConnectionFactory connectionFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);

        await MySqlRuntimeReadiness.ValidateServerAsync(connectionFactory, cancellationToken);
        await using var session = await connectionFactory.OpenSessionAsync(cancellationToken);
        var version = await session.Connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT Version FROM MySqlCategorySchemaVersion WHERE Domain = 'category-recovery';",
            cancellationToken: cancellationToken));
        if (version is not >= MySqlCategoryRecoveryDurabilitySchemaInitializer.CurrentSchemaVersion)
            throw new InvalidOperationException("The category-recovery durability schema is not at the required version.");

        var tables = (await session.Connection.QueryAsync<string>(new CommandDefinition("""
            SELECT TABLE_NAME
            FROM information_schema.TABLES
            WHERE TABLE_SCHEMA = DATABASE()
              AND TABLE_NAME IN ('TwCategoryRecoveryOperation', 'TwCategoryRecoveryAuditEvent');
            """, cancellationToken: cancellationToken))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!tables.Contains("TwCategoryRecoveryOperation") || !tables.Contains("TwCategoryRecoveryAuditEvent"))
            throw new InvalidOperationException("The category-recovery durability tables are incomplete.");

        var columns = (await session.Connection.QueryAsync<ColumnRow>(new CommandDefinition("""
            SELECT TABLE_NAME AS TableName, COLUMN_NAME AS ColumnName
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
              AND TABLE_NAME IN ('TwCategoryRecoveryOperation', 'TwCategoryRecoveryAuditEvent');
            """, cancellationToken: cancellationToken)))
            .Select(column => $"{column.TableName}.{column.ColumnName}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var required in new[]
                 {
                     "TwCategoryRecoveryOperation.OperationId",
                     "TwCategoryRecoveryOperation.ScopeFingerprint",
                     "TwCategoryRecoveryOperation.ScopeJson",
                     "TwCategoryRecoveryOperation.CommittedPosition",
                     "TwCategoryRecoveryOperation.CheckpointGeneration",
                     "TwCategoryRecoveryOperation.LastOutcomeSequence",
                     "TwCategoryRecoveryOperation.LastAuditSequence",
                     "TwCategoryRecoveryAuditEvent.OperationId",
                     "TwCategoryRecoveryAuditEvent.EventSequence",
                     "TwCategoryRecoveryAuditEvent.EventKey",
                     "TwCategoryRecoveryAuditEvent.EventKind",
                     "TwCategoryRecoveryAuditEvent.Outcome"
                 })
        {
            if (!columns.Contains(required))
                throw new InvalidOperationException("The category-recovery durability columns are incomplete.");
        }

        var database = await session.Connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT DATABASE();",
            cancellationToken: cancellationToken));
        if (string.IsNullOrWhiteSpace(database))
            throw new InvalidOperationException("The category-recovery durability connection has no selected database.");
        return new(database, MySqlCategoryRecoveryDurabilitySchemaInitializer.CurrentSchemaVersion);
    }

    private sealed record ColumnRow(string TableName, string ColumnName);
}

public sealed record MySqlCategoryRecoveryDurabilityReadinessProof(
    string DatabaseName,
    int SchemaVersion);
