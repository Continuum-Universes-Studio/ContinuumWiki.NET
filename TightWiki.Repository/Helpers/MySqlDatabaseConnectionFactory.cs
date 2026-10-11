using System.Data.Common;
using Dapper;
using MySqlConnector;
using TightWiki.Library;
using TightWiki.Library.Database;

namespace TightWiki.Repository.Helpers
{
    /// <summary>MySQL implementation of the provider-neutral database session boundary.</summary>
    public sealed class MySqlDatabaseConnectionFactory : IDatabaseConnectionFactory
    {
        private readonly string _connectionString;

        public MySqlDatabaseConnectionFactory(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException("A MySQL connection string is required.", nameof(connectionString));
            }

            var builder = new MySqlConnectionStringBuilder(connectionString)
            {
                Pooling = true
            };
            // Provider-neutral repositories return Guid identity values from
            // MySQL CHAR(36) columns. Register the handler at the provider
            // boundary so direct repository consumers and the application
            // runtime use the same mapping.
            SqlMapper.AddTypeHandler(new GuidTypeHandler());
            _connectionString = builder.ConnectionString;
        }

        public async ValueTask<IDatabaseSession> OpenSessionAsync(CancellationToken cancellationToken = default)
        {
            var connection = new MySqlConnection(_connectionString);
            try
            {
                await connection.OpenAsync(cancellationToken);
                return new DatabaseSession(connection);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        private sealed class DatabaseSession(MySqlConnection connection) : IDatabaseSession
        {
            private readonly MySqlConnection _connection = connection;
            private bool _disposed;

            public DbConnection Connection => _connection;

            public async ValueTask<IDatabaseTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return new DatabaseTransaction(await _connection.BeginTransactionAsync(cancellationToken));
            }

            public ValueTask DisposeAsync()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    return _connection.DisposeAsync();
                }

                return ValueTask.CompletedTask;
            }
        }

        private sealed class DatabaseTransaction(MySqlTransaction transaction) : IDatabaseTransaction
        {
            private readonly MySqlTransaction _transaction = transaction;
            private bool _disposed;

            public DbTransaction Transaction => _transaction;
            public Task CommitAsync(CancellationToken cancellationToken = default) => _transaction.CommitAsync(cancellationToken);
            public Task RollbackAsync(CancellationToken cancellationToken = default) => _transaction.RollbackAsync(cancellationToken);

            public ValueTask DisposeAsync()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    return _transaction.DisposeAsync();
                }

                return ValueTask.CompletedTask;
            }
        }
    }
}
