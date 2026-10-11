using System.Security.Cryptography;
using System.Text;
using MediaWikiInventory;

namespace TightWiki.Tests.Unit;

public sealed class MediaWikiXmlImportTests
{
    [Fact]
    public void StreamingReaderPreservesIdsAttributionAndExactWikitext()
    {
        using var stream = new MediaWikiXmlReader().Open(Fixture("import-fixture.xml"));
        Assert.Equal("Continuum 世界", stream.Header.SiteName);
        Assert.Equal("0.11", stream.Header.ExportVersion);
        Assert.Contains(stream.Header.Namespaces, value => value.SourceNamespaceId == "10" && value.Name == "Template");

        var pages = stream.ReadPages().ToArray();
        var page = Assert.Single(pages, value => value.SourcePageId == "42");
        Assert.Equal("世界", page.Title);
        Assert.Equal("0", page.SourceNamespaceId);
        Assert.Contains(page.UnsupportedElements, value => value.Name == "extension-data");
        Assert.Equal(2, page.Revisions.Count);

        var first = page.Revisions[0];
        Assert.Equal("4201", first.SourceRevisionId);
        Assert.Equal("4200", first.ParentRevisionId);
        Assert.Equal(DateTimeOffset.Parse("2025-02-03T04:05:06Z"), first.Timestamp);
        Assert.Equal(MediaWikiContributorKind.NamedUser, first.Contributor.Kind);
        Assert.Equal("Alice 世界", first.Contributor.DisplayName);
        Assert.Equal("Preserve exact source", first.Comment);
        Assert.Equal("wikitext", first.ContentModel);
        Assert.Equal("text/x-wiki", first.ContentFormat);
        Assert.Equal("abc123", first.Sha1);
        Assert.False(first.IsMinor);
        Assert.Equal("== 世界 ==\n{{Infobox|name=世界|raw=&value}}\n<ref name=\"source\">A & B</ref>\n[[Category:Lore]] [[File:Map.png|thumb|Caption]] {{#invoke:World|render}}\n<custom-tag data-x=\"1\">unknown</custom-tag>", first.Wikitext);
        Assert.True(first.WikitextPresent);

        var second = page.Revisions[1];
        Assert.Equal(MediaWikiContributorKind.AnonymousIp, second.Contributor.Kind);
        Assert.Equal("192.0.2.10", second.Contributor.IpAddress);
        Assert.True(second.IsDeleted);
        var missingOptionalFields = Assert.Single(pages, value => value.SourcePageId == "44");
        Assert.Contains(missingOptionalFields.Revisions[0].Warnings, value => value.Code == "MissingTimestamp");

        var redirect = Assert.Single(pages, value => value.SourcePageId == "43");
        Assert.True(redirect.IsRedirect);
        Assert.Equal("世界", redirect.RedirectTarget);
    }

    [Fact]
    public void PreviewReportsHashesModelsWarningsAndContributorsOffline()
    {
        var preview = MediaWikiImportPreview.Create(Fixture("import-fixture.xml"));

        Assert.Equal(3, preview.PageCount);
        Assert.Equal(4, preview.RevisionCount);
        Assert.Equal(1, preview.RedirectCount);
        Assert.Equal(3, preview.NamespaceDistribution["0"]);
        Assert.Equal(2, preview.ContributorDistribution["AnonymousIp"]);
        Assert.Equal(1, preview.ContentModelDistribution["unknown-model"]);
        Assert.Equal(2, preview.ContentModelDistribution["UNSPECIFIED"]);
        var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("Body"))).ToLowerInvariant();
        Assert.Equal(expectedHash, preview.WikitextContentHashes["44/4401"]);
        Assert.True(preview.WarningCount >= 2);
        Assert.True(preview.UnsupportedElementCount >= 2);
    }

    [Fact]
    public void MalformedAndExternalEntityXmlAreRejectedSafely()
    {
        Assert.Throws<MediaWikiImportFormatException>(() =>
        {
            using var malformed = new MediaWikiXmlReader().Open(Fixture("malformed-export.xml"));
            malformed.ReadPages().ToArray();
        });
        Assert.Throws<MediaWikiImportFormatException>(() =>
        {
            using var externalEntity = new MediaWikiXmlReader().Open(Fixture("external-entity-export.xml"));
            externalEntity.ReadPages().ToArray();
        });
    }

    private static string Fixture(string name)
    {
        var outputPath = Path.Combine(AppContext.BaseDirectory, "MediaWikiInventory", "Fixtures", name);
        return File.Exists(outputPath) ? outputPath : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../MediaWikiInventory/Fixtures", name));
    }
}
