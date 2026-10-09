using System.Data.Common;

namespace TightWiki.Library.Database
{
    /// <summary>Creates provider-neutral database sessions.</summary>
    public interface IDatabaseConnectionFactory
    {
        ValueTask<IDatabaseSession> OpenSessionAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>Owns one open connection and any transactions created from it.</summary>
    public interface IDatabaseSession : IAsyncDisposable
    {
        DbConnection Connection { get; }
        ValueTask<IDatabaseTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>Represents an explicitly owned transaction for a database session.</summary>
    public interface IDatabaseTransaction : IAsyncDisposable
    {
        DbTransaction Transaction { get; }
        Task CommitAsync(CancellationToken cancellationToken = default);
        Task RollbackAsync(CancellationToken cancellationToken = default);
    }
}
