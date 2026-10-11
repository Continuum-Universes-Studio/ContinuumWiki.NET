using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaWikiInventory;

public sealed record MediaWikiModuleInventoryReport(
    string ReportVersion,
    string SourcePath,
    string SourceSha256,
    int ModuleCount,
    int RevisionCount,
    int InvocationCount,
    IReadOnlyList<MediaWikiModuleInventoryEntry> Modules,
    IReadOnlyList<MediaWikiModuleSelection> RepresentativeModules,
    IReadOnlyList<string> Diagnostics);

public sealed record MediaWikiModuleInventoryEntry(
    string Title,
    string NamespaceId,
    string ContentModel,
    IReadOnlyList<MediaWikiModuleRevisionInventory> Revisions,
    IReadOnlyList<string> StaticDependencies,
    IReadOnlyList<string> DynamicDependencyWarnings,
    IReadOnlyList<string> ReferencedMwApis,
    IReadOnlyList<string> ReferencedCwApis,
    IReadOnlyList<string> UnsupportedApiReferences,
    IReadOnlyList<MediaWikiModuleInvocationSite> InvocationSites,
    IReadOnlyList<string> RequiredCapabilities,
    string DependencyDisposition);

public sealed record MediaWikiModuleRevisionInventory(
    string SourcePageId,
    string SourceRevisionId,
    DateTimeOffset? Timestamp,
    string ContentHash,
    bool IsHistorical,
    bool IsDeleted,
    bool IsSuppressed);

public sealed record MediaWikiModuleInvocationSite(
    string SourcePageId,
    string SourceRevisionId,
    string Function,
    int Offset,
    IReadOnlyList<string> PositionalArguments,
    IReadOnlyDictionary<string, string> NamedArguments);

public sealed record MediaWikiModuleSelection(string Title, string Rationale, string Category);

public static class MediaWikiModuleInventory
{
    public const string ReportVersion = "cw-m3-006c-v1";

    private static readonly string[] KnownApis = ["mw.text", "mw.ustring", "mw.title", "mw.site", "mw.language", "mw.loadData", "mw.loadJsonData", "cw.", "require"];
    private static readonly string[] UnsupportedApis = ["mw.getCurrentFrame", "mw.ext", "mw.dumpObject", "mw.executeFunction", "os.", "io.", "package.", "debug."];

