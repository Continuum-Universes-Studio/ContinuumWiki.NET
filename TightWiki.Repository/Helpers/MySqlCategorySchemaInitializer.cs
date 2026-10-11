using Dapper;
using TightWiki.Library;
using TightWiki.Library.Database;

namespace TightWiki.Repository.Helpers;

/// <summary>
/// Explicitly provisions the category schema for an approved managed
/// database. It is intentionally not called by application startup.
/// </summary>
public sealed class MySqlCategorySchemaInitializer
{
    public const int CurrentSchemaVersion = 1;

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
                         EmbeddedResourceReader.LoadText("MySql.Category.V1__CategorySchema.sql"))
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
