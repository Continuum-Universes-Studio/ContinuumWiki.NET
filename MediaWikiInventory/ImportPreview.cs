using System.Security.Cryptography;
using System.Text;

namespace MediaWikiInventory;

public sealed record MediaWikiImportPreview(
    MediaWikiImportHeader Header,
    int PageCount,
    int RevisionCount,
    int RedirectCount,
    IReadOnlyDictionary<string, int> NamespaceDistribution,
    IReadOnlyDictionary<string, int> ContributorDistribution,
    IReadOnlyDictionary<string, int> ContentModelDistribution,
    IReadOnlyDictionary<string, string> WikitextContentHashes,
    int WarningCount,
    int UnsupportedElementCount)
{
    public static MediaWikiImportPreview Create(string path)
    {
        using var stream = new MediaWikiXmlReader().Open(path);
        var namespaces = new Dictionary<string, int>(StringComparer.Ordinal);
        var contributors = new Dictionary<string, int>(StringComparer.Ordinal);
        var models = new Dictionary<string, int>(StringComparer.Ordinal);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var warnings = stream.Header.Warnings.Count;
        var unsupported = stream.Header.UnsupportedElements.Count;
        var pages = 0;
        var revisions = 0;
        var redirects = 0;
        foreach (var page in stream.ReadPages())
        {
            pages++;
            Increment(namespaces, page.SourceNamespaceId);
            if (page.IsRedirect) redirects++;
            warnings += page.Warnings.Count;
            unsupported += page.UnsupportedElements.Count;
            foreach (var revision in page.Revisions)
            {
                revisions++;
                var contributor = revision.Contributor.Kind.ToString();
                Increment(contributors, contributor);
                Increment(models, string.IsNullOrEmpty(revision.ContentModel) ? "UNSPECIFIED" : revision.ContentModel);
                hashes[$"{page.SourcePageId}/{revision.SourceRevisionId}"] = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(revision.Wikitext))).ToLowerInvariant();
                warnings += revision.Warnings.Count;
                unsupported += revision.UnsupportedElements.Count;
            }
        }
        return new(stream.Header, pages, revisions, redirects, namespaces, contributors, models, hashes, warnings, unsupported);
    }

    private static void Increment(IDictionary<string, int> values, string key) =>
        values[key] = values.TryGetValue(key, out var count) ? count + 1 : 1;
}
