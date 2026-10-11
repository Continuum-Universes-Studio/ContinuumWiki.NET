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
    public async Task ReexpandsLuaReturnedWikitextThroughTheBoundedRenderer()
    {
        var repository = new FakeMediaWikiImportRepository();
        repository.Templates["Inner"] = Template("Inner", "Expanded {{{value}}}");
        var renderer = new MediaWikiWikitextRenderer(repository, new TwConfiguration(), new MediaWikiWikitextRenderOptions
        {
            ScribuntoExecutor = new ReturnedWikitextExecutor()
        });

        var result = await renderer.RenderAsync("{{#invoke:Returned|render|value=Continuum}}", ImportedSource(repository));

        Assert.Contains("Expanded Continuum", result.HtmlResult);
        Assert.DoesNotContain("{{Inner", result.HtmlResult);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "UnsupportedParserFunction");
    }

    [Fact]
    public async Task RendersNativeContinuumInfoboxThroughTheSafeInlinePipeline()
    {
        var renderer = CreateRenderer(new FakeMediaWikiImportRepository());

        var result = await renderer.RenderAsync(
            "{{#cuinfobox:species|name=Koozarii|status=Active|details=[[Lore:Koozarii|Lore]]}}",
            ImportedSource());

        Assert.Contains("cu-infobox cu-infobox--species", result.HtmlResult);
        Assert.Contains("Koozarii", result.HtmlResult);
        Assert.Contains("Active", result.HtmlResult);
        Assert.DoesNotContain("<p><aside", result.HtmlResult, StringComparison.Ordinal);
        Assert.DoesNotContain("Unsupported MediaWiki construct", result.HtmlResult, StringComparison.Ordinal);
        Assert.DoesNotContain("{{#cuinfobox", result.HtmlResult, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", result.HtmlResult, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdaptsBoundedLegacyPortableInfoboxWithoutTrustingImportedMarkup()
    {
        var renderer = CreateRenderer(new FakeMediaWikiImportRepository());

        var result = await renderer.RenderAsync("""
            Before
            <infobox type="species">
              <title source="name">Koozarii</title>
              <group><header>Overview</header><data source="status"><label>Status</label>Active</data></group>
            </infobox>
            After
            """, ImportedSource());

        Assert.Contains("cu-infobox cu-infobox--species", result.HtmlResult);
        Assert.Contains("Before", result.HtmlResult);
        Assert.Contains("After", result.HtmlResult);
        Assert.Contains("Koozarii", result.HtmlResult);
        Assert.Contains("Active", result.HtmlResult);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "UnsupportedExtensionTag");
        Assert.DoesNotContain("<script", result.HtmlResult, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RendersObservedMermaidAsAnAccessibleStaticSourceFallback()
    {
        var renderer = CreateRenderer(new FakeMediaWikiImportRepository());

        var result = await renderer.RenderAsync("""
            <mermaid>
            flowchart TB
            n_Jovan["Jovan <Creator>"]
            click n_Jovan "https://example.invalid/Jovan"
            </mermaid>
            """, ImportedSource(revision: "9482"));

        Assert.Contains("<figure class=\"cw-mermaid\"", result.HtmlResult);
        Assert.Contains("Mermaid diagram source (static; diagram rendering unavailable)", result.HtmlResult);
        Assert.Contains("Jovan &lt;Creator&gt;", result.HtmlResult);
        Assert.Contains("https://example.invalid/Jovan", result.HtmlResult);
        Assert.DoesNotContain("<svg", result.HtmlResult, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", result.HtmlResult, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "ExternalRendererUnavailable");
        Assert.Contains(result.UnsupportedConstructs, value => value == "mermaid-diagram-rendering");
    }

    [Fact]
    public async Task RejectsUnsafeMalformedAndUnboundedMermaidInput()
    {
        var repository = new FakeMediaWikiImportRepository();
        var renderer = new MediaWikiWikitextRenderer(repository, new TwConfiguration(), new MediaWikiWikitextRenderOptions
        {
            MaxMermaidInputCharacters = 8
        });

        var unsafeResult = await CreateRenderer(repository).RenderAsync("""
            <mermaid onclick="alert(1)">flowchart TB
            A-->B</mermaid>
            """, ImportedSource());
        var unknownAttributeResult = await CreateRenderer(repository).RenderAsync("<mermaid class=diagram>flowchart TB\nA-->B</mermaid>", ImportedSource());
        var limitedResult = await renderer.RenderAsync("<mermaid>flowchart TB\nA-->B</mermaid>", ImportedSource());
        var malformedResult = await CreateRenderer(repository).RenderAsync("<mermaid>flowchart TB\nA-->B", ImportedSource());
        var nestedResult = await CreateRenderer(repository).RenderAsync("<mermaid>flowchart TB\n<mermaid>graph TB\nA-->B</mermaid></mermaid>", ImportedSource());
        var emptyResult = await CreateRenderer(repository).RenderAsync("<mermaid></mermaid>", ImportedSource());
        var invalidTypeResult = await CreateRenderer(repository).RenderAsync("<mermaid>not-a-diagram\nA-->B</mermaid>", ImportedSource());

        Assert.Contains(unsafeResult.Diagnostics, diagnostic => diagnostic.Code == "ExtensionSecurityRejected");
        Assert.Contains(unknownAttributeResult.Diagnostics, diagnostic => diagnostic.Code == "InvalidExtensionInput");
        Assert.Contains(limitedResult.Diagnostics, diagnostic => diagnostic.Code == "ExtensionInputLimitExceeded");
        Assert.Contains(malformedResult.Diagnostics, diagnostic => diagnostic.Code == "InvalidExtensionInput");
        Assert.Contains(nestedResult.Diagnostics, diagnostic => diagnostic.Code == "InvalidExtensionInput");
        Assert.Contains(emptyResult.Diagnostics, diagnostic => diagnostic.Code == "InvalidExtensionInput");
        Assert.Contains(invalidTypeResult.Diagnostics, diagnostic => diagnostic.Code == "InvalidExtensionInput");
        Assert.DoesNotContain("onclick", unsafeResult.HtmlResult, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert(1)", unsafeResult.HtmlResult, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<figure class=\"cw-mermaid\"", unsafeResult.HtmlResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppliesSharedOutputBudgetToMermaidStaticFallback()
    {
        var renderer = new MediaWikiWikitextRenderer(new FakeMediaWikiImportRepository(), new TwConfiguration(), new MediaWikiWikitextRenderOptions
        {
            MaxOutputCharacters = 32
        });

        var result = await renderer.RenderAsync("<mermaid>flowchart TB\nA-->B</mermaid>", ImportedSource());

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "ExternalRendererUnavailable");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "ExtensionOutputLimitExceeded");
        Assert.DoesNotContain("<figure", result.HtmlResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeavesMalformedLegacyInfoboxOnTheRejectingTagPath()
    {
        var renderer = CreateRenderer(new FakeMediaWikiImportRepository());

        var result = await renderer.RenderAsync("<infobox><data source=broken>value", ImportedSource());

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "UnsupportedExtensionTag");
        Assert.Contains(result.UnsupportedConstructs, value => value.Equals("infobox", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AppliesSchemaAliasesNestedPathsGroupOrderAndHorizontalLayout()
    {
        var registry = new MediaWikiInfoboxRegistry([
            new MediaWikiInfoboxSchema("generic", "Infobox"),
            new MediaWikiInfoboxSchema("fixture", "Fixture", TitleField: "profile.name", Fields: [
                new("profile.name", "Name", "overview"),
                new("state", "State", "overview"),
                new("left", "Left", "details"),
                new("right", "Right", "details")
            ], Groups: [new("overview", "Overview"), new("details", "Details", "horizontal")], Aliases: new Dictionary<string, string> { ["display"] = "profile[name]" }, RenderUnknownFields: false)
        ]);
        var renderer = new MediaWikiWikitextRenderer(new FakeMediaWikiImportRepository(), new TwConfiguration(), new MediaWikiWikitextRenderOptions { InfoboxRegistry = registry });

        var result = await renderer.RenderAsync("{{#cuinfobox:fixture|display=Valorîin|state=Active|left=L|right=R|unknown=hidden}}", ImportedSource());

        Assert.Contains("Valor&#238;in", result.HtmlResult);
        Assert.Contains("cu-infobox__section--horizontal", result.HtmlResult);
        Assert.Contains("Left", result.HtmlResult);
        Assert.DoesNotContain("hidden", result.HtmlResult);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "UnsupportedParserFunction");
    }

    [Fact]
    public async Task ReportsUnknownSchemaAndUsesBoundedTitleFallback()
    {
        var renderer = CreateRenderer(new FakeMediaWikiImportRepository());

        var result = await renderer.RenderAsync("{{#cuinfobox:unknown-schema|status=Active}}", ImportedSource());

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "InfoboxSchemaFallback");
        Assert.Contains("Article", result.HtmlResult);
        Assert.Contains("Active", result.HtmlResult);
    }

    [Fact]
    public void SchemaInterchangeIsVersionedAndInheritanceCyclesAreRejected()
    {
        var registry = new MediaWikiInfoboxRegistry([
            new MediaWikiInfoboxSchema("generic", "Infobox"),
            new MediaWikiInfoboxSchema("base", "Base", Fields: [new("name", "Name")]),
            new MediaWikiInfoboxSchema("child", "Child", Extends: ["base"])
        ]);

        var json = registry.ToJson();
        Assert.Contains("schemaVersion", json);
        Assert.Contains("\"name\"", MediaWikiInfoboxRegistry.FromJson(json).ToJson());
        Assert.Throws<ArgumentException>(() => new MediaWikiInfoboxRegistry([
            new MediaWikiInfoboxSchema("generic", "Infobox"),
            new MediaWikiInfoboxSchema("a", "A", Extends: ["b"]),
            new MediaWikiInfoboxSchema("b", "B", Extends: ["a"])
        ]));
        Assert.Throws<InvalidDataException>(() => MediaWikiInfoboxRegistry.FromJson("{\"schemas\":[]}"));
    }

    [Fact]
    public async Task RejectsUnsupportedPortableInfoboxElementsAndXmlLimits()
    {
        var renderer = CreateRenderer(new FakeMediaWikiImportRepository());
        var panel = await renderer.RenderAsync("<infobox><panel><data source=\"x\">x</data></panel></infobox>", ImportedSource());
        var dtd = await renderer.RenderAsync("<!DOCTYPE infobox [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]><infobox>&xxe;</infobox>", ImportedSource());
        var deep = "<infobox>" + string.Concat(Enumerable.Repeat("<group>", 20)) + "x" + string.Concat(Enumerable.Repeat("</group>", 20)) + "</infobox>";
        var bounded = await renderer.RenderAsync(deep, ImportedSource());

        Assert.Contains(panel.Diagnostics, diagnostic => diagnostic.Code == "UnsupportedPortableInfoboxConstruct");
        Assert.Contains(dtd.Diagnostics, diagnostic => diagnostic.Code is "PortableInfoboxLimitExceeded" or "MalformedPortableInfobox");
        Assert.Contains(bounded.Diagnostics, diagnostic => diagnostic.Code == "PortableInfoboxLimitExceeded");
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
    public async Task SwitchSupportsFallThroughLabelsAndKeepsUnselectedResultsLazy()
    {
        var repository = new FakeMediaWikiImportRepository();
        var renderer = CreateRenderer(repository);

        var result = await renderer.RenderAsync(
            "{{#switch:alpha|alpha|beta=Alpha or beta|gamma={{Missing}}|#default=Fallback}}",
            ImportedSource(repository));

        Assert.Contains("Alpha or beta", result.HtmlResult, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "MissingTemplate");
    }

    [Fact]
    public async Task EvaluatesIfAndIfEqWithWhitespaceNestedConditionsAndLazyBranches()
    {
        var repository = new FakeMediaWikiImportRepository();
        repository.Templates["Value"] = Template("Value", " two ");
        var renderer = CreateRenderer(repository);

        var result = await renderer.RenderAsync(
            "{{#if:   |yes|empty}} {{#if:{{Value}}|nested|wrong}} {{#ifeq: left |  left  |equal|wrong}} {{#ifeq:{{Value}}|two|template-equal|wrong}} {{#if:|{{Missing}}|selected}} {{#if:yes|present}} {{#ifeq:a|a|equal-no-fallback}}",
            ImportedSource(repository));

        Assert.Contains("empty", result.HtmlResult, StringComparison.Ordinal);
        Assert.Contains("nested", result.HtmlResult, StringComparison.Ordinal);
        Assert.Contains("equal", result.HtmlResult, StringComparison.Ordinal);
        Assert.Contains("template-equal", result.HtmlResult, StringComparison.Ordinal);
        Assert.Contains("selected", result.HtmlResult, StringComparison.Ordinal);
        Assert.Contains("present", result.HtmlResult, StringComparison.Ordinal);
        Assert.Contains("equal-no-fallback", result.HtmlResult, StringComparison.Ordinal);
        Assert.DoesNotContain("yes", result.HtmlResult, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "MissingTemplate");
    }

    [Fact]
    public async Task SwitchUsesFirstMatchingCaseEmptyResultAndDefaultWithoutExpandingOtherCases()
    {
        var repository = new FakeMediaWikiImportRepository();
        var renderer = CreateRenderer(repository);

        var result = await renderer.RenderAsync(
            "{{#switch:a|a=first|a=duplicate|#default=unused}} / {{#switch:missing|a=first|bad={{Missing}}|#default=default}} / {{#switch:empty|empty=|#default=wrong}} / {{#switch:absent|a=first}}",
            ImportedSource(repository));

        Assert.Contains("first", result.HtmlResult, StringComparison.Ordinal);
        Assert.Contains("default", result.HtmlResult, StringComparison.Ordinal);
        Assert.DoesNotContain("duplicate", result.HtmlResult, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong", result.HtmlResult, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "MissingTemplate");
        Assert.DoesNotContain("{{#switch:absent", result.HtmlResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluatesRestrictedExpressionPrecedenceDecimalsUnaryOperatorsAndRejectsInvalidInput()
    {
        var renderer = CreateRenderer(new FakeMediaWikiImportRepository());

        var result = await renderer.RenderAsync(
            "{{#expr:(2 + 3) * -2}} {{#expr:7 % 4}} {{#ifexpr:1.5 > 1|decimal|wrong}} {{#expr:1 + }}",
            ImportedSource());

        Assert.Contains("-10", result.HtmlResult, StringComparison.Ordinal);
        Assert.Contains("3", result.HtmlResult, StringComparison.Ordinal);
        Assert.Contains("decimal", result.HtmlResult, StringComparison.Ordinal);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "InvalidArithmeticExpression");

        var overflow = await renderer.RenderAsync("{{#expr:79228162514264337593543950335 + 1}}", ImportedSource());
        var tooLong = await renderer.RenderAsync("{{#expr:" + new string('1', 513) + "}}", ImportedSource());

        Assert.Contains(overflow.Diagnostics, diagnostic => diagnostic.Code == "InvalidArithmeticExpression");
        Assert.Contains(tooLong.Diagnostics, diagnostic => diagnostic.Code == "InvalidArithmeticExpression");

        var tokenLimited = await new MediaWikiWikitextRenderer(new FakeMediaWikiImportRepository(), new TwConfiguration(), new MediaWikiWikitextRenderOptions
        {
            MaxExpressionTokens = 2
        }).RenderAsync("{{#expr:1 + 2}}", ImportedSource());

        Assert.Contains(tokenLimited.Diagnostics, diagnostic => diagnostic.Code == "InvalidArithmeticExpression");
        Assert.Contains(tokenLimited.Diagnostics, diagnostic => diagnostic.Message.Contains("token limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PreservesNestedParserArgumentsWhenSeparatorsAndEqualsAreNested()
    {
        var repository = new FakeMediaWikiImportRepository();
        repository.Templates["Outer"] = Template("Outer", "value={{{value}}}");
        var renderer = CreateRenderer(repository);

        var result = await renderer.RenderAsync(
            "{{Outer|value={{#switch:x|x=left=right|#default=wrong}}}}",
            ImportedSource(repository));

        Assert.Contains("value=left=right", result.HtmlResult, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "MissingTemplate");
    }

    [Fact]
    public async Task ParserFunctionOperandsUseTheEligibleHistoricalTemplateRevision()
    {
        var repository = new FakeMediaWikiImportRepository();
        repository.TemplateRevisions["Versioned"] =
        [
            Template("Template:Versioned", "old", "old-rev", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            Template("Template:Versioned", "new", "new-rev", new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc))
        ];
        var renderer = CreateRenderer(repository);

        var historical = await renderer.RenderAsync(
            "{{#ifeq:{{Versioned}}|old|historical-match|wrong}}",
            ImportedSource(repository) with { SourceTimestampUtc = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc) });
        var unavailable = await renderer.RenderAsync(
            "{{#ifeq:{{Versioned}}|old|historical-match|wrong}}",
            ImportedSource(repository) with { SourceTimestampUtc = new DateTime(2023, 12, 1, 0, 0, 0, DateTimeKind.Utc) });

        Assert.Contains("historical-match", historical.HtmlResult, StringComparison.Ordinal);
        Assert.DoesNotContain(historical.Diagnostics, diagnostic => diagnostic.Code == "RestrictedTemplate");
        Assert.Contains(unavailable.Diagnostics, diagnostic => diagnostic.Code == "RestrictedTemplate");
    }

    [Fact]
    public async Task HistoricalSelectionUsesTimestampBoundaryRatherThanRevisionIdOrder()
    {
        var repository = new FakeMediaWikiImportRepository();
        repository.TemplateRevisions["Era"] =
        [
            Template("Template:Era", "boundary", "revision-2", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), targetRevision: 2),
            Template("Template:Era", "future", "revision-1", new DateTime(2024, 1, 1, 0, 0, 1, DateTimeKind.Utc), targetRevision: 1)
        ];
        var renderer = CreateRenderer(repository);

        var result = await renderer.RenderAsync(
            "{{Era}}",
            ImportedSource(repository) with { SourceTimestampUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) });

        Assert.Contains("boundary", result.HtmlResult, StringComparison.Ordinal);
        Assert.DoesNotContain("future", result.HtmlResult, StringComparison.Ordinal);
        Assert.Contains(result.TemplateDependencies, dependency => dependency.SourceRevisionId == "revision-2");
    }

    [Fact]
    public async Task EnforcesDiagnosticCountAndCancellationBounds()
    {
        var options = new MediaWikiWikitextRenderOptions { MaxDiagnostics = 2 };
        var renderer = new MediaWikiWikitextRenderer(new FakeMediaWikiImportRepository(), new TwConfiguration(), options);
        var result = await renderer.RenderAsync(string.Concat(Enumerable.Repeat("{{#unknown:value}} ", 16)), ImportedSource());

        Assert.True(result.Diagnostics.Count <= options.MaxDiagnostics);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => renderer.RenderAsync("text", ImportedSource(), cancellation.Token));
    }

    [Fact]
    public async Task SanitizesTemplateResolverExceptionsAndDistinguishesUnavailableSourceBodies()
    {
        var repository = new FakeMediaWikiImportRepository
        {
            TemplateResolutionException = new InvalidOperationException("private connection details"),
            Templates =
            {
                ["BodyMissing"] = new(TwMediaWikiTemplateResolutionStatus.Resolved, "Template:BodyMissing", "page-1", "rev-1", 1, null, null, "wikitext")
            }
        };
        var renderer = CreateRenderer(repository);

        var resolverFailure = await renderer.RenderAsync("before {{Broken}} after", ImportedSource(repository));
        Assert.Contains(resolverFailure.Diagnostics, diagnostic => diagnostic.Code == "TemplateResolutionFailed");
        Assert.DoesNotContain("private connection", resolverFailure.HtmlResult, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(resolverFailure.TemplateDependencies, dependency => dependency.Status == "ResolutionFailed");

        repository.TemplateResolutionException = null;
        var unavailableSource = await renderer.RenderAsync("before {{BodyMissing}} after", ImportedSource(repository));
        Assert.Contains(unavailableSource.Diagnostics, diagnostic => diagnostic.Code == "TemplateSourceUnavailable");
        Assert.DoesNotContain(unavailableSource.Diagnostics, diagnostic => diagnostic.Code == "MissingTemplate");
    }

    [Fact]
    public async Task ResolvesMainNamespaceColonAliasesWhitespaceUnicodeAndDynamicTitles()
    {
        var repository = new FakeMediaWikiImportRepository();
        repository.Templates["Example"] = Template("Template:Example", "example");
        repository.Templates["Space Name"] = Template("Template:Space Name", "space");
        repository.Templates["世界"] = Template("Template:世界", "unicode");
        repository.Templates["Name"] = Template("Template:Name", "Example");
        repository.Transclusions["0:Example"] = Template("Example", "main-example");
        var renderer = CreateRenderer(repository);

        var supported = await renderer.RenderAsync("{{Example}} {{Template:Example}} {{Space_Name}} {{Space  Name}} {{世界}} {{:Example}} {{Main:Example}} {{ {{Name}} }}", ImportedSource(repository));
        Assert.Contains("example example space space unicode main-example main-example example", supported.HtmlResult, StringComparison.Ordinal);
        Assert.Equal(7, repository.TemplateResolutionRequests.Count);
        Assert.All(repository.TemplateResolutionRequests, value => Assert.DoesNotContain("Template:", value, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(repository.TransclusionRequests, value => value == "0:Example");

        var unsupported = await renderer.RenderAsync("{{Unknown:Example}} {{::Example}} {{}}", ImportedSource(repository));
        Assert.Contains(unsupported.Diagnostics, diagnostic => diagnostic.Code == "UnresolvedTemplateNamespace");
        Assert.Contains(unsupported.Diagnostics, diagnostic => diagnostic.Code == "InvalidTemplateName");
    }

    [Fact]
    public async Task DynamicMainTitleUsesEligibleHistoricalRevisionAfterNormalization()
    {
        var repository = new FakeMediaWikiImportRepository();
        repository.Templates["Name"] = Template("Template:Name", "Era");
        repository.TransclusionRevisions["0:Era"] =
        [
            Template("Era", "old-main", "old-main-rev", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            Template("Era", "new-main", "new-main-rev", new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc))
        ];

        var result = await CreateRenderer(repository).RenderAsync(
            "{{:{{Name}} }}",
            ImportedSource(repository) with { SourceTimestampUtc = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc) });

        Assert.Contains("old-main", result.HtmlResult, StringComparison.Ordinal);
        Assert.DoesNotContain("new-main", result.HtmlResult, StringComparison.Ordinal);
        Assert.Contains(result.TemplateDependencies, dependency => dependency.SourceRevisionId == "old-main-rev");
    }

    [Fact]
    public async Task ResolvesDiamondDependenciesRepeatedlyWithoutFalseCyclesAndKeepsUnselectedCyclesLazy()
    {
        var repository = new FakeMediaWikiImportRepository();
        repository.Templates["A"] = Template("Template:A", "A {{C}} {{C}}");
        repository.Templates["B"] = Template("Template:B", "B {{C}}");
        repository.Templates["C"] = Template("Template:C", "C");
        repository.Templates["Cycle"] = Template("Template:Cycle", "{{Cycle}}");
        var renderer = CreateRenderer(repository);

        var result = await renderer.RenderAsync("{{A}} {{B}} {{#if:yes|visible|{{Cycle}}}}", ImportedSource(repository));

        Assert.Contains("A C C B C visible", result.HtmlResult, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "RecursiveTemplate");
        Assert.DoesNotContain(result.TemplateDependencies, dependency => dependency.SourceTitle == "Cycle");
        Assert.Equal(3, repository.TemplateResolutionRequests.Count(value => value.Equals("C", StringComparison.OrdinalIgnoreCase)));
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
    public async Task StopsDirectLongAndParserFunctionTemplateCyclesWithoutTimeouts()
    {
        var repository = new FakeMediaWikiImportRepository();
        repository.Templates["Direct"] = Template("Direct", "before {{Direct}} after");
        repository.Templates["A"] = Template("A", "{{B}}");
        repository.Templates["B"] = Template("B", "{{C}}");
        repository.Templates["C"] = Template("C", "{{A}}");
        repository.Templates["Parser"] = Template("Parser", "{{#if:yes|{{Parser}}|done}}");
        var renderer = CreateRenderer(repository);

        var direct = await renderer.RenderAsync("{{Direct}}", ImportedSource(repository));
        var longCycle = await renderer.RenderAsync("{{A}}", ImportedSource(repository));
        var parserCycle = await renderer.RenderAsync("{{Parser}}", ImportedSource(repository));

        Assert.Contains(direct.Diagnostics, diagnostic => diagnostic.Code == "RecursiveTemplate");
        Assert.Contains(longCycle.Diagnostics, diagnostic => diagnostic.Code == "RecursiveTemplate");
        Assert.Contains(parserCycle.Diagnostics, diagnostic => diagnostic.Code == "RecursiveTemplate");
        Assert.DoesNotContain(direct.Diagnostics, diagnostic => diagnostic.Code == "ExpansionTimeLimitExceeded");
        Assert.DoesNotContain(longCycle.Diagnostics, diagnostic => diagnostic.Code == "ExpansionTimeLimitExceeded");
        Assert.DoesNotContain(parserCycle.Diagnostics, diagnostic => diagnostic.Code == "ExpansionTimeLimitExceeded");
    }

    [Fact]
    public async Task RepeatedNonrecursiveTemplatesAndOperationBudgetRemainDistinct()
    {
        var repository = new FakeMediaWikiImportRepository();
        repository.Templates["Word"] = Template("Word", "x");
        var renderer = CreateRenderer(repository);

        var repeated = await renderer.RenderAsync(string.Concat(Enumerable.Repeat("{{Word}}", 32)), ImportedSource(repository));

        Assert.Equal(32, repeated.HtmlResult.Count(value => value == 'x'));
        Assert.DoesNotContain(repeated.Diagnostics, diagnostic => diagnostic.Code == "RecursiveTemplate");

        var bounded = new MediaWikiWikitextRenderer(repository, new TwConfiguration(), new MediaWikiWikitextRenderOptions
        {
            MaxExpansionOperations = 8,
            MaxExpansionTime = TimeSpan.FromSeconds(30)
        });
        var budget = await bounded.RenderAsync(string.Concat(Enumerable.Repeat("{{Word}}", 32)), ImportedSource(repository));

        Assert.Contains(budget.Diagnostics, diagnostic => diagnostic.Code == "ExpansionBudgetExceeded");
        Assert.DoesNotContain(budget.Diagnostics, diagnostic => diagnostic.Code == "ExpansionTimeLimitExceeded");
    }

    [Fact]
    public async Task TerminalTypedScribuntoFailureStopsLaterExpansionAndKeepsPrimaryEvidence()
    {
        var repository = new FakeMediaWikiImportRepository();
        var renderer = new MediaWikiWikitextRenderer(repository, new TwConfiguration(), new MediaWikiWikitextRenderOptions
        {
            ScribuntoExecutor = new FixedScribuntoExecutor(null, false, "LuaRuntimeError", "CargoProviderUnavailable: authorized historical data is unavailable.")
        });

        var result = await renderer.RenderAsync("before {{#invoke:Cargo|run}} {{LaterMissing}}", ImportedSource(repository));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "LuaRuntimeError");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "MissingTemplate");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "ExpansionTimeLimitExceeded");
    }

    [Fact]
    public async Task TypedScribuntoFailuresRemainFailClosedAcrossRuntimeCategories()
    {
        var cases = new[]
        {
            ("ScribuntoModuleMissing", "The requested module was not available."),
            ("LuaRuntimeError", "CargoProviderUnavailable: no authorized historical Cargo dataset is configured."),
            ("UnsupportedApi", "The requested Cargo query shape is unsupported."),
            ("ScribuntoWorkerProtocolError", "The worker returned an invalid response.")
        };

        foreach (var (code, message) in cases)
        {
            var renderer = new MediaWikiWikitextRenderer(new FakeMediaWikiImportRepository(), new TwConfiguration(), new MediaWikiWikitextRenderOptions
            {
                ScribuntoExecutor = new FixedScribuntoExecutor(null, false, code, message)
            });

            var result = await renderer.RenderAsync("{{#invoke:Fixture|run}}", ImportedSource());

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code);
            Assert.Contains(result.ScribuntoDependencies!, dependency => dependency.Status == "ExecutionFailed");
        }
    }

    [Fact]
    public async Task MissingRequiredTemplateIsPreservedAsARecoverableDependencyFailure()
    {
        var renderer = CreateRenderer(new FakeMediaWikiImportRepository());

        var result = await renderer.RenderAsync("before {{RequiredMissing}} after", ImportedSource());

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "MissingTemplate");
        Assert.Contains(result.TemplateDependencies, dependency => dependency.SourceTitle == "RequiredMissing" && dependency.Status == "Missing");
        Assert.Contains("before", result.HtmlResult);
        Assert.Contains("after", result.HtmlResult);
    }

    [Fact]
    public async Task WallClockCancellationIsReportedSeparatelyFromOperationBudget()
    {
        var repository = new FakeMediaWikiImportRepository { TemplateResolutionDelay = TimeSpan.FromMilliseconds(50) };
        repository.Templates["Delayed"] = Template("Delayed", "done");
        var renderer = new MediaWikiWikitextRenderer(repository, new TwConfiguration(), new MediaWikiWikitextRenderOptions
        {
            MaxExpansionTime = TimeSpan.FromMilliseconds(1),
            MaxExpansionOperations = 10_000
        });

        var result = await renderer.RenderAsync("{{Delayed}}", ImportedSource(repository));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "ExpansionTimeLimitExceeded");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "ExpansionBudgetExceeded");
    }

    [Fact]
    public async Task EmptyLuaResultIsSuccessfulAndNestedLuaRecursionIsBounded()
    {
        var emptyRenderer = new MediaWikiWikitextRenderer(new FakeMediaWikiImportRepository(), new TwConfiguration(), new MediaWikiWikitextRenderOptions
        {
            ScribuntoExecutor = new FixedScribuntoExecutor(string.Empty, true)
        });
        var empty = await emptyRenderer.RenderAsync("before {{#invoke:Fixture|run}} after", ImportedSource());
        Assert.Contains("before", empty.HtmlResult);
        Assert.Contains("after", empty.HtmlResult);
        Assert.DoesNotContain(empty.Diagnostics, diagnostic => diagnostic.Code == "ScribuntoExecutionRejected");

        var recursiveRenderer = new MediaWikiWikitextRenderer(new FakeMediaWikiImportRepository(), new TwConfiguration(), new MediaWikiWikitextRenderOptions
        {
            ScribuntoExecutor = new FixedScribuntoExecutor("{{#invoke:Fixture|run}}", true)
        });
        var recursive = await recursiveRenderer.RenderAsync("{{#invoke:Fixture|run}}", ImportedSource());
        Assert.Contains(recursive.Diagnostics, diagnostic => diagnostic.Code == "RecursiveScribuntoInvocation");
        Assert.DoesNotContain(recursive.Diagnostics, diagnostic => diagnostic.Code == "ExpansionTimeLimitExceeded");
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

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "ScribuntoExecutionRejected");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "UnsupportedTimeFormat");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "InvalidPaddingWidth");
        Assert.Contains("Unsupported MediaWiki construct", result.HtmlResult);
    }

    [Fact]
    public async Task KeepsCargoStoreOutsideTheReadOnlyRendererBoundary()
    {
        var result = await CreateRenderer(new FakeMediaWikiImportRepository()).RenderAsync(
            "{{#cargo_store:_table=Divines|Name=Jovan}}", ImportedSource());

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "UnsupportedParserFunction");
        Assert.Contains(result.UnsupportedConstructs, value => value == "parser-function");
        Assert.DoesNotContain("cargo_store", result.HtmlResult, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT", result.HtmlResult, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE", result.HtmlResult, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolvesTemplateStylesWithHistoricalProvenanceAndDeduplicatesPerRender()
    {
        var repository = new FakeMediaWikiImportRepository();
        repository.Templates["Example/styles.css"] = Template(
            "Template:Example/styles.css",
            ".example, #fixture { color: #fff; padding: 0.5rem; }",
            "css-rev-1",
            contentModel: "css");
        var renderer = CreateRenderer(repository);

        var result = await renderer.RenderAsync("""
            <templatestyles src="Template:Example/styles.css" />
            <templatestyles
             src='Template:Example/styles.css' />
            Example
            """, ImportedSource(repository));

        var stylesheet = Assert.Single(result.Stylesheets!);
        Assert.Equal("Template:Example/styles.css", stylesheet.SourceTitle);
        Assert.Equal(7, stylesheet.ImportBatchId);
        Assert.Equal(0, stylesheet.ReferenceOrder);
        Assert.Equal("css-rev-1", stylesheet.SourceRevisionId);
        Assert.Equal("css", stylesheet.ContentModel);
        Assert.Equal(MediaWikiTemplateStylesCompatibility.SanitizerPolicyVersion, stylesheet.SanitizerPolicyVersion);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stylesheet.SanitizedCss))),
            stylesheet.SanitizedCssSha256);
        Assert.Contains(MediaWikiTemplateStylesCompatibility.SanitizerPolicyVersion, stylesheet.CacheKey, StringComparison.Ordinal);
        Assert.Contains(".wiki-page-body .example", stylesheet.SanitizedCss, StringComparison.Ordinal);
        Assert.Contains(".wiki-page-body #fixture", stylesheet.SanitizedCss, StringComparison.Ordinal);
        Assert.DoesNotContain("<templatestyles", result.HtmlResult, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code.StartsWith("TemplateStyles", StringComparison.Ordinal));
    }

    [Fact]
    public async Task KeepsEligibleTemplateStylesRevisionsDistinctAndRejectsFutureRevision()
    {
        var repository = new FakeMediaWikiImportRepository();
        repository.TemplateRevisions["Example/styles.css"] =
        [
            Template("Template:Example/styles.css", ".old { color: blue; }", "css-old", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), "css"),
            Template("Template:Example/styles.css", ".new { color: red; }", "css-new", new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc), "css")
        ];
        var renderer = CreateRenderer(repository);

        var oldResult = await renderer.RenderAsync(
            """<templatestyles src="Template:Example/styles.css" />""",
            ImportedSource(repository) with { SourceTimestampUtc = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc) });
        var futureOnlyResult = await renderer.RenderAsync(
            """<templatestyles src="Template:Example/styles.css" />""",
            ImportedSource(repository) with { SourceTimestampUtc = new DateTime(2023, 12, 1, 0, 0, 0, DateTimeKind.Utc) });

        Assert.Equal("css-old", Assert.Single(oldResult.Stylesheets!).SourceRevisionId);
        Assert.Contains(".old", oldResult.Stylesheets![0].SanitizedCss, StringComparison.Ordinal);
        Assert.Contains(futureOnlyResult.Diagnostics, diagnostic => diagnostic.Code == "TemplateStylesSourceRevisionUnavailable");
        Assert.Empty(futureOnlyResult.Stylesheets!);
    }

    [Fact]
    public async Task ClassifiesTemplateStylesSourceAndContentModelFailures()
    {
        var repository = new FakeMediaWikiImportRepository();
        repository.Templates["Example/not-css"] = Template("Template:Example/not-css", ".x { color: red; }", "text-rev", contentModel: "wikitext");
        repository.Templates["Example/styles.css"] = Template("Template:Example/styles.css", ".x { color: red; }", "css-rev", contentModel: "css");
        var renderer = CreateRenderer(repository);

        var result = await renderer.RenderAsync("""
            <templatestyles />
            <templatestyles src="https://example.invalid/style.css" />
            <templatestyles src="Template:Missing/styles.css" />
            <templatestyles src="Template:Example/not-css" />
            """, ImportedSource(repository));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TemplateStylesMissingSource");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TemplateStylesInvalidSource");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TemplateStylesSourceUnavailable");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TemplateStylesUnsupportedContentModel");
        Assert.Empty(result.Stylesheets!);
    }

    [Fact]
    public async Task RejectsUnsafeMalformedAndUnboundedTemplateStylesCss()
    {
        var repository = new FakeMediaWikiImportRepository();
        var renderer = CreateRenderer(repository);

        repository.Templates["Unsafe/styles.css"] = Template("Template:Unsafe/styles.css", """@import url("https://evil.invalid/a.css"); .x { color: red; }""", "unsafe", contentModel: "css");
        var unsafeResult = await renderer.RenderAsync("""<templatestyles src="Template:Unsafe/styles.css" />""", ImportedSource(repository));
        Assert.Contains(unsafeResult.Diagnostics, diagnostic => diagnostic.Code == "TemplateStylesCssSecurityRejected");
        Assert.Empty(unsafeResult.Stylesheets!);

        repository.Templates["Unsafe/styles.css"] = Template("Template:Unsafe/styles.css", ".x { color: red; ", "malformed", contentModel: "css");
        var malformed = await renderer.RenderAsync("""<templatestyles src="Template:Unsafe/styles.css" />""", ImportedSource(repository));
        Assert.Contains(malformed.Diagnostics, diagnostic => diagnostic.Code == "TemplateStylesCssSyntaxInvalid");

        repository.Templates["Unsafe/styles.css"] = Template("Template:Unsafe/styles.css", ".x { color: red; }", "limited", contentModel: "css");
        var limited = await new MediaWikiWikitextRenderer(repository, new TwConfiguration(), new MediaWikiWikitextRenderOptions
        {
            MaxTemplateStylesDeclarations = 0
        }).RenderAsync("""<templatestyles src="Template:Unsafe/styles.css" />""", ImportedSource(repository));
        Assert.Contains(limited.Diagnostics, diagnostic => diagnostic.Code == "TemplateStylesCssResourceLimitExceeded");

        repository.Templates["Unsafe/styles.css"] = Template("Template:Unsafe/styles.css", "body .x { color: red; }", "global-selector", contentModel: "css");
        var globalSelector = await renderer.RenderAsync("""<templatestyles src="Template:Unsafe/styles.css" />""", ImportedSource(repository));
        Assert.Contains(globalSelector.Diagnostics, diagnostic => diagnostic.Code == "TemplateStylesCssSecurityRejected");

        repository.Templates["Unsafe/styles.css"] = Template("Template:Unsafe/styles.css", ".x .y { color: red; }", "complex-selector", contentModel: "css");
        var complexSelector = await new MediaWikiWikitextRenderer(repository, new TwConfiguration(), new MediaWikiWikitextRenderOptions
        {
            MaxTemplateStylesSelectorComplexity = 1
        }).RenderAsync("""<templatestyles src="Template:Unsafe/styles.css" />""", ImportedSource(repository));
        Assert.Contains(complexSelector.Diagnostics, diagnostic => diagnostic.Code == "TemplateStylesCssResourceLimitExceeded");
    }

    [Fact]
    public async Task KeepsTemplateStylesRegistryRequestScopedAndFailClosedAtReferenceBudget()
    {
        var repository = new FakeMediaWikiImportRepository();
        repository.Templates["A/styles.css"] = Template("Template:A/styles.css", ".a { color: red; }", "a", contentModel: "css");
        repository.Templates["B/styles.css"] = Template("Template:B/styles.css", ".b { color: blue; }", "b", contentModel: "css");
        var renderer = new MediaWikiWikitextRenderer(repository, new TwConfiguration(), new MediaWikiWikitextRenderOptions
        {
            MaxTemplateStylesReferences = 1
        });

        var first = await renderer.RenderAsync("""
            <templatestyles src="Template:A/styles.css" />
            <templatestyles src="Template:B/styles.css" />
            """, ImportedSource(repository));
        var second = await renderer.RenderAsync("""<templatestyles src="Template:B/styles.css" />""", ImportedSource(repository));

        Assert.Single(first.Stylesheets!);
        Assert.Contains(first.Diagnostics, diagnostic => diagnostic.Code == "TemplateStylesCssResourceLimitExceeded");
        Assert.Single(second.Stylesheets!);
        Assert.Equal("b", second.Stylesheets![0].SourceRevisionId);
        Assert.DoesNotContain("<style", second.HtmlResult, StringComparison.OrdinalIgnoreCase);
    }

    private static MediaWikiWikitextRenderer CreateRenderer(FakeMediaWikiImportRepository repository)
        => new(repository, new TwConfiguration { BasePath = "/wiki" });

    private static TwMediaWikiRenderSource ImportedSource(FakeMediaWikiImportRepository? repository = null, string revision = "1000")
        => new(TwPageSourceFormat.ImportedMediaWiki, 7, "100", revision, "Article");

    private static TwMediaWikiTemplateTarget Template(
        string title,
        string body,
        string revision = "t1",
        DateTime? timestamp = null,
        string? contentModel = null,
        int targetRevision = 1)
        => new(TwMediaWikiTemplateResolutionStatus.Resolved, title, title, revision, targetRevision, body, timestamp, contentModel);

    private sealed class ReturnedWikitextExecutor : IMediaWikiScribuntoExecutor
    {
        public Task<MediaWikiScribuntoExecutionResult> ExecuteAsync(
            MediaWikiScribuntoInvocation invocation,
            IMediaWikiScribuntoExecutionHost host,
            MediaWikiScribuntoExecutionContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new MediaWikiScribuntoExecutionResult(
                "{{Inner|value=" + invocation.NamedArguments["value"] + "}}",
                [],
                [new MediaWikiScribuntoDependency("module", invocation.Module.CanonicalTitle, "module-rev-1", "Succeeded")],
                true));
    }

    private sealed class FixedScribuntoExecutor(string? text, bool executed, string? errorCode = null, string? errorMessage = null) : IMediaWikiScribuntoExecutor
    {
        public Task<MediaWikiScribuntoExecutionResult> ExecuteAsync(
            MediaWikiScribuntoInvocation invocation,
            IMediaWikiScribuntoExecutionHost host,
            MediaWikiScribuntoExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            var diagnostics = errorCode is null
                ? Array.Empty<MediaWikiCompatibilityDiagnostic>()
                : [new(errorCode, errorMessage ?? errorCode, invocation.Location.Offset, invocation.Location.Length, invocation.Location.Line, invocation.Location.Column, MediaWikiDiagnosticSeverity.Error)];
            return Task.FromResult(new MediaWikiScribuntoExecutionResult(
                text,
                diagnostics,
                [new MediaWikiScribuntoDependency("module", invocation.Module.CanonicalTitle, "module-rev-1", executed ? "Executed" : "ExecutionFailed")],
                executed));
        }
    }

    private sealed class FakeMediaWikiImportRepository : ITwMediaWikiImportRepository
    {
        public Dictionary<string, TwMediaWikiPageTarget> Pages { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, TwMediaWikiMediaTarget> Media { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, TwMediaWikiTemplateTarget> Templates { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, IReadOnlyList<TwMediaWikiTemplateTarget>> TemplateRevisions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, TwMediaWikiTemplateTarget> Transclusions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, IReadOnlyList<TwMediaWikiTemplateTarget>> TransclusionRevisions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public TimeSpan TemplateResolutionDelay { get; init; }
        public Exception? TemplateResolutionException { get; set; }
        public List<string> TemplateResolutionRequests { get; } = [];
        public List<string> TransclusionRequests { get; } = [];

        public Task<TwMediaWikiRenderSource> GetRenderSourceAsync(int targetPageId, int targetRevision, CancellationToken cancellationToken = default)
            => Task.FromResult(TwMediaWikiRenderSource.Native);

        public Task<TwMediaWikiPageTarget?> ResolvePageAsync(long importBatchId, string sourceTitle, CancellationToken cancellationToken = default)
            => Task.FromResult(Pages.TryGetValue(sourceTitle.Replace('_', ' '), out var target) ? target : null);

        public Task<TwMediaWikiMediaTarget?> ResolveMediaAsync(long importBatchId, string sourcePageId, string sourceRevisionId, string sourceFileName, CancellationToken cancellationToken = default)
            => Task.FromResult(Media.TryGetValue(sourceFileName.Replace('_', ' '), out var target) ? target : null);

        public Task<TwMediaWikiTemplateTarget> ResolveTemplateAsync(long importBatchId, string sourceTitle, DateTime? asOfUtc = null, CancellationToken cancellationToken = default)
            => ResolveTemplateWithDelayAsync(sourceTitle, asOfUtc, cancellationToken);

        public async Task<TwMediaWikiTemplateTarget> ResolveTransclusionAsync(long importBatchId, int sourceNamespaceId, string sourceTitle, DateTime? asOfUtc = null, CancellationToken cancellationToken = default)
        {
            if (sourceNamespaceId == 10) return await ResolveTemplateWithDelayAsync(sourceTitle, asOfUtc, cancellationToken);
            var normalized = CollapseWhitespace(sourceTitle.Replace('_', ' '));
            var key = sourceNamespaceId + ":" + normalized;
            TransclusionRequests.Add(key);
            if (TransclusionRevisions.TryGetValue(key, out var revisions))
            {
                var eligible = revisions
                    .Where(target => asOfUtc is null || target.SourceTimestampUtc is null || target.SourceTimestampUtc <= asOfUtc)
                    .OrderByDescending(target => target.SourceTimestampUtc)
                    .ThenByDescending(target => target.TargetRevision)
                    .FirstOrDefault();
                return eligible ?? new TwMediaWikiTemplateTarget(TwMediaWikiTemplateResolutionStatus.Restricted, sourceTitle, SourceNamespaceId: sourceNamespaceId.ToString());
            }
            return Transclusions.TryGetValue(key, out var target)
                ? target
                : new TwMediaWikiTemplateTarget(TwMediaWikiTemplateResolutionStatus.Missing, sourceTitle, SourceNamespaceId: sourceNamespaceId.ToString());
        }

        private static string CollapseWhitespace(string value)
        {
            var result = new StringBuilder(value.Length);
            var pendingSpace = false;
            foreach (var character in value)
            {
                if (char.IsWhiteSpace(character)) { pendingSpace = result.Length > 0; continue; }
                if (pendingSpace) result.Append(' ');
                result.Append(character);
                pendingSpace = false;
            }
            return result.ToString().Trim();
        }

        private async Task<TwMediaWikiTemplateTarget> ResolveTemplateWithDelayAsync(string sourceTitle, DateTime? asOfUtc, CancellationToken cancellationToken)
        {
            TemplateResolutionRequests.Add(sourceTitle);
            if (TemplateResolutionException is not null)
                throw TemplateResolutionException;
            if (TemplateResolutionDelay > TimeSpan.Zero)
                await Task.Delay(TemplateResolutionDelay, cancellationToken);
            var normalized = sourceTitle.Replace('_', ' ');
            if (normalized.StartsWith("Template:", StringComparison.OrdinalIgnoreCase))
                normalized = normalized["Template:".Length..];
            if (TemplateRevisions.TryGetValue(normalized, out var revisions))
            {
                var eligible = revisions
                    .Where(target => asOfUtc is null || target.SourceTimestampUtc is null || target.SourceTimestampUtc <= asOfUtc)
                    .OrderByDescending(target => target.SourceTimestampUtc)
                    .ThenByDescending(target => target.TargetRevision)
                    .FirstOrDefault();
                return eligible ?? new TwMediaWikiTemplateTarget(TwMediaWikiTemplateResolutionStatus.Restricted, sourceTitle);
            }

            return Templates.TryGetValue(normalized, out var target)
                ? target
                : new TwMediaWikiTemplateTarget(TwMediaWikiTemplateResolutionStatus.Missing, sourceTitle);
        }

        public Task<string> GetTemplateCacheVersionAsync(long importBatchId, DateTime? asOfUtc = null, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Join('|', Templates.OrderBy(pair => pair.Key).Select(pair => pair.Value.CacheKey)));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
