using MediaWikiInventory;
using System.Text.Json;

namespace TightWiki.Tests.Unit;

public sealed class MediaWikiModuleInventoryTests
{
    [Fact]
    public void InventoryPreservesModuleRevisionsApisAndInvocationSitesDeterministically()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "MediaWikiInventory", "Fixtures", "scribunto-moonsharp-compatibility-fixture.xml");
        if (!File.Exists(path)) path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../MediaWikiInventory/Fixtures/scribunto-moonsharp-compatibility-fixture.xml"));
        var report = MediaWikiModuleInventory.Read(path);
        Assert.Equal("cw-m3-006c-v1", report.ReportVersion);
        Assert.NotEmpty(report.Modules);
        Assert.All(report.Modules, module => Assert.All(module.Revisions, revision => Assert.False(string.IsNullOrWhiteSpace(revision.ContentHash))));
        Assert.Equal(report.Modules.OrderBy(m => m.Title, StringComparer.OrdinalIgnoreCase).Select(m => m.Title), report.Modules.Select(m => m.Title));
        Assert.NotNull(report.RepresentativeModules);
        Assert.Equal("DependencyCycle", report.Modules.Single(m => m.Title == "CycleA").DependencyDisposition);
        Assert.Equal("DependencyCycle", report.Modules.Single(m => m.Title == "CycleB").DependencyDisposition);
        Assert.Contains("frame.title-parent", report.Modules.Single(m => m.Title == "UnsupportedCapability").RequiredCapabilities);
    }

    [Fact]
    public void ModuleSubsetPreservesSelectedExportPagesAndRejectsMissingTitles()
    {
        var input = Path.Combine(AppContext.BaseDirectory, "MediaWikiInventory", "Fixtures", "scribunto-moonsharp-compatibility-fixture.xml");
        if (!File.Exists(input)) input = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../MediaWikiInventory/Fixtures/scribunto-moonsharp-compatibility-fixture.xml"));
        var output = Path.Combine(Path.GetTempPath(), "cw-module-subset-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            var selected = MediaWikiModuleSubset.Write(input, output, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Compatibility", "Historical" });
            Assert.Equal(new[] { "Compatibility", "Historical" }, selected);
            var subset = MediaWikiModuleInventory.Read(output);
            Assert.Equal(new[] { "Compatibility", "Historical" }, subset.Modules.Select(value => value.Title));
            Assert.Equal(3, subset.RevisionCount);
            Assert.Throws<InvalidDataException>(() => MediaWikiModuleSubset.Write(input, output, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Missing" }));
        }
        finally
        {
            try { File.Delete(output); } catch { }
        }
    }

    [Fact]
    public void InvocationInventoryRecordsPageRevisionAndArguments()
    {
        var input = Path.Combine(AppContext.BaseDirectory, "MediaWikiInventory", "Fixtures", "scribunto-import-fixture.xml");
        if (!File.Exists(input)) input = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../MediaWikiInventory/Fixtures/scribunto-import-fixture.xml"));
        var report = MediaWikiInvocationInventory.Read(input);
        var invocation = Assert.Single(report.Invocations);
        Assert.Equal("Compatibility Article", invocation.PageTitle);
        Assert.Equal("1001", invocation.SourceRevisionId);
        Assert.Equal("Compatibility", invocation.ModuleTitle);
        Assert.Equal("main", invocation.Function);
        Assert.Equal("  positional  ", Assert.Single(invocation.PositionalArguments));
        Assert.Equal("", invocation.NamedArguments["empty"]);
        Assert.Equal("é✓中", invocation.NamedArguments["unicode"]);
        Assert.Equal("page", invocation.ContextKind);
        Assert.Equal("CandidatePageUsage", invocation.UsageDisposition);
    }

    [Fact]
    public void CompatibilitySubsetRetainsAllRequestedRevisionsForSharedRootPages()
    {
        var input = Path.Combine(AppContext.BaseDirectory, "MediaWikiInventory", "Fixtures", "scribunto-import-fixture.xml");
        if (!File.Exists(input)) input = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../MediaWikiInventory/Fixtures/scribunto-import-fixture.xml"));
        var corpus = Path.GetTempFileName();
        var output = Path.GetTempFileName();
        try
        {
            File.WriteAllText(corpus, JsonSerializer.Serialize(new
            {
                Cases = new[]
                {
                    new { PageTitle = "Compatibility Article", SourceRevisionId = "1001", ModuleTitle = "Compatibility" },
                    new { PageTitle = "Historical", SourceRevisionId = "1271", ModuleTitle = "Historical" },
                }
            }));
            var selected = MediaWikiCompatibilitySubset.Write(input, corpus, output);
            Assert.Equal(3, selected.Pages);
            using var stream = new MediaWikiXmlReader().Open(output);
            var pages = stream.ReadPages().ToArray();
            Assert.Equal(new[] { "1001" }, pages.Single(page => page.Title == "Compatibility Article").Revisions.Select(value => value.SourceRevisionId));
            Assert.Equal(new[] { "1271", "1272" }, pages.Single(page => page.Title == "Historical").Revisions.Select(value => value.SourceRevisionId));
        }
        finally
        {
            File.Delete(corpus);
            File.Delete(output);
        }
    }

}
