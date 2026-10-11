using System.Globalization;
using TightWiki.Engine.MediaWiki;
using TightWiki.Plugin.Models;
using MediaWikiInventory;

namespace TightWiki.Tests.Unit;

public sealed class MediaWikiScribuntoBoundaryTests
{
    [Fact]
    public void TypedInvocationPreservesArgumentOrderingAndCallingContext()
    {
        var expansion = new MediaWikiExpansionContext(
            "Article", "0", string.Empty, "wiki|url|generator", "42", "4201", 7, 3,
            new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc), "wikitext", CultureInfo.InvariantCulture,
            TimeZoneInfo.Utc, DateTimeOffset.UnixEpoch, "Fixture", "en");

        var parsed = MediaWikiScribuntoInvocationParser.TryParse(
            "{{#invoke:Module:世界|render|first|name=value|second}}",
            "#invoke:Module:世界|render|first|name=value|second",
            expansion, ["Template:Invoker"], 12, out var invocation);

        Assert.True(parsed);
        Assert.Equal("世界", invocation.Module.Name);
        Assert.Equal("render", invocation.ExportedFunction);
        Assert.Equal(["first", "second"], invocation.PositionalArguments);
        Assert.Equal("value", invocation.NamedArguments["name"]);
        Assert.Equal("Article", invocation.CallingPageTitle);
        Assert.Equal(["Template:Invoker"], invocation.TemplateInvocationChain);
    }

    [Fact]
    public void ScannerFindsStaticLoadersAndLabelsDynamicReferencesWithoutExecutingLua()
    {
        var result = MediaWikiScribuntoScanner.Scan("{{#invoke:Tools|render|x=1}} require('Module:Data') mw.loadData(\"Config\") mw.loadJsonData(jsonName) require(name)");

        Assert.Contains(result.Invocations, value => value.ModuleTitle == "Tools" && value.Function == "render");
        Assert.Contains("Tools", result.Dependencies);
        Assert.Contains("Data", result.Dependencies);
        Assert.Contains("Config", result.Dependencies);
        Assert.Contains("<dynamic>", result.UncertainDependencies);
        Assert.Contains(result.Findings, value => value.Code == "DynamicModuleReference");
    }

    [Fact]
    public async Task DefaultExecutorRejectsExecutionAndReturnsStructuredDiagnostic()
    {
        var expansion = new MediaWikiExpansionContext("Article", "0", string.Empty, "wiki", "42", "4201", 7, 3, null, "wikitext", CultureInfo.InvariantCulture, TimeZoneInfo.Utc, DateTimeOffset.UnixEpoch, "Fixture", "en");
        Assert.True(MediaWikiScribuntoInvocationParser.TryParse("{{#invoke:Tools|render|x}}", "#invoke:Tools|render|x", expansion, [], 0, out var invocation));
        var context = new MediaWikiScribuntoExecutionContext(
            new("Article", "42", "4201", null, "wikitext", new("wiki")),
            new(MaxInvocations: 1, MaxModuleDependencyDepth: 1, MaxModuleSourceCharacters: 100, MaxArgumentCharacters: 100, MaxResultCharacters: 100, MaxInstructionCount: 10, MaxExecutionTime: TimeSpan.FromMilliseconds(1), MaxMemoryBytes: 1024),
            0, []);
        var host = new RecordingHost();

        var result = await new RejectingMediaWikiScribuntoExecutor().ExecuteAsync(invocation, host, context);

        Assert.False(result.Executed);
        Assert.Equal(invocation.OriginalText, result.Text);
        Assert.Contains(result.Diagnostics, value => value.Code == "ScribuntoExecutionRejected");
        Assert.Single(result.Dependencies);
        Assert.Empty(host.Registered);
        Assert.Equal(0, host.CallCount);
    }

    [Fact]
    public void BudgetValidatorDiagnosesMeasurableInvocationLimitViolations()
    {
        var expansion = new MediaWikiExpansionContext("Article", "0", string.Empty, "wiki", "42", "4201", 7, 3, null, "wikitext", CultureInfo.InvariantCulture, TimeZoneInfo.Utc, DateTimeOffset.UnixEpoch, "Fixture", "en");
        Assert.True(MediaWikiScribuntoInvocationParser.TryParse("{{#invoke:Tools|render|123456}}", "#invoke:Tools|render|123456", expansion, [], 0, out var invocation));
        var context = new MediaWikiScribuntoExecutionContext(new("Article", "42", "4201", null, "wikitext", new("wiki")), new(MaxInvocations: 1, MaxModuleDependencyDepth: 1, MaxModuleSourceCharacters: 4, MaxArgumentCharacters: 4, MaxResultCharacters: 4), 2, []);

        var diagnostics = MediaWikiScribuntoBudgetValidator.Validate(invocation, context, 2, 8, 9);

        Assert.Contains(diagnostics, value => value.Code == "ScribuntoInvocationBudgetExceeded");
        Assert.Contains(diagnostics, value => value.Code == "ScribuntoDependencyDepthExceeded");
        Assert.Contains(diagnostics, value => value.Code == "ScribuntoModuleSourceBudgetExceeded");
        Assert.Contains(MediaWikiScribuntoBudgetValidator.Validate(invocation, context, 1, 1, 1, 4_000_001), value => value.Code == "ScribuntoDependencySourceBudgetExceeded");
        Assert.Contains(diagnostics, value => value.Code == "ScribuntoArgumentBudgetExceeded");
        Assert.Contains(diagnostics, value => value.Code == "ScribuntoResultBudgetExceeded");
    }

    [Fact]
    public void PlannerRecordsModuleClosureMissingModulesCyclesAndUnsupportedModels()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "MediaWikiInventory", "Fixtures", "scribunto-boundary-fixture.xml");
        if (!File.Exists(fixture)) fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../MediaWikiInventory/Fixtures/scribunto-boundary-fixture.xml"));
        var plan = MediaWikiImportPlanner.Create(fixture, NamespaceMappingConfiguration.Defaults);

        var article = Assert.Single(plan.Pages, value => value.OriginalTitle == "Invocation Article");
        var revision = Assert.Single(article.Revisions);
        Assert.Contains("Tools", revision.Dependencies.Modules);
        Assert.Contains(revision.Dependencies.ModuleInvocations ?? [], value => value.ModuleTitle == "Tools");
        Assert.Contains(plan.Findings, value => value.Code == "MissingModuleDependency");
        Assert.Contains(plan.Findings, value => value.Code == "CyclicModuleDependency");
        Assert.Contains(plan.Findings, value => value.Code == "UnsupportedContentModel");
        Assert.Contains(plan.Findings, value => value.Code == "DynamicModuleReference");
        var moduleA = Assert.Single(plan.Pages, value => value.OriginalTitle == "A");
        Assert.NotEmpty(Assert.Single(moduleA.Revisions).Dependencies.ModuleCycles ?? []);
    }

    [Fact]
    public void InventoryReportsModulePagesInvocationsDependenciesAndCycles()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "MediaWikiInventory", "Fixtures", "scribunto-boundary-fixture.xml");
        if (!File.Exists(fixture)) fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../MediaWikiInventory/Fixtures/scribunto-boundary-fixture.xml"));
        var report = InventoryReport.Read(fixture);

        Assert.Equal(5, report.ModulePageCount);
        Assert.Equal(5, report.ModuleInvocationCount);
        Assert.True(report.ModuleDependencyCount >= 6);
        Assert.Equal(1, report.ModuleCycleCount);
        Assert.Contains("世界", report.ModuleTitles);
        Assert.Contains("Data", report.ModuleDependencies);
        Assert.Contains("<dynamic>", report.UncertainModuleDependencies);
    }

    private sealed class RecordingHost : IMediaWikiScribuntoExecutionHost
    {
        public List<MediaWikiScribuntoDependency> Registered { get; } = [];
        public int CallCount { get; private set; }
        public Task<string?> ResolveModuleSourceAsync(MediaWikiModuleTitle module, DateTime? asOfUtc, CancellationToken cancellationToken = default) { CallCount++; return Task.FromResult<string?>(null); }
        public Task<string?> ResolveTemplateTextAsync(string templateTitle, DateTime? asOfUtc, CancellationToken cancellationToken = default) { CallCount++; return Task.FromResult<string?>(null); }
        public Task<MediaWikiScribuntoPageMetadata?> ResolvePageMetadataAsync(string title, DateTime? asOfUtc, CancellationToken cancellationToken = default) { CallCount++; return Task.FromResult<MediaWikiScribuntoPageMetadata?>(null); }
        public void RegisterDependency(MediaWikiScribuntoDependency dependency) => Registered.Add(dependency);
    }
}
