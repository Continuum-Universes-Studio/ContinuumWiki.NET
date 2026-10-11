using TightWiki.Engine.MediaWiki;
using TightWiki.Plugin;
using TightWiki.Plugin.Interfaces.Persistence;
using TightWiki.Plugin.Models;
using System.Security.Cryptography;
using System.Text;

namespace TightWiki.Tests.Unit;

public sealed class MediaWikiWikitextRendererTests
{
    [Fact]
    public async Task RendersBasicMediaWikiMarkupAndUsesImportedNavigation()
    {
        var repository = new FakeMediaWikiImportRepository();
        repository.Pages["Lore:世界"] = new("Lore:世界", "Lore::世界", 2);
        var renderer = CreateRenderer(repository);

        var result = await renderer.RenderAsync("""
            = 世界 =
            '''bold''' and ''italic''
            [[Lore:世界|世界 page]] [https://example.com External]
            * one
            * two
            # ordered
            ----
            """, ImportedSource(repository));

        Assert.Contains("<h1>", result.HtmlResult);
        Assert.Contains("<strong>bold</strong>", result.HtmlResult);
        Assert.Contains("<em>italic</em>", result.HtmlResult);
        Assert.Contains("Lore%3A%3A%E4%B8%96%E7%95%8C", result.HtmlResult);
        Assert.Contains("rel=\"nofollow noopener noreferrer\"", result.HtmlResult);
        Assert.Contains("<ul>", result.HtmlResult);
        Assert.Contains("<ol>", result.HtmlResult);
        Assert.Contains("<hr />", result.HtmlResult);
        Assert.Empty(result.UnsupportedConstructs);
    }

