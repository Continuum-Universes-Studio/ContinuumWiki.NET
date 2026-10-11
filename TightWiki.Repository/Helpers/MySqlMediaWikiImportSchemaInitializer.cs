using Dapper;
using TightWiki.Library;
using TightWiki.Library.Database;

namespace TightWiki.Repository.Helpers;

/// <summary>
/// Explicitly provisions the MediaWiki import provenance schema. This is not part
/// of ordinary application startup; the import CLI invokes it after target
/// schema preflight has established a disposable destination.
/// </summary>
public sealed class MySqlMediaWikiImportSchemaInitializer
{
    public async Task InitializeAsync(IDatabaseConnectionFactory connectionFactory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        await using var schemaLock = await MySqlSchemaInitializationLock.AcquireAsync(connectionFactory, cancellationToken);
        await using var session = await connectionFactory.OpenSessionAsync(cancellationToken);
        await using var transaction = await session.BeginTransactionAsync(cancellationToken);
        foreach (var resource in new[] { "MySql.Import.V1__MediaWikiImportSchema.sql", "MySql.Import.V2__MediaWikiMediaImportSchema.sql" })
        foreach (var statement in MariaDbSqlCompatibility.Normalize(
                     EmbeddedResourceReader.LoadText(resource))
                     .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            await session.Connection.ExecuteAsync(new CommandDefinition(
                statement, transaction: transaction.Transaction, cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
