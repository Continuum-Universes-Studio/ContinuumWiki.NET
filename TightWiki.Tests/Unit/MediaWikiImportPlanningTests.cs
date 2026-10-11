using System.Text.Json;
using MediaWikiImport;
using MediaWikiInventory;

namespace TightWiki.Tests.Unit;

public sealed class MediaWikiImportPlanningTests
{
    [Fact]
    public void PlannerPreservesSourceDataAndProducesBlockingDependencyFindings()
    {
        var input = Fixture("import-fixture.xml");
        var mapping = NamespaceMappingConfiguration.Load(Fixture("namespace-mapping.json"));
        var plan = MediaWikiImportPlanner.Create(input, mapping);

        Assert.Equal(3, plan.PlannedPageCount);
        Assert.Equal(4, plan.PlannedRevisionCount);
        Assert.Equal(ImportDisposition.Blocked, plan.OverallDisposition);
        var page = Assert.Single(plan.Pages, value => value.SourcePageId == "42");
        Assert.Equal("世界", page.OriginalTitle);
        Assert.Equal("世界", page.ProposedTargetNavigation);
        Assert.Equal("== 世界 ==\n{{Infobox|name=世界|raw=&value}}\n<ref name=\"source\">A & B</ref>\n[[Category:Lore]] [[File:Map.png|thumb|Caption]] {{#invoke:World|render}}\n<custom-tag data-x=\"1\">unknown</custom-tag>", page.Revisions[0].Wikitext);
        Assert.Contains(plan.Findings, value => value.Code == "MissingTemplateDependency" && value.SourcePageId == "42");
        Assert.Contains(plan.Findings, value => value.Code == "MissingModuleDependency" && value.SourcePageId == "42");
        Assert.Contains(plan.Findings, value => value.Code == "UnsupportedExtensionTag");
        Assert.Empty(ImportPlanValidator.Validate(plan, input, mapping));
    }

    [Fact]
    public void PlannerDetectsNavigationAndSourceIdentityConflicts()
    {
        var mapping = NamespaceMappingConfiguration.Load(Fixture("namespace-mapping.json"));
        var collision = MediaWikiImportPlanner.Create(Fixture("planning-collision.xml"), mapping);
        Assert.Contains(collision.Findings, value => value.Code == "TargetNavigationCollision" && value.Severity == PlanFindingSeverity.Blocking);
        Assert.Equal(ImportDisposition.Blocked, collision.OverallDisposition);

        var conflicts = MediaWikiImportPlanner.Create(Fixture("planning-conflicts.xml"), mapping);
        Assert.Contains(conflicts.Findings, value => value.Code == "DuplicateSourcePageId");
        Assert.Contains(conflicts.Findings, value => value.Code == "DuplicateSourceRevisionId");
        Assert.Contains(conflicts.Findings, value => value.Code == "InvalidRedirectTarget");
    }

    [Fact]
    public void NamespaceAliasesResolveOnlyAgainstTheirDeclaringRule()
    {
        var mapping = new NamespaceMappingConfiguration("alias-test", [
            new("100", "File", ["File", "Image"]),
            new("101", "Template", ["Template"])
        ]);

        var image = mapping.Resolve("999", "Image", null);
        var template = mapping.Resolve("999", "Template", null);
        var unknown = mapping.Resolve("999", "Unknown", null);

        Assert.Equal("File", image.TargetNamespace);
        Assert.Equal("Template", template.TargetNamespace);
        Assert.False(unknown.IsMapped);
    }

    [Fact]
    public void PlanJsonIsDeterministicAndChecksumValidationRejectsDifferentInput()
    {
        var input = Fixture("planning-collision.xml");
        var mapping = NamespaceMappingConfiguration.Load(Fixture("namespace-mapping.json"));
        var first = JsonSerializer.Serialize(MediaWikiImportPlanner.Create(input, mapping), JsonOptions());
        var second = JsonSerializer.Serialize(MediaWikiImportPlanner.Create(input, mapping), JsonOptions());
        Assert.Equal(first, second);

        var otherInput = Path.GetTempFileName();
        try
        {
            File.Copy(input, otherInput, true);
            File.AppendAllText(otherInput, "\n");
            var plan = MediaWikiImportPlanner.Create(input, mapping);
            Assert.Contains(ImportPlanValidator.Validate(plan, otherInput, mapping), value => value.Code == "InputChecksumMismatch");
        }
        finally { File.Delete(otherInput); }
    }

    [Fact]
    public void PlanCommandWritesJsonWithoutDatabaseAccess()
    {
        var output = Path.GetTempFileName();
        var secondOutput = Path.GetTempFileName();
        try
        {
            var exitCode = PlannerCli.Run(["plan", "--input", Fixture("planning-collision.xml"), "--output", output, "--mapping", Fixture("namespace-mapping.json")]);
            Assert.Equal(3, exitCode);
            Assert.Equal(3, PlannerCli.Run(["plan", "--input", Fixture("planning-collision.xml"), "--output", secondOutput, "--mapping", Fixture("namespace-mapping.json")]));
            Assert.Equal(File.ReadAllText(output), File.ReadAllText(secondOutput));
            Assert.Equal(0, PlannerCli.Run(["validate", "--plan", output, "--input", Fixture("planning-collision.xml"), "--mapping", Fixture("namespace-mapping.json")]));
            using var json = JsonDocument.Parse(File.ReadAllText(output));
            Assert.Equal("CW-M3-005A.1", json.RootElement.GetProperty("PlannerVersion").GetString());
            Assert.Equal("Blocked", json.RootElement.GetProperty("OverallDisposition").GetString());
        }
        finally { File.Delete(output); File.Delete(secondOutput); }
    }

