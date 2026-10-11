using Dapper;
using TightWiki.Library.Database;

namespace TightWiki.Repository.Helpers;

/// <summary>Read-only readiness checks for explicit category-provider activation.</summary>
public static class MySqlCategoryPersistenceReadiness
{
    public static async Task<MySqlCategoryPersistenceReadinessProof> ValidateAsync(
        IDatabaseConnectionFactory connectionFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);

        await MySqlRuntimeReadiness.ValidateServerAsync(connectionFactory, cancellationToken);
        await using var session = await connectionFactory.OpenSessionAsync(cancellationToken);

        var version = await session.Connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT Version FROM MySqlCategorySchemaVersion WHERE Domain = 'category';",
            cancellationToken: cancellationToken));
        if (version is not >= MySqlCategorySchemaInitializer.CurrentSchemaVersion)
            throw new InvalidOperationException("The category schema is not at the required version.");

        var tables = await session.Connection.QueryAsync<string>(new CommandDefinition("""
            SELECT TABLE_NAME
            FROM information_schema.TABLES
            WHERE TABLE_SCHEMA = DATABASE()
              AND TABLE_NAME IN ('PageCategoryIndexState', 'PageCategoryMembership');
            """, cancellationToken: cancellationToken));
        var actual = tables.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actual.Contains("PageCategoryIndexState") || !actual.Contains("PageCategoryMembership"))
            throw new InvalidOperationException("The category schema tables are incomplete.");

        var columns = await session.Connection.QueryAsync<ColumnRow>(new CommandDefinition("""
            SELECT TABLE_NAME AS TableName, COLUMN_NAME AS ColumnName
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
              AND TABLE_NAME IN ('PageCategoryIndexState', 'PageCategoryMembership');
            """, cancellationToken: cancellationToken));
        var actualColumns = columns
            .Select(column => $"{column.TableName}.{column.ColumnName}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requiredColumns = new[]
        {
            "PageCategoryIndexState.PageId",
            "PageCategoryIndexState.IndexedPageRevision",
            "PageCategoryIndexState.IndexState",
            "PageCategoryIndexState.IndexGeneration",
            "PageCategoryIndexState.IndexedAtUtc",
            "PageCategoryMembership.PageId",
            "PageCategoryMembership.CategoryNavigation",
            "PageCategoryMembership.SortKey",
            "PageCategoryMembership.SourcePageRevision",
            "PageCategoryMembership.IndexGeneration",
            "PageCategoryMembership.IndexedAtUtc"
        };
        if (requiredColumns.Any(required => !actualColumns.Contains(required)))
            throw new InvalidOperationException("The category schema columns are incomplete.");

        var database = await session.Connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT DATABASE();",
            cancellationToken: cancellationToken));
        if (string.IsNullOrWhiteSpace(database))
            throw new InvalidOperationException("The category persistence connection has no selected database.");
        return new(database, MySqlCategorySchemaInitializer.CurrentSchemaVersion);
    }

    private sealed record ColumnRow(string TableName, string ColumnName);
}

/// <summary>Read-only proof produced after MariaDB and category schema checks.</summary>
public sealed record MySqlCategoryPersistenceReadinessProof(
    string DatabaseName,
    int SchemaVersion);
