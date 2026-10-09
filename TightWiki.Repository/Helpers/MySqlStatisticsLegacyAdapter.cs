using NTDLS.SqliteDapperWrapper;
using TightWiki.Plugin.Interfaces.Persistence;
using TightWiki.Plugin.Interfaces.Repository;
using TightWiki.Plugin.Models;

namespace TightWiki.Repository.Helpers;

/// <summary>Bridges the small statistics surface used by legacy engine handlers to MySQL operations.</summary>
public sealed class MySqlStatisticsLegacyAdapter(ITwApplicationOperationsRepository operations) : ITwStatisticsRepository
{
    public SqliteManagedFactory StatisticsFactory => throw Unsupported();
    public Task IncrementPageViewCount(int pageId) => operations.IncrementPageViewCountAsync(pageId);
    public Task MergePageCompilationStatistics(int pageId, double wikifyTimeMs, int matchCount, int errorCount, int outgoingLinkCount, int tagCount, int processedBodySize, int bodySize)
        => operations.MergePageCompilationStatisticsAsync(pageId, wikifyTimeMs, matchCount, errorCount, outgoingLinkCount, tagCount, processedBodySize, bodySize);
    public Task<int> GetPageTotalViewCount(int pageId) => operations.GetPageTotalViewCountAsync(pageId);
    public Task PurgePageStatistics() => operations.PurgePageStatisticsAsync();
    public async Task<List<TwPageStatistics>> GetPageStatisticsPaged(int pageNumber, string? orderBy = null, string? orderByDirection = null, int? pageSize = null)
        => (await operations.GetPageStatisticsPagedAsync(pageNumber, orderBy, orderByDirection, pageSize)).ToList();
    public Task<int> DeletePageStatisticsByPageId(int pageId) => throw Unsupported();

    private static NotSupportedException Unsupported() => new("The SQLite statistics factory or page-specific deletion operation is unavailable in MySQL mode.");
}
