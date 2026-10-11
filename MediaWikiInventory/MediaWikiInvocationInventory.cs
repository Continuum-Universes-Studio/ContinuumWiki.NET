using System.Security.Cryptography;
using System.Text.Json;

namespace MediaWikiInventory;

public sealed record MediaWikiInvocationInventoryReport(
    string ReportVersion,
    string SourcePath,
    string SourceSha256,
    int InvocationCount,
    IReadOnlyList<MediaWikiInvocationInventoryEntry> Invocations,
    IReadOnlyList<string> Diagnostics);

public sealed record MediaWikiInvocationInventoryEntry(
    string FixtureId,
    string PageTitle,
    string SourcePageId,
    string SourceRevisionId,
    DateTimeOffset? RevisionTimestamp,
    string ModuleTitle,
    string Function,
    IReadOnlyList<string> PositionalArguments,
    IReadOnlyDictionary<string, string> NamedArguments,
    int SourceOffset,
    string RawInvocation,
    string ContextKind,
    string UsageDisposition,
    string DependencyDisposition);

public static class MediaWikiInvocationInventory
{
    public const string ReportVersion = "cw-m3-006c.2-invocations-v1";

    public static MediaWikiInvocationInventoryReport Read(string path)
    {
        var fullPath = Path.GetFullPath(path);
        using var stream = new MediaWikiXmlReader().Open(fullPath);
        var entries = new List<MediaWikiInvocationInventoryEntry>();
        foreach (var page in stream.ReadPages())
        foreach (var revision in page.Revisions)
        {
            var scan = MediaWikiScribuntoScanner.Scan(revision.Wikitext, page.SourcePageId, revision.SourceRevisionId);
            foreach (var invocation in scan.Invocations.Where(value => !value.IsMalformed && !value.IsDynamic && value.ModuleTitle is not null && value.Function is not null))
            {
                var module = MediaWikiScribuntoScanner.NormalizeModule(invocation.ModuleTitle!);
                entries.Add(new(
                    CreateFixtureId(module + "|" + page.SourcePageId + "|" + revision.SourceRevisionId + "|" + invocation.Offset),
                    page.Title, page.SourcePageId, revision.SourceRevisionId, revision.Timestamp, module, invocation.Function!,
                    invocation.PositionalArguments, invocation.NamedArguments, invocation.Offset, invocation.RawText,
                    page.SourceNamespaceId == "0" ? "page" : page.SourceNamespaceId == "10" ? "template" : page.SourceNamespaceId == "828" ? "module-documentation" : "other",
                    page.SourceNamespaceId == "828" ? "DocumentationExample" : "CandidatePageUsage",
                    "RequiresRevisionAwareResolution"));
            }
        }
        using var source = File.OpenRead(fullPath);
        var sourceHash = Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
        return new(ReportVersion, Path.GetFileName(fullPath), sourceHash, entries.Count,
            entries.OrderBy(value => value.PageTitle, StringComparer.OrdinalIgnoreCase).ThenBy(value => value.SourceRevisionId, StringComparer.OrdinalIgnoreCase).ThenBy(value => value.SourceOffset).ToArray(), []);
    }

    private static string CreateFixtureId(string value)
        => "mw-invoke-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..16];

    public static string Serialize(MediaWikiInvocationInventoryReport report)
        => JsonSerializer.Serialize(report, MediaWikiModuleInventory.JsonOptions());
}
