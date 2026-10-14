using Dapper;
using TightWiki.Library;
using TightWiki.Library.Database;

namespace TightWiki.Repository.Helpers;

/// <summary>
/// Explicitly provisions the administrative-grant schema. Application startup
/// never invokes this initializer.
/// </summary>
public sealed class MySqlCategoryAdministrativeGrantSchemaInitializer
{
    public const int CurrentSchemaVersion = 3;

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
                         EmbeddedResourceReader.LoadText("MySql.Category.V3__CategoryAdministrativeGrants.sql"))
                     .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                await session.Connection.ExecuteAsync(new CommandDefinition(statement, transaction: transaction.Transaction, cancellationToken: cancellationToken));
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
