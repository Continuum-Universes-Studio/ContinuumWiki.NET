using TightWiki.Plugin.Models;

namespace TightWiki.Plugin.Interfaces.Persistence;

#pragma warning disable CS1591

/// <summary>
/// Selects the source format for one target page revision and resolves the
/// source titles retained by the MediaWiki import provenance tables.
/// </summary>
public interface ITwMediaWikiImportRepository
{
    Task<TwMediaWikiRenderSource> GetRenderSourceAsync(int targetPageId, int targetRevision, CancellationToken cancellationToken = default);
    Task<TwMediaWikiPageTarget?> ResolvePageAsync(long importBatchId, string sourceTitle, CancellationToken cancellationToken = default);
    Task<TwMediaWikiMediaTarget?> ResolveMediaAsync(long importBatchId, string sourcePageId, string sourceRevisionId, string sourceFileName, CancellationToken cancellationToken = default);
    Task<TwMediaWikiTemplateTarget> ResolveTemplateAsync(long importBatchId, string sourceTitle, DateTime? asOfUtc = null, CancellationToken cancellationToken = default);
    Task<string> GetTemplateCacheVersionAsync(long importBatchId, DateTime? asOfUtc = null, CancellationToken cancellationToken = default);
    Task<TwMediaWikiModuleTarget> ResolveModuleAsync(long importBatchId, string sourceTitle, DateTime? asOfUtc = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new TwMediaWikiModuleTarget(TwMediaWikiModuleResolutionStatus.Missing, sourceTitle));
    Task<string> GetModuleCacheVersionAsync(long importBatchId, DateTime? asOfUtc = null, CancellationToken cancellationToken = default)
        => Task.FromResult("modules-unavailable");
}

#pragma warning restore CS1591
