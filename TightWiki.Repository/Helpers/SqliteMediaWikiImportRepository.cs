using TightWiki.Plugin.Interfaces.Persistence;
using TightWiki.Plugin.Models;

namespace TightWiki.Repository.Helpers;

/// <summary>
/// Native SQLite installations do not have MediaWiki import provenance. The
/// explicit no-op adapter keeps their established rendering behavior intact.
/// </summary>
public sealed class SqliteMediaWikiImportRepository : ITwMediaWikiImportRepository
{
    public Task<TwMediaWikiRenderSource> GetRenderSourceAsync(int targetPageId, int targetRevision, CancellationToken cancellationToken = default)
        => Task.FromResult(TwMediaWikiRenderSource.Native);

    public Task<TwMediaWikiPageTarget?> ResolvePageAsync(long importBatchId, string sourceTitle, CancellationToken cancellationToken = default)
        => Task.FromResult<TwMediaWikiPageTarget?>(null);

    public Task<TwMediaWikiMediaTarget?> ResolveMediaAsync(long importBatchId, string sourcePageId, string sourceRevisionId, string sourceFileName, CancellationToken cancellationToken = default)
        => Task.FromResult<TwMediaWikiMediaTarget?>(null);

    public Task<TwMediaWikiTemplateTarget> ResolveTemplateAsync(long importBatchId, string sourceTitle, DateTime? asOfUtc = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new TwMediaWikiTemplateTarget(TwMediaWikiTemplateResolutionStatus.Missing, sourceTitle));

    public Task<string> GetTemplateCacheVersionAsync(long importBatchId, DateTime? asOfUtc = null, CancellationToken cancellationToken = default)
        => Task.FromResult("native");

    public Task<TwMediaWikiModuleTarget> ResolveModuleAsync(long importBatchId, string sourceTitle, DateTime? asOfUtc = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new TwMediaWikiModuleTarget(TwMediaWikiModuleResolutionStatus.Missing, sourceTitle));

    public Task<string> GetModuleCacheVersionAsync(long importBatchId, DateTime? asOfUtc = null, CancellationToken cancellationToken = default)
        => Task.FromResult("native");
}
