using Dapper;
using NTDLS.SqliteDapperWrapper;
using TightWiki.Library.Database;
using TightWiki.Repository.Helpers;

namespace TightWiki.Tests.Unit
{
    public class DatabaseAdapterTests
    {
        [Fact]
        public async Task SQLiteAdapterCreatesAndDisposesAConnection()
        {
            var path = TemporaryDatabasePath();
            try
            {
                var factory = new SqliteDatabaseConnectionFactory(new SqliteManagedFactory($"Data Source={path}"));
                await using var session = await factory.OpenSessionAsync();

                Assert.Equal(System.Data.ConnectionState.Open, session.Connection.State);
                await session.Connection.ExecuteAsync("CREATE TABLE Sample (Id INTEGER PRIMARY KEY, Value TEXT NOT NULL)");

                await session.DisposeAsync();
                Assert.Equal(System.Data.ConnectionState.Closed, session.Connection.State);
            }
            finally
            {
                DeleteDatabase(path);
            }
        }

        [Fact]
        public async Task SQLiteAdapterCommitsTransactions()
        {
            var path = TemporaryDatabasePath();
            try
            {
                var factory = new SqliteDatabaseConnectionFactory(new SqliteManagedFactory($"Data Source={path}"));
                await using var session = await factory.OpenSessionAsync();
                await session.Connection.ExecuteAsync("CREATE TABLE Sample (Id INTEGER PRIMARY KEY, Value TEXT NOT NULL)");

                await using (var transaction = await session.BeginTransactionAsync())
                {
                    await session.Connection.ExecuteAsync("INSERT INTO Sample (Id, Value) VALUES (@Id, @Value)",
                        new { Id = 1, Value = "committed" }, transaction.Transaction);
                    await transaction.CommitAsync();
                }

                Assert.Equal("committed", await session.Connection.QuerySingleAsync<string>("SELECT Value FROM Sample WHERE Id = 1"));
            }
            finally
            {
                DeleteDatabase(path);
            }
        }

        [Fact]
        public async Task SQLiteAdapterRollsBackTransactions()
        {
            var path = TemporaryDatabasePath();
            try
            {
                var factory = new SqliteDatabaseConnectionFactory(new SqliteManagedFactory($"Data Source={path}"));
                await using var session = await factory.OpenSessionAsync();
                await session.Connection.ExecuteAsync("CREATE TABLE Sample (Id INTEGER PRIMARY KEY, Value TEXT NOT NULL)");

                await using (var transaction = await session.BeginTransactionAsync())
                {
                    await session.Connection.ExecuteAsync("INSERT INTO Sample (Id, Value) VALUES (@Id, @Value)",
                        new { Id = 1, Value = "rolled back" }, transaction.Transaction);
                    await transaction.RollbackAsync();
                }

                Assert.Equal(0, await session.Connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM Sample"));
            }
            finally
            {
                DeleteDatabase(path);
            }
        }

        [Fact]
        public async Task MySqlAdapterPropagatesConnectionFailureWithoutExposingPassword()
        {
            var factory = new MySqlDatabaseConnectionFactory(
                "Server=127.0.0.1;Port=1;Database=unavailable;User ID=test;Password=secret-test-only;Connection Timeout=1;");

            var exception = await Assert.ThrowsAnyAsync<Exception>(async () => await factory.OpenSessionAsync());

            Assert.DoesNotContain("secret-test-only", exception.ToString(), StringComparison.Ordinal);
        }

        private static string TemporaryDatabasePath()
            => Path.Combine(Path.GetTempPath(), $"continuumwiki-adapter-{Guid.NewGuid():N}.db");

        private static void DeleteDatabase(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