    [Fact]
    public void PlannerClassifiesContinuumInfoboxAsUnsupportedCustomParserFunction()
    {
        var input = Path.GetTempFileName();
        try
        {
            File.WriteAllText(input, """<?xml version="1.0"?><mediawiki version="0.11"><siteinfo><namespaces><namespace key="0" /></namespaces></siteinfo><page><title>Infobox case</title><ns>0</ns><id>1</id><revision><id>11</id><timestamp>2026-01-01T00:00:00Z</timestamp><model>wikitext</model><text>{{#cuinfobox:species|name=Example}}</text></revision></page></mediawiki>""");
            var plan = MediaWikiImportPlanner.Create(input, NamespaceMappingConfiguration.Defaults);
            var finding = plan.Findings.First(value => value.Code == "UnsupportedCustomParserFunction");
            Assert.Contains("#cuinfobox", finding.Message);
            Assert.DoesNotContain(plan.Findings, value => value.Code == "MissingModuleDependency");
        }
        finally { File.Delete(input); }
    }

    [Fact]
    public void PlannerDoesNotTreatPortableInfoboxSchemaTagsAsExtensionTags()
    {
        var input = Path.GetTempFileName();
        try
        {
            File.WriteAllText(input, """<?xml version="1.0"?><mediawiki version="0.11"><siteinfo><namespaces><namespace key="0" /></namespaces></siteinfo><page><title>Schema case</title><ns>0</ns><id>1</id><revision><id>11</id><timestamp>2026-01-01T00:00:00Z</timestamp><model>wikitext</model><text>&lt;infobox&gt;&lt;group&gt;&lt;header&gt;Audio&lt;/header&gt;&lt;data source="x"&gt;&lt;default&gt;Default&lt;/default&gt;&lt;format&gt;Value&lt;/format&gt;&lt;audio source="audio" /&gt;&lt;/data&gt;&lt;/group&gt;&lt;/infobox&gt;&lt;custom-extension&gt;x&lt;/custom-extension&gt;</text></revision></page></mediawiki>""");
            var plan = MediaWikiImportPlanner.Create(input, NamespaceMappingConfiguration.Defaults);
            Assert.DoesNotContain(plan.Findings, value => value.Code == "UnsupportedExtensionTag" && value.Message.Contains("<infobox>"));
            Assert.DoesNotContain(plan.Findings, value => value.Code == "UnsupportedExtensionTag" && value.Message.Contains("<audio>"));
            Assert.Contains(plan.Findings, value => value.Code == "UnsupportedExtensionTag" && value.Message.Contains("<custom-extension>"));
        }
        finally { File.Delete(input); }
    }

    [Fact]
    public void DiagnosticPolicyAllowsOnlyAuditedRenderingTagsAndNeverMissingDependencies()
    {
        Assert.True(MediaWikiImportPolicyRules.IsAllowedDiagnosticFinding(
            new(PlanFindingSeverity.Blocking, "UnsupportedExtensionTag", "Extension or unknown tag <templatedata> was found.")));
        Assert.True(MediaWikiImportPolicyRules.IsAllowedDiagnosticFinding(
            new(PlanFindingSeverity.Blocking, "UnsupportedExtensionTag", "Extension or unknown tag <mermaid> was found.")));
        Assert.True(MediaWikiImportPolicyRules.IsAllowedDiagnosticFinding(
            new(PlanFindingSeverity.Blocking, "UnsupportedExtensionTag", "Extension or unknown tag <templatestyles> was found.")));
        Assert.False(MediaWikiImportPolicyRules.IsAllowedDiagnosticFinding(
            new(PlanFindingSeverity.Blocking, "UnsupportedExtensionTag", "Extension or unknown tag <syntaxhighlight> was found.")));
        Assert.False(MediaWikiImportPolicyRules.IsAllowedDiagnosticFinding(
            new(PlanFindingSeverity.Blocking, "MissingModuleDependency", "Module dependency 'Example' is not present in the export.")));
    }

    [Fact]
    public void DiagnosticAuthorizationRequiresHarnessMarkerAndDisposableTargetIdentity()
    {
        Assert.True(MediaWikiDiagnosticAuthorization.IsAuthorized("1"));
        Assert.False(MediaWikiDiagnosticAuthorization.IsAuthorized(null));
        Assert.False(MediaWikiDiagnosticAuthorization.IsAuthorized("true"));

        var disposable = new MediaWikiImportTargetConfiguration(
            "Server=127.0.0.1;Database=cw_m3_test;User ID=root;Password=not-used;",
            "cw_m3_test", "disposable-mariadb");
        var production = disposable with { DatabaseName = "continuumwiki", ConnectionString = disposable.ConnectionString.Replace("cw_m3_test", "continuumwiki") };
        Assert.True(MediaWikiDiagnosticAuthorization.IsDisposableTarget(disposable));
        Assert.False(MediaWikiDiagnosticAuthorization.IsDisposableTarget(production));
    }

    private static string Fixture(string name)
    {
        var outputPath = Path.Combine(AppContext.BaseDirectory, "MediaWikiInventory", "Fixtures", name);
        return File.Exists(outputPath) ? outputPath : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../MediaWikiInventory/Fixtures", name));
    }

    private static JsonSerializerOptions JsonOptions() => new() { WriteIndented = false };
}
