using Dapper;
using TightWiki.Library.Database;

namespace TightWiki.Repository.Helpers;

internal sealed class MySqlSchemaInitializationLock : IAsyncDisposable
{
    private readonly IDatabaseSession _session;

    private MySqlSchemaInitializationLock(IDatabaseSession session) => _session = session;

    public static async Task<MySqlSchemaInitializationLock> AcquireAsync(IDatabaseConnectionFactory connectionFactory, CancellationToken cancellationToken)
    {
        var session = await connectionFactory.OpenSessionAsync(cancellationToken);
        try
        {
            var acquired = await session.Connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT GET_LOCK('ContinuumWiki.MySqlSchemaInitializer', 120);", cancellationToken: cancellationToken));
            if (acquired != 1) throw new TimeoutException("Timed out waiting for the MySQL schema initialization lock.");
            return new MySqlSchemaInitializationLock(session);
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _session.Connection.ExecuteAsync("SELECT RELEASE_LOCK('ContinuumWiki.MySqlSchemaInitializer');");
        }
        finally
        {
            await _session.DisposeAsync();
        }
    }
}
