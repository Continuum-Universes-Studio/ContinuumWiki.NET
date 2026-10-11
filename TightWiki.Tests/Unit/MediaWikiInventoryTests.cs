using MediaWikiInventory;

namespace TightWiki.Tests.Unit;

public sealed class MediaWikiInventoryTests
{
    [Fact]
    public void SyntheticExportDetectsPreservationRisksWithoutImporting()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "MediaWikiInventory", "Fixtures", "synthetic-export.xml");
        if (!File.Exists(path))
            path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../MediaWikiInventory/Fixtures/synthetic-export.xml"));

        var report = InventoryReport.Read(path);

        Assert.Equal(3, report.PageCount);
        Assert.Equal(4, report.RevisionCount);
        Assert.Equal(3, report.CurrentArticleBodyCount);
        Assert.Equal(4, report.RevisionTimestampCount);
        Assert.Equal(3, report.NamespaceCount);
        Assert.Contains("10:Template", report.Namespaces);
        Assert.Contains("6:File", report.Namespaces);
        Assert.Equal(1, report.RedirectCount);
        Assert.Equal(3, report.DistinctContributorCount);
        Assert.Equal(2, report.AnonymousContributorCount);
        Assert.Equal(1, report.TemplateDefinitionPageCount);
        Assert.Equal(1, report.TemplateTransclusionCount);
        Assert.Equal(1, report.TemplateParameterCount);
        Assert.Equal(2, report.ParserFunctionCount);
        Assert.Equal(1, report.LuaInvocationCount);
        Assert.Equal(2, report.ReferenceTagCount);
        Assert.Equal(1, report.CategoryLinkCount);
        Assert.Equal(1, report.FileLinkCount);
        Assert.Equal(1, report.FilePageCount);
        Assert.Equal(1, report.FileMetadataRecordCount);
        Assert.Equal(1, report.MagicWordCount);
        Assert.Equal(1, report.LanguageLinkCount);
        Assert.Equal(1, report.LanguageVariantMarkupCount);
        Assert.Contains("custom-tag", report.ExtensionTags);
        Assert.Contains("7", report.SourcePageIds);
        Assert.Contains("104", report.SourceRevisionIds);
        Assert.Contains("世界", report.PageTitles);
    }
}
