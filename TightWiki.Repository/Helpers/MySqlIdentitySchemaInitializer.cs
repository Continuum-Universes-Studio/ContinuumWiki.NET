using Dapper;
using TightWiki.Library;
using TightWiki.Library.Database;

namespace TightWiki.Repository.Helpers;

/// <summary>Initializes the MySQL Identity/profile/permission slice explicitly.</summary>
public sealed class MySqlIdentitySchemaInitializer
{
    public async Task InitializeAsync(IDatabaseConnectionFactory connectionFactory, CancellationToken cancellationToken = default)
    {
        await using var schemaLock = await MySqlSchemaInitializationLock.AcquireAsync(connectionFactory, cancellationToken);
        await using var session = await connectionFactory.OpenSessionAsync(cancellationToken);
        await using var transaction = await session.BeginTransactionAsync(cancellationToken);

        foreach (var statement in MariaDbSqlCompatibility.Normalize(EmbeddedResourceReader
            .LoadText("MySql.Identity.V1__IdentityAndUsersSchema.sql"))
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            await session.Connection.ExecuteAsync(new CommandDefinition(
                statement, transaction: transaction.Transaction, cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
