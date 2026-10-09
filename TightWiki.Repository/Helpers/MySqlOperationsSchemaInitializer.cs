using Dapper;
using TightWiki.Library;
using TightWiki.Library.Database;

namespace TightWiki.Repository.Helpers;

/// <summary>Initializes logging, statistics, and emoji tables in an explicit MySQL database.</summary>
public sealed class MySqlOperationsSchemaInitializer
{
    public async Task InitializeAsync(IDatabaseConnectionFactory connectionFactory, CancellationToken cancellationToken = default)
    {
        await using var schemaLock = await MySqlSchemaInitializationLock.AcquireAsync(connectionFactory, cancellationToken);
        await using var session = await connectionFactory.OpenSessionAsync(cancellationToken);
        await using var transaction = await session.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var statement in EmbeddedResourceReader.LoadText("MySql.Operations.V1__OperationsSchema.sql")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                await session.Connection.ExecuteAsync(new CommandDefinition(statement, transaction: transaction.Transaction, cancellationToken: cancellationToken));
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }
}
