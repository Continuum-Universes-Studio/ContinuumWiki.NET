using System.Data.Common;
using NTDLS.SqliteDapperWrapper;
using TightWiki.Library.Database;

namespace TightWiki.Repository.Helpers
{
    /// <summary>
    /// Provider-neutral session factory backed by the existing SQLite wrapper.
    /// The legacy factory remains available for plugin compatibility.
    /// </summary>
    public sealed class SqliteDatabaseConnectionFactory : IDatabaseConnectionFactory
    {
        private readonly string _connectionString;

        public SqliteDatabaseConnectionFactory(SqliteManagedFactory factory)
            : this(factory.DefaultConnectionString)
        {
        }

        public SqliteDatabaseConnectionFactory(string connectionString)
        {
            _connectionString = string.IsNullOrWhiteSpace(connectionString)
                ? throw new ArgumentException("A SQLite connection string is required.", nameof(connectionString))
                : connectionString;
        }

        public async ValueTask<IDatabaseSession> OpenSessionAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var instance = new SqliteManagedInstance(_connectionString);
            try
            {
                if (instance.NativeConnection.State != System.Data.ConnectionState.Open)
                {
                    await instance.NativeConnection.OpenAsync(cancellationToken);
                }

                return new SqliteDatabaseSession(instance);
            }
            catch
            {
                instance.Dispose();
                throw;
            }
        }

        private sealed class SqliteDatabaseSession(SqliteManagedInstance instance) : IDatabaseSession
        {
            private readonly SqliteManagedInstance _instance = instance;
            private bool _disposed;

            public DbConnection Connection => _instance.NativeConnection;

            public async ValueTask<IDatabaseTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return new DatabaseTransaction(await Connection.BeginTransactionAsync(cancellationToken));
            }

            public ValueTask DisposeAsync()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    _instance.Dispose();
                }

                return ValueTask.CompletedTask;
            }
        }

        private sealed class DatabaseTransaction(DbTransaction transaction) : IDatabaseTransaction
        {
            private readonly DbTransaction _transaction = transaction;
            private bool _disposed;

            public DbTransaction Transaction => _transaction;

            public Task CommitAsync(CancellationToken cancellationToken = default)
                => _transaction.CommitAsync(cancellationToken);

            public Task RollbackAsync(CancellationToken cancellationToken = default)
                => _transaction.RollbackAsync(cancellationToken);

            public ValueTask DisposeAsync()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    _transaction.Dispose();
                }

                return ValueTask.CompletedTask;
            }
        }
    }
}
