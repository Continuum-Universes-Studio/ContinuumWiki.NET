using Microsoft.Extensions.Logging;
using TightWiki.Plugin.Interfaces.Persistence;
using TightWiki.Plugin.Interfaces.Repository;
using TightWiki.Plugin.Models;

namespace TightWiki.Repository.Helpers;

/// <summary>Provider-neutral operations adapter over the unchanged SQLite statistics repository.</summary>
public sealed class SqliteApplicationOperationsRepository(ITwStatisticsRepository repository, ITwLoggingRepository loggingRepository) : ITwApplicationOperationsRepository
{
    public Task WriteLogAsync(LogLevel severity, string? text = null, string? exceptionText = null, string? stackTrace = null, CancellationToken cancellationToken = default) => loggingRepository.WriteLog(severity, text, exceptionText, stackTrace).WaitAsync(cancellationToken);
    public Task<int> GetExceptionCountAsync(CancellationToken cancellationToken = default) => loggingRepository.GetExceptionCount().WaitAsync(cancellationToken);
    public Task PurgeLogsAsync(CancellationToken cancellationToken = default) => loggingRepository.PurgeLogs().WaitAsync(cancellationToken);
    public async Task<IReadOnlyList<TwEventLogSeverity>> GetSeveritiesAsync(CancellationToken cancellationToken = default) => await loggingRepository.GetSeverities().WaitAsync(cancellationToken);
    public async Task<IReadOnlyList<TwLogEntry>> GetLogEntriesPagedAsync(int pageNumber, string? orderBy = null, string? orderByDirection = null, string? severity = null, int? pageSize = null, CancellationToken cancellationToken = default) => await loggingRepository.GetLogEntriesPaged(pageNumber, orderBy, orderByDirection, severity).WaitAsync(cancellationToken);
    public Task<TwLogEntry> GetLogEntryByIdAsync(int id, CancellationToken cancellationToken = default) => loggingRepository.GetLogEntryById(id).WaitAsync(cancellationToken);
    public async Task<int> IncrementPageViewCountAsync(int pageId, CancellationToken cancellationToken = default)
    {
        await repository.IncrementPageViewCount(pageId).WaitAsync(cancellationToken);
        return 1;
    }
    public Task MergePageCompilationStatisticsAsync(int pageId, double wikifyTimeMs, int matchCount, int errorCount, int outgoingLinkCount, int tagCount, int processedBodySize, int bodySize, CancellationToken cancellationToken = default)
        => repository.MergePageCompilationStatistics(pageId, wikifyTimeMs, matchCount, errorCount, outgoingLinkCount, tagCount, processedBodySize, bodySize).WaitAsync(cancellationToken);
    public Task<int> GetPageTotalViewCountAsync(int pageId, CancellationToken cancellationToken = default) => repository.GetPageTotalViewCount(pageId).WaitAsync(cancellationToken);
    public async Task<IReadOnlyList<TwPageStatistics>> GetPageStatisticsPagedAsync(int pageNumber, string? orderBy = null, string? orderByDirection = null, int? pageSize = null, CancellationToken cancellationToken = default) => await repository.GetPageStatisticsPaged(pageNumber, orderBy, orderByDirection, pageSize).WaitAsync(cancellationToken);
    public Task PurgePageStatisticsAsync(CancellationToken cancellationToken = default) => repository.PurgePageStatistics().WaitAsync(cancellationToken);
    public Task<int> UpsertEmojiAsync(TightWiki.Plugin.Models.TwUpsertEmoji emoji, CancellationToken cancellationToken = default) => Unsupported<int>();
    private static Task Unsupported() => Task.FromException(new NotSupportedException("This SQLite compatibility adapter exposes only reader-facing page statistics operations."));
    private static Task<T> Unsupported<T>() => Task.FromException<T>(new NotSupportedException("This SQLite compatibility adapter exposes only reader-facing page statistics operations."));
}
