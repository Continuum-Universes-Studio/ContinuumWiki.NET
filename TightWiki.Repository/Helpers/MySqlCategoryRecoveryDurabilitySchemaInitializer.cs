using Dapper;
using TightWiki.Library;
using TightWiki.Library.Database;

namespace TightWiki.Repository.Helpers;

/// <summary>
/// Explicitly provisions the category-recovery durability schema for an
/// approved disposable or managed MariaDB target. Application startup never
/// calls this initializer.
/// </summary>
public sealed class MySqlCategoryRecoveryDurabilitySchemaInitializer
{
    public const int CurrentSchemaVersion = 2;

    public async Task InitializeAsync(
        IDatabaseConnectionFactory connectionFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);

        await using var schemaLock = await MySqlSchemaInitializationLock.AcquireAsync(connectionFactory, cancellationToken);
        await using var session = await connectionFactory.OpenSessionAsync(cancellationToken);
        await using var transaction = await session.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var statement in MariaDbSqlCompatibility.Normalize(
                         EmbeddedResourceReader.LoadText("MySql.Category.V2__CategoryRecoveryDurability.sql"))
                     .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                await session.Connection.ExecuteAsync(new CommandDefinition(
                    statement,
                    transaction: transaction.Transaction,
                    cancellationToken: cancellationToken));
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