    public static MediaWikiModuleInventoryReport Read(string path)
    {
        var fullPath = Path.GetFullPath(path);
        using var stream = new MediaWikiXmlReader().Open(fullPath);
        var pages = stream.ReadPages().ToArray();
        var modulePages = pages.Where(p => p.SourceNamespaceId == "828").ToArray();
        var moduleNames = modulePages.Select(p => Normalize(p.Title)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var graph = modulePages.ToDictionary(p => Normalize(p.Title), p => p.Revisions.SelectMany(r => MediaWikiScribuntoScanner.Scan(r.Wikitext).Dependencies).Where(moduleNames.Contains).Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
        var cycles = FindCycles(graph);
        var modules = modulePages.Select(p => CreateEntry(p, pages, cycles)).OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase).ToArray();
        var invocations = modules.Sum(p => p.InvocationSites.Count);
        var diagnostics = modules.SelectMany(p => p.Revisions.Count == 0 ? [$"MissingRevision:{p.Title}"] : Array.Empty<string>()).Order(StringComparer.Ordinal).ToArray();
        return new(ReportVersion, Path.GetFileName(fullPath), Sha256File(fullPath), modules.Length, modules.Sum(p => p.Revisions.Count), invocations,
            modules, SelectRepresentatives(modules), diagnostics);
    }

    public static JsonSerializerOptions JsonOptions(bool indented = true) => new()
    {
        WriteIndented = indented, PropertyNamingPolicy = null,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    private static MediaWikiModuleInventoryEntry CreateEntry(MediaWikiPage page, IReadOnlyList<MediaWikiPage> pages, IReadOnlySet<string> cycles)
    {
        var revisions = page.Revisions.Select((revision, index) => new MediaWikiModuleRevisionInventory(page.SourcePageId, revision.SourceRevisionId,
            revision.Timestamp, Sha256(revision.Wikitext), index < page.Revisions.Count - 1, revision.IsDeleted, revision.IsSuppressed)).ToArray();
        var scans = page.Revisions.Select(r => MediaWikiScribuntoScanner.Scan(r.Wikitext, page.SourcePageId, r.SourceRevisionId)).ToArray();
        var dependencies = scans.SelectMany(s => s.Dependencies).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var dynamic = scans.SelectMany(s => s.UncertainDependencies).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var source = string.Join("\n", page.Revisions.Select(r => r.Wikitext));
        var mw = KnownApis.Where(api => source.Contains(api, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var cw = mw.Where(api => api.StartsWith("cw.", StringComparison.OrdinalIgnoreCase)).ToArray();
        var unsupported = UnsupportedApis.Where(api => source.Contains(api, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var sites = pages.SelectMany(p => p.Revisions.SelectMany(r => MediaWikiScribuntoScanner.Scan(r.Wikitext, p.SourcePageId, r.SourceRevisionId).Invocations
            .Where(i => string.Equals(i.ModuleTitle, MediaWikiScribuntoScanner.NormalizeModule(page.Title), StringComparison.OrdinalIgnoreCase))
            .Select(i => new MediaWikiModuleInvocationSite(p.SourcePageId, r.SourceRevisionId, i.Function ?? string.Empty, i.Offset, i.PositionalArguments, i.NamedArguments)))).ToArray();
        var normalizedTitle = Normalize(page.Title);
        var disposition = dynamic.Length > 0 ? "ContextualResolutionRequired" : cycles.Contains(normalizedTitle) ? "DependencyCycle" : dependencies.Any(d => !moduleNamesIn(pages).Contains(Normalize(d))) ? "DependencyAbsent" : "StaticDependenciesResolved";
        var capabilities = new List<string>();
        if (source.Contains("frame:getTitle", StringComparison.Ordinal)) capabilities.Add("frame.title-parent");
        if (source.Contains("mw.title", StringComparison.Ordinal)) capabilities.Add("mw.title");
        return new(page.Title, page.SourceNamespaceId, page.Revisions.LastOrDefault()?.ContentModel ?? string.Empty, revisions, dependencies, dynamic, mw, cw, unsupported, sites, capabilities, disposition);

        static HashSet<string> moduleNamesIn(IReadOnlyList<MediaWikiPage> values) => values.Where(p => p.SourceNamespaceId == "828").Select(p => Normalize(p.Title)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<MediaWikiModuleSelection> SelectRepresentatives(IReadOnlyList<MediaWikiModuleInventoryEntry> modules)
    {
        var result = new List<MediaWikiModuleSelection>();
        Add("simple-formatting", m => m.StaticDependencies.Count == 0 && m.UnsupportedApiReferences.Count == 0);
        Add("nested-dependency", m => m.StaticDependencies.Count > 0);
        Add("data-or-unicode", m => m.ReferencedMwApis.Any(a => a is "mw.text" or "mw.ustring" or "mw.loadData"));
        Add("unsupported-api", m => m.UnsupportedApiReferences.Count > 0);
        return result;
        void Add(string category, Func<MediaWikiModuleInventoryEntry, bool> predicate)
        {
            var match = modules.Where(predicate)
                .Where(m => result.All(selected => !string.Equals(selected.Title, m.Title, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(m => m.Title, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (match is not null) result.Add(new(match.Title, $"First deterministic module matching {category} coverage.", category));
        }
    }

    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    private static string Normalize(string title) => MediaWikiScribuntoScanner.NormalizeModule(title);

    private static IReadOnlySet<string> FindCycles(IReadOnlyDictionary<string, string[]> graph)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in graph.Keys)
        {
            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Walk(start, visiting, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
        return result;
        void Walk(string node, HashSet<string> visiting, HashSet<string> visited)
        {
            if (!visiting.Add(node)) { result.UnionWith(visiting); return; }
            foreach (var next in graph.GetValueOrDefault(node, []))
                if (!visited.Contains(next)) Walk(next, visiting, visited);
            visiting.Remove(node);
            visited.Add(node);
        }
    }
}