    [Fact]
    public async Task RecognizesReferencesCategoriesAndHistoricalMedia()
    {
        var repository = new FakeMediaWikiImportRepository();
        repository.Media["Map.png"] = new("Map.png", "File::Map_png", "Map_png", "image/png", 2, true);
        var renderer = CreateRenderer(repository);
        var source = "[[Category:Maps]]\n[[File:Map.png|thumb|A map]] Text<ref name=source>Source</ref><references />";
        var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));

        var result = await renderer.RenderAsync(source, ImportedSource(repository, revision: "1001"));

        Assert.Contains("cw-mediawiki-categories", result.HtmlResult);
        Assert.Contains("<img", result.HtmlResult);
        Assert.Contains("cite_ref_1", result.HtmlResult);
        Assert.Contains("cw-mediawiki-references", result.HtmlResult);
        Assert.Single(result.Categories);
        Assert.Single(result.References);
        Assert.Equal(sourceHash, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))));
    }

    [Fact]
    public async Task DoesNotEvaluateTemplatesOrEmitImportedHtml()
    {
        var renderer = CreateRenderer(new FakeMediaWikiImportRepository());

        var result = await renderer.RenderAsync("<script>alert(1)</script><img src=x onerror=alert(2)> {{#invoke:Unsafe|run}} [javascript:alert(3) click] [[File:../secret.png]] <nowiki><b>x</b></nowiki>", ImportedSource());

        Assert.DoesNotContain("<script", result.HtmlResult, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", result.HtmlResult, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert(1)", result.HtmlResult, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert(2)", result.HtmlResult, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("File/Binary", result.HtmlResult, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Unsupported MediaWiki construct", result.HtmlResult);
        Assert.Contains("&lt;b&gt;x&lt;/b&gt;", result.HtmlResult);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "UnsupportedParserFunction");
        Assert.NotNull(result.ScribuntoDependencies);
        Assert.Contains(result.ScribuntoDependencies!, dependency => dependency.Name == "Module:Unsafe" && dependency.Status == "ExecutionRejected");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "UnsafeExternalUrl");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "UnsupportedExtensionTag");
    }

    [Fact]
    public async Task ExpandsNamedPositionalDefaultAndNestedTemplatesThroughTheSafeRenderer()
    {
        var repository = new FakeMediaWikiImportRepository();
        repository.Templates["Outer"] = Template("Outer", "Outer {{{name|Unknown}}} / {{{1|Default}}} / {{Inner|value={{{name|Unknown}}}}}");
        repository.Templates["Inner"] = Template("Inner", "Inner {{{value}}}");
        var renderer = CreateRenderer(repository);

        var result = await renderer.RenderAsync("{{Template:Outer|name=Koozarii}}", ImportedSource(repository));

        Assert.Contains("Outer Koozarii / Default / Inner Koozarii", result.HtmlResult);
        Assert.Equal(2, result.TemplateDependencies.Count);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "UnsupportedTemplate");
    }

    [Fact]
    public async Task PreservesEmptyParametersAndBalancedLinksAndAppliesInclusionRules()
    {
        var repository = new FakeMediaWikiImportRepository();
        repository.Pages["Lore:世界"] = new("Lore:世界", "Lore::世界", 2);
        repository.Templates["Show"] = Template("Show", "<noinclude>documentation</noinclude><includeonly>Name={{{name|Unknown}}}; Link={{{link}}}</includeonly>");
        repository.Templates["Only"] = Template("Only", "before<onlyinclude>Only {{{1|default}}}</onlyinclude>after");
        var renderer = CreateRenderer(repository);

        var result = await renderer.RenderAsync("{{Show|name=|link=[[Lore:世界|世界]]}} {{Only|value}}", ImportedSource(repository));

        Assert.Contains("Name=; Link=", result.HtmlResult);
        Assert.Contains("Only value", result.HtmlResult);
        Assert.DoesNotContain("documentation", result.HtmlResult);
        Assert.DoesNotContain("before", result.HtmlResult);
        Assert.Contains("Lore%3A%3A%E4%B8%96%E7%95%8C", result.HtmlResult);
    }

    [Fact]
    public async Task EvaluatesLazyCoreParserFunctionsAndRestrictedArithmetic()
    {
        var repository = new FakeMediaWikiImportRepository();
        var renderer = CreateRenderer(repository);

        var result = await renderer.RenderAsync(
            "{{#if:yes|IF|{{Missing}}}} {{#ifeq: 2 | 2 | EQ | NO}} {{#switch: b | a=A | b=B | #default=D}} {{#expr: 2 + 3 * 4}} {{#ifexpr: 2 > 1 | TRUE | FALSE}}",
            ImportedSource(repository));

        Assert.Contains("IF", result.HtmlResult);
        Assert.Contains("EQ", result.HtmlResult);
        Assert.Contains("B", result.HtmlResult);
        Assert.Contains("14", result.HtmlResult);
        Assert.Contains("TRUE", result.HtmlResult);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "MissingTemplate");
    }

    [Fact]
    public async Task StopsCyclesAndRejectsUnsafeTemplateOutputWithinBudgets()
    {
        var repository = new FakeMediaWikiImportRepository();
        repository.Templates["A"] = Template("A", "A {{B}} <script>alert(1)</script>");
        repository.Templates["B"] = Template("B", "B {{A}}");
        var renderer = new MediaWikiWikitextRenderer(repository, new TwConfiguration(), new MediaWikiWikitextRenderOptions { MaxTemplateExpansionDepth = 8 });

        var result = await renderer.RenderAsync("{{A|x=<img src=x onerror=alert(2)>}}", ImportedSource(repository));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "RecursiveTemplate");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "UnsupportedExtensionTag");
        Assert.DoesNotContain("<script", result.HtmlResult, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", result.HtmlResult, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert(1)", result.HtmlResult, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert(2)", result.HtmlResult, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownAndInvalidParserFunctionsRemainDiagnosed()
    {
        var renderer = CreateRenderer(new FakeMediaWikiImportRepository());

        var result = await renderer.RenderAsync("{{#invoke:Unsafe|run}} {{#expr: 1 / 0}} {{#unknown:value}}", ImportedSource());

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "UnsupportedParserFunction");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "InvalidArithmeticExpression");
        Assert.Contains("Unsupported MediaWiki construct", result.HtmlResult);
    }

    [Fact]
    public async Task DiagnosesRestrictedTemplatesAndSanitizesTemplateProducedUrls()
    {
        var repository = new FakeMediaWikiImportRepository();
        repository.Templates["Restricted"] = new(TwMediaWikiTemplateResolutionStatus.Restricted, "Restricted");
        repository.Templates["Unsafe"] = Template("Unsafe", "[javascript:alert(1) click]");
        var renderer = CreateRenderer(repository);

        var result = await renderer.RenderAsync("{{Restricted}} {{Unsafe}}", ImportedSource(repository));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "RestrictedTemplate");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "UnsafeExternalUrl");
        Assert.DoesNotContain("javascript:", result.HtmlResult, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert(1)", result.HtmlResult, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnforcesTemplateDepthAndOutputBudgets()
    {
        var repository = new FakeMediaWikiImportRepository();
        repository.Templates["One"] = Template("One", "{{Two}}");
        repository.Templates["Two"] = Template("Two", "{{Three}}");
        repository.Templates["Three"] = Template("Three", new string('x', 500));
        var renderer = new MediaWikiWikitextRenderer(repository, new TwConfiguration(), new MediaWikiWikitextRenderOptions
        {
            MaxTemplateExpansionDepth = 1,
            MaxExpansionOutputCharacters = 32
        });

        var result = await renderer.RenderAsync("{{One}}", ImportedSource(repository));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code is "ExpansionDepthExceeded" or "ExpansionOutputLimitExceeded");
        Assert.True(result.HtmlResult.Length <= 1_500_000);
    }

    [Fact]
    public async Task MalformedAndOversizedInputFailsSafelyWithLocations()
    {
        var options = new MediaWikiWikitextRenderOptions { MaxInputCharacters = 32, MaxOutputCharacters = 48, MaxParseDepth = 2 };
        var renderer = new MediaWikiWikitextRenderer(new FakeMediaWikiImportRepository(), new TwConfiguration(), options);

        var result = await renderer.RenderAsync("{{{{{{{{nested}}}}}}}} [[unclosed <ref>body", ImportedSource());

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "InputLimitExceeded");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code is "ParseDepthExceeded" or "UnbalancedDelimiter" or "UnbalancedLink" or "UnbalancedReferenceTag");
        Assert.All(result.Diagnostics, diagnostic => Assert.True(diagnostic.Line == 0 || diagnostic.Line >= 1));
        Assert.True(result.HtmlResult.Length <= options.MaxOutputCharacters);
    }

    [Fact]
    public async Task RendersTypedTablesCaptionsSpansLinksAndNestedTables()
    {
        var repository = new FakeMediaWikiImportRepository();
        repository.Pages["Lore:世界"] = new("Lore:世界", "Lore::世界", 2);
        var renderer = CreateRenderer(repository);

        var result = await renderer.RenderAsync("""
            {| class="wikitable" id="facts"
            |+ Example caption
            ! Name !! Value
            |-
            ! rowspan="2" | [[Lore:世界|世界]] || Outer
            {| class="wikitable"
            ! Nested !! Cell
            |-
            | One || Two
            |}
            |-
            | colspan="2" | Last
            |}
            """, ImportedSource(repository));

        Assert.Contains("cw-mediawiki-table-wrap", result.HtmlResult);
        Assert.Contains("<caption", result.HtmlResult);
        Assert.Contains("<th", result.HtmlResult);
        Assert.Contains("rowspan=\"2\"", result.HtmlResult);
        Assert.Contains("colspan=\"2\"", result.HtmlResult);
        Assert.Contains("Lore%3A%3A%E4%B8%96%E7%95%8C", result.HtmlResult);
        Assert.Contains("cw-mediawiki-table", result.HtmlResult);
        Assert.Equal(2, result.Tables.Count);
        Assert.Equal("wikitable", result.Tables[0].CssClass);
        Assert.Equal(2, result.Tables[0].Rows[0].Cells.Count);
    }

    [Fact]
    public async Task RendersInfoboxTemplateAdvancedMediaAndGalleryWithBounds()
    {
        var repository = new FakeMediaWikiImportRepository();
        repository.Templates["SpeciesInfobox"] = Template("SpeciesInfobox", "{| class=\"infobox\"\n! {{{name|Unknown}}}\n|-\n! Homeworld\n| {{{homeworld|Unknown}}}\n|}");
        repository.Media["Map.png"] = new("Map.png", "File::Map_png", "Map_png", "image/png", 2, true);
        repository.Pages["Lore:Map"] = new("Lore:Map", "Lore::Map", 2);
        var renderer = CreateRenderer(repository);

        var result = await renderer.RenderAsync("""
            {{SpeciesInfobox|name=Koozarii|homeworld=[[Lore:Map|Aerenda]]}}
            [[File:Map.png|thumb|right|250x150px|alt=Map|link=Lore:Map|Map caption]]
            <gallery mode="packed" widths="120" heights="100">
            File:Map.png|Gallery caption
            File:Missing.png|Missing image
            </gallery>
            """, ImportedSource(repository));

        Assert.Contains("cw-mediawiki-infobox", result.HtmlResult);
        Assert.Contains("<figure class=\"cw-mediawiki-media-figure\">", result.HtmlResult);
        Assert.Contains("width=\"250\"", result.HtmlResult);
        Assert.Contains("height=\"150\"", result.HtmlResult);
        Assert.Contains("cw-mediawiki-gallery", result.HtmlResult);
        Assert.Contains("Gallery caption", result.HtmlResult);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "UnresolvedMedia");
        Assert.DoesNotContain("javascript:", result.HtmlResult, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsUnsafeTableAttributesMediaLinksAndOversizedDimensions()
    {
        var repository = new FakeMediaWikiImportRepository();
        repository.Media["Map.png"] = new("Map.png", "File::Map_png", "Map_png", "image/png", 2, true);
        var renderer = CreateRenderer(repository);

        var result = await renderer.RenderAsync("""
            {| class="wikitable" style="background:url(javascript:alert(1))" onclick="alert(2)"
            | data
            |}
            [[File:Map.png|99999px|link=https://evil.example|alt=ok]]
            """, ImportedSource(repository));

        Assert.DoesNotContain("onclick", result.HtmlResult, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", result.HtmlResult, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://evil.example", result.HtmlResult, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "UnsafeTableStyle");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "UnsafeMediaLink");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "InvalidMediaDimension");
    }

    [Fact]
    public async Task BoundsMalformedNestedTablesCellsAndGalleryMarkup()
    {
        var renderer = new MediaWikiWikitextRenderer(new FakeMediaWikiImportRepository(), new TwConfiguration(), new MediaWikiWikitextRenderOptions
        {
            MaxTableNestingDepth = 1,
            MaxTableCells = 1,
            MaxGalleryItems = 1
        });

        var result = await renderer.RenderAsync("""
            {| class="wikitable"
            | one || two
            {| class="wikitable"
            | nested
            |}
            |}
            <gallery>
            File:Missing.png|<script>alert(1)</script>
            File:Missing2.png|second
            """, ImportedSource());

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TableCellLimitExceeded");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code is "TableNestingLimitExceeded" or "UnclosedGallery");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GalleryItemLimitExceeded");
        Assert.DoesNotContain("<script", result.HtmlResult, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert(1)", result.HtmlResult, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreservesRestrictedMediaAsAPlaceholderAndFiltersTemplateAttributes()
    {
        var repository = new FakeMediaWikiImportRepository();
        repository.Media["Restricted.png"] = new("Restricted.png", "File::Restricted_png", "Restricted_png", "image/png", 1, false);
        repository.Templates["UnsafeTable"] = Template("UnsafeTable", "{| style=\"background:url(javascript:alert(1))\" onclick=\"alert(2)\"\n| safe\n|}\n");
        var renderer = CreateRenderer(repository);

        var result = await renderer.RenderAsync("{{UnsafeTable}} [[File:Restricted.png|thumb]]", ImportedSource(repository));

        Assert.Contains("cw-mediawiki-unresolved-media", result.HtmlResult);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "UnresolvedMedia");
        Assert.DoesNotContain("onclick", result.HtmlResult, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", result.HtmlResult, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert(1)", result.HtmlResult, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NativeDescriptorRemainsTheDefaultAndHasSeparateCacheIdentity()
    {
        Assert.Equal(TwPageSourceFormat.NativeTightWiki, TwMediaWikiRenderSource.Native.Format);
        Assert.Equal("native", TwMediaWikiRenderSource.Native.CacheKey);
        Assert.NotEqual(TwMediaWikiRenderSource.Native.CacheKey, ImportedSource().CacheKey);
    }

    [Fact]
    public async Task ExpandsPageNamespaceRevisionSiteAndClockMagicWordsFromTypedContext()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2024, 2, 3, 4, 5, 6, TimeSpan.Zero));
        var repository = new FakeMediaWikiImportRepository();
        var source = ImportedSource(repository, "42") with
        {
            SourceTitle = "Lore/World",
            SourceNamespaceId = "0",
            SourceNamespaceName = string.Empty,
            SourceRevisionId = "42",
            SourceTimestampUtc = new DateTime(2020, 12, 31, 23, 59, 58, DateTimeKind.Utc),
            SourceWikiIdentity = "fixture-wiki",
            ContentModel = "wikitext"
        };
        var renderer = new MediaWikiWikitextRenderer(repository,
            new TwConfiguration { Name = "Fixture Wiki", ContentLanguage = "fr" },
            new MediaWikiWikitextRenderOptions { TimeProvider = clock });

        var result = await renderer.RenderAsync("{{PAGENAME}}|{{FULLPAGENAME}}|{{PAGENAMEE}}|{{BASEPAGENAME}}|{{SUBPAGENAME}}|{{ROOTPAGENAME}}|{{NAMESPACE}}|{{NAMESPACENUMBER}}|{{TALKPAGENAME}}|{{SUBJECTPAGENAME}}|{{SITENAME}}|{{CONTENTLANGUAGE}}|{{REVISIONID}}|{{REVISIONTIMESTAMP}}|{{CURRENTTIMESTAMP}}", source);

        Assert.Contains("Lore/World|Lore/World|Lore%2FWorld|Lore|World|Lore||0|Talk:Lore/World|Lore/World|Fixture Wiki|fr|42|20201231235958|20240203040506", result.HtmlResult);
        Assert.True(result.DependsOnCurrentTime);
    }

    [Fact]
    public async Task KeepsViewedPageContextWhenTemplateBodyExpandsAndSupportsControlledFunctions()
    {
        var repository = new FakeMediaWikiImportRepository();
        repository.Templates["Context"] = Template("Context", "{{PAGENAME}}/{{FULLPAGENAME}}/{{NAMESPACE}}/{{REVISIONID}}/{{#if:{{PAGENAME}}|yes|no}}");
        var renderer = new MediaWikiWikitextRenderer(repository, new TwConfiguration(), new MediaWikiWikitextRenderOptions
        {
            TimeProvider = new FixedTimeProvider(DateTimeOffset.UnixEpoch)
        });

        var result = await renderer.RenderAsync("{{Context}} {{lc:HeLLo}} {{ucfirst:world}} {{padleft:7|3|0}} {{padright:x|3|.}} {{formatnum:12345.5}} {{#titleparts:One/Two/Three|2|1}} {{#rel2abs:../Sibling|Parent/Child/Leaf}} {{#time:%Y-%m-%d|@0}}", ImportedSource(repository));

        Assert.Contains("Article/Article//1000/yes", result.HtmlResult);
        Assert.Contains("hello World 007 x.. 12,345.5 One/Two Parent/Sibling 1970-01-01", result.HtmlResult);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "MissingTemplate");
    }

    [Fact]
    public async Task DiagnosesUnsupportedExtensionsAndRejectsUnsafeTimeAndPadding()
    {
        var renderer = new MediaWikiWikitextRenderer(new FakeMediaWikiImportRepository(), new TwConfiguration(), new MediaWikiWikitextRenderOptions
        {
            MaxFunctionOutputCharacters = 8
        });

        var result = await renderer.RenderAsync("{{#invoke:Unsafe|run}} {{#time:%Q|@0}} {{padleft:x|999|0}}", ImportedSource());

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "UnsupportedExtensionFunction");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "UnsupportedTimeFormat");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "InvalidPaddingWidth");
        Assert.Contains("Unsupported MediaWiki construct", result.HtmlResult);
    }

    private static MediaWikiWikitextRenderer CreateRenderer(FakeMediaWikiImportRepository repository)
        => new(repository, new TwConfiguration { BasePath = "/wiki" });

    private static TwMediaWikiRenderSource ImportedSource(FakeMediaWikiImportRepository? repository = null, string revision = "1000")
        => new(TwPageSourceFormat.ImportedMediaWiki, 7, "100", revision, "Article");

    private static TwMediaWikiTemplateTarget Template(string title, string body, string revision = "t1")
        => new(TwMediaWikiTemplateResolutionStatus.Resolved, title, title, revision, 1, body);

    private sealed class FakeMediaWikiImportRepository : ITwMediaWikiImportRepository
    {
        public Dictionary<string, TwMediaWikiPageTarget> Pages { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, TwMediaWikiMediaTarget> Media { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, TwMediaWikiTemplateTarget> Templates { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<TwMediaWikiRenderSource> GetRenderSourceAsync(int targetPageId, int targetRevision, CancellationToken cancellationToken = default)
            => Task.FromResult(TwMediaWikiRenderSource.Native);

        public Task<TwMediaWikiPageTarget?> ResolvePageAsync(long importBatchId, string sourceTitle, CancellationToken cancellationToken = default)
            => Task.FromResult(Pages.TryGetValue(sourceTitle.Replace('_', ' '), out var target) ? target : null);

        public Task<TwMediaWikiMediaTarget?> ResolveMediaAsync(long importBatchId, string sourcePageId, string sourceRevisionId, string sourceFileName, CancellationToken cancellationToken = default)
            => Task.FromResult(Media.TryGetValue(sourceFileName.Replace('_', ' '), out var target) ? target : null);

        public Task<TwMediaWikiTemplateTarget> ResolveTemplateAsync(long importBatchId, string sourceTitle, DateTime? asOfUtc = null, CancellationToken cancellationToken = default)
            => Task.FromResult(Templates.TryGetValue(sourceTitle.Replace('_', ' '), out var target)
                ? target
                : new TwMediaWikiTemplateTarget(TwMediaWikiTemplateResolutionStatus.Missing, sourceTitle));

        public Task<string> GetTemplateCacheVersionAsync(long importBatchId, DateTime? asOfUtc = null, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Join('|', Templates.OrderBy(pair => pair.Key).Select(pair => pair.Value.CacheKey)));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
