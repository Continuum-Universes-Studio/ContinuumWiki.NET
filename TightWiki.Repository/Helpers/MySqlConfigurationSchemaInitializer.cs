using Dapper;
using TightWiki.Library;
using TightWiki.Library.Database;

namespace TightWiki.Repository.Helpers
{
    /// <summary>Initializes a disposable MySQL configuration database to schema version one.</summary>
    public sealed class MySqlConfigurationSchemaInitializer
    {
        public async Task InitializeAsync(IDatabaseConnectionFactory connectionFactory, CancellationToken cancellationToken = default)
        {
            await using var schemaLock = await MySqlSchemaInitializationLock.AcquireAsync(connectionFactory, cancellationToken);
            await using var session = await connectionFactory.OpenSessionAsync(cancellationToken);
            await using var transaction = await session.BeginTransactionAsync(cancellationToken);

            foreach (var resource in new[] { "MySql.ConfigurationSchema.sql", "MySql.Configuration.V2__ConfigurationSupportSchema.sql" })
            {
                foreach (var statement in EmbeddedResourceReader.LoadText(resource)
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    await session.Connection.ExecuteAsync(new CommandDefinition(statement, transaction: transaction.Transaction, cancellationToken: cancellationToken));
            }

            await transaction.CommitAsync(cancellationToken);
        }
    }
}
