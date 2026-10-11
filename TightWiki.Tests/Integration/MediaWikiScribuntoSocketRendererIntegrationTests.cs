using System.Security.Cryptography;
using System.Text;
using TightWiki.Engine.MediaWiki;
using TightWiki.Plugin;
using TightWiki.Plugin.Interfaces.Persistence;
using TightWiki.Plugin.Models;

namespace TightWiki.Tests.Integration;

public sealed class MediaWikiScribuntoSocketRendererIntegrationTests
{
    [Fact]
    public async Task RealSocketInvokeRendersSyntheticRevisionedModuleSafely()
    {
        var socketPath = Environment.GetEnvironmentVariable("CW_SCRIBUNTO_SOCKET_PATH");
        if (string.IsNullOrWhiteSpace(socketPath)) return; // The real-host command supplies this opt-in variable.

        var repository = new SyntheticImportRepository();
        var renderer = new MediaWikiWikitextRenderer(repository, new TwConfiguration { BasePath = "/wiki" }, new MediaWikiWikitextRenderOptions
        {
            MaxExpansionOperations = 100_000,
            MaxExpansionOutputCharacters = 100_000,
            MaxExpansionTime = TimeSpan.FromSeconds(2),
            ScribuntoExecutor = new MediaWikiScribuntoSocketExecutor(new MediaWikiScribuntoUnixSocketClient(socketPath!, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)))
        });
        var source = new TwMediaWikiRenderSource(TwPageSourceFormat.ImportedMediaWiki, 77, "page-1", "page-rev-2", "Greetings", SourceNamespaceId: "0", SourceWikiIdentity: "synthetic-wiki", ContentModel: "wikitext");

        var result = await renderer.RenderAsync("{{#invoke:Greetings|hello|name=Continuum|second|unsafe=<script>alert(1)</script>}}", source);

        Assert.True(result.HtmlResult.Contains("Continuum", StringComparison.Ordinal),
            $"HTML={result.HtmlResult}\nDiagnostics={string.Join("; ", result.Diagnostics.Select(value => value.Code + ":" + value.Message))}\nDependencies={string.Join("; ", (result.ScribuntoDependencies ?? []).Select(value => value.Name + ":" + value.Status))}");
        Assert.Contains("second", result.HtmlResult);
        Assert.DoesNotContain("<script", result.HtmlResult, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert(1)", result.HtmlResult, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.ScribuntoDependencies ?? [], value => value.Name == "Module:Greetings" && value.SourceRevisionId == "module-rev-2");
        Assert.DoesNotContain(result.Diagnostics, value => value.Code is "ScribuntoWorkerUnavailable" or "ScribuntoWorkerProtocolError");
    }

    [Fact]
    public async Task RealSocketInvokeRendersSyntheticMediaWikiCompatibilityModule()
    {
        var socketPath = Environment.GetEnvironmentVariable("CW_SCRIBUNTO_SOCKET_PATH");
        if (string.IsNullOrWhiteSpace(socketPath)) return;

        var renderer = new MediaWikiWikitextRenderer(new SyntheticImportRepository(), new TwConfiguration { BasePath = "/wiki" }, new MediaWikiWikitextRenderOptions
        {
            MaxExpansionOperations = 100_000,
            MaxExpansionOutputCharacters = 100_000,
            MaxExpansionTime = TimeSpan.FromSeconds(2),
            ScribuntoExecutor = new MediaWikiScribuntoSocketExecutor(new MediaWikiScribuntoUnixSocketClient(socketPath!, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)))
        });
        var source = new TwMediaWikiRenderSource(TwPageSourceFormat.ImportedMediaWiki, 77, "page-1", "page-rev-2", "MediaWikiExample", SourceNamespaceId: "0", SourceWikiIdentity: "synthetic-wiki", ContentModel: "wikitext");

        var result = await renderer.RenderAsync("{{#invoke:MediaWikiExample|render|name= Continuum }}", source);

        Assert.Contains("Continuum", result.HtmlResult, StringComparison.Ordinal);
        Assert.DoesNotContain("cw-mediawiki-unsupported", result.HtmlResult, StringComparison.Ordinal);
        Assert.Contains(result.ScribuntoDependencies ?? [], value => value.Name == "Module:MediaWikiExample" && value.SourceRevisionId == "module-rev-3");
        Assert.DoesNotContain(result.Diagnostics, value => value.Code is "ScribuntoWorkerUnavailable" or "ScribuntoWorkerProtocolError");
    }

    private sealed class SyntheticImportRepository : ITwMediaWikiImportRepository
    {
        private const string Module = "return { hello = function(frame) return frame.args.name .. ' / ' .. frame.args[1] .. ' / ' .. frame.args.unsafe end }";
        private const string MediaWikiModule = "return { render = function(frame) return mw.text.trim(frame.args.name) end }";

        public Task<TwMediaWikiRenderSource> GetRenderSourceAsync(int targetPageId, int targetRevision, CancellationToken cancellationToken = default) => Task.FromResult(TwMediaWikiRenderSource.Native);
        public Task<TwMediaWikiPageTarget?> ResolvePageAsync(long importBatchId, string sourceTitle, CancellationToken cancellationToken = default) => Task.FromResult<TwMediaWikiPageTarget?>(null);
        public Task<TwMediaWikiMediaTarget?> ResolveMediaAsync(long importBatchId, string sourcePageId, string sourceRevisionId, string sourceFileName, CancellationToken cancellationToken = default) => Task.FromResult<TwMediaWikiMediaTarget?>(null);
        public Task<TwMediaWikiTemplateTarget> ResolveTemplateAsync(long importBatchId, string sourceTitle, DateTime? asOfUtc = null, CancellationToken cancellationToken = default) => Task.FromResult(new TwMediaWikiTemplateTarget(TwMediaWikiTemplateResolutionStatus.Missing, sourceTitle));
        public Task<string> GetTemplateCacheVersionAsync(long importBatchId, DateTime? asOfUtc = null, CancellationToken cancellationToken = default) => Task.FromResult("synthetic");
        public Task<TwMediaWikiModuleTarget> ResolveModuleAsync(long importBatchId, string sourceTitle, DateTime? asOfUtc = null, CancellationToken cancellationToken = default)
        {
            if (sourceTitle.Equals("Greetings", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new TwMediaWikiModuleTarget(TwMediaWikiModuleResolutionStatus.Resolved, sourceTitle, "module-page-2", "module-rev-2", 2, Module, null, "Scribunto"));
            if (sourceTitle.Equals("MediaWikiExample", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new TwMediaWikiModuleTarget(TwMediaWikiModuleResolutionStatus.Resolved, sourceTitle, "module-page-3", "module-rev-3", 3, MediaWikiModule, null, "Scribunto"));
            return Task.FromResult(new TwMediaWikiModuleTarget(TwMediaWikiModuleResolutionStatus.Missing, sourceTitle));
        }
    }
}
