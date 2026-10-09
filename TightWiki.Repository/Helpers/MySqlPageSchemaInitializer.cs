using Dapper;
using TightWiki.Library;
using TightWiki.Library.Database;

namespace TightWiki.Repository.Helpers;

/// <summary>Initializes the bounded MySQL page domain for an explicitly managed database.</summary>
public sealed class MySqlPageSchemaInitializer
{
    public async Task InitializeAsync(IDatabaseConnectionFactory connectionFactory, CancellationToken cancellationToken = default)
    {
        await using var schemaLock = await MySqlSchemaInitializationLock.AcquireAsync(connectionFactory, cancellationToken);
        await using var session = await connectionFactory.OpenSessionAsync(cancellationToken);
        await using var transaction = await session.BeginTransactionAsync(cancellationToken);

        foreach (var resource in new[] { "MySql.Pages.V1__PageSchema.sql", "MySql.Pages.V2__PageSupportSchema.sql", "MySql.Pages.V3__DeletedRevisionSchema.sql" })
        {
            foreach (var statement in EmbeddedResourceReader.LoadText(resource)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                await session.Connection.ExecuteAsync(new CommandDefinition(
                    statement, transaction: transaction.Transaction, cancellationToken: cancellationToken));
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
