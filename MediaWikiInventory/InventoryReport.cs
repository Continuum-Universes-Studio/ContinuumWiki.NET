using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MediaWikiInventory;

/// <summary>Read-only facts extracted from an offline MediaWiki XML export.</summary>
public sealed record InventoryReport
{
    public required string SourceFormat { get; init; }
    public required int PageCount { get; init; }
    public required int CurrentArticleBodyCount { get; init; }
    public required int RevisionCount { get; init; }
    public required int RevisionTimestampCount { get; init; }
    public required int NamespaceCount { get; init; }
    public required int RedirectCount { get; init; }
    public required int DistinctContributorCount { get; init; }
    public required int AnonymousContributorCount { get; init; }
    public required int TemplateDefinitionPageCount { get; init; }
    public required int TemplateTransclusionCount { get; init; }
    public required int TemplateParameterCount { get; init; }
    public required int ParserFunctionCount { get; init; }
    public required int LuaInvocationCount { get; init; }
    public required int ModulePageCount { get; init; }
    public required int ModuleInvocationCount { get; init; }
    public required int ModuleDependencyCount { get; init; }
    public required int ModuleCycleCount { get; init; }
    public required int ReferenceTagCount { get; init; }
    public required int CategoryLinkCount { get; init; }
    public required int FileLinkCount { get; init; }
    public required int FilePageCount { get; init; }
    public required int FileMetadataRecordCount { get; init; }
    public required int InternalLinkCount { get; init; }
    public required int ExternalLinkCount { get; init; }
    public required int MagicWordCount { get; init; }
    public required int LanguageLinkCount { get; init; }
    public required int LanguageVariantMarkupCount { get; init; }
    public required int ExtensionTagCount { get; init; }
    public required IReadOnlyList<string> Namespaces { get; init; }
    public required IReadOnlyList<string> ExtensionTags { get; init; }
    public required IReadOnlyList<string> PageTitles { get; init; }
    public required IReadOnlyList<string> SourcePageIds { get; init; }
    public required IReadOnlyList<string> SourceRevisionIds { get; init; }
    public required IReadOnlyList<string> ModuleTitles { get; init; }
    public required IReadOnlyList<string> ModuleDependencies { get; init; }
    public required IReadOnlyList<string> UncertainModuleDependencies { get; init; }

    private static readonly Regex Template = new(@"\{\{\s*(?!#)[^{}|]+", RegexOptions.Compiled);
    private static readonly Regex ParserFunction = new(@"\{\{\s*#", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Lua = new(@"\{\{\s*#invoke:", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TemplateParameter = new(@"\{\{[^{}]*\|[^{}]*=", RegexOptions.Compiled);
    private static readonly Regex Reference = new(@"<ref\b|<references\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Category = new(@"\[\[\s*Category:", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FileLink = new(@"\[\[\s*(File|Image):", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Internal = new(@"\[\[(?!Category:|File:|Image:)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex External = new(@"\bhttps?://", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MagicWord = new(@"__[^\s_]+__", RegexOptions.Compiled);
    private static readonly Regex LanguageVariant = new(@"-\{[^}]*\}-", RegexOptions.Compiled);
    private static readonly Regex Tag = new(@"<([a-z][a-z0-9-]*)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NamespaceDeclaration = new(@"\s+xmlns(?::[a-z0-9_-]+)?=""[^"" ]*""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static InventoryReport Read(string path)
    {
        if (!System.IO.File.Exists(path)) throw new FileNotFoundException("MediaWiki XML export was not found.", path);
        var document = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        var pages = document.Descendants().Where(e => e.Name.LocalName == "page").ToArray();
        var revisions = pages.SelectMany(p => p.Elements().Where(e => e.Name.LocalName == "revision")).ToArray();
        var texts = revisions.Select(TextOf).ToArray();
        var scans = texts.Select((text, index) => MediaWikiScribuntoScanner.Scan(text, ChildValue(revisions[index].Parent ?? revisions[index], "id"), ChildValue(revisions[index], "id"))).ToArray();
        var moduleDependencies = scans.SelectMany(scan => scan.Dependencies).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var uncertainModuleDependencies = scans.SelectMany(scan => scan.UncertainDependencies).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var moduleCycleCount = CountModuleCycles(pages);
        var latestTexts = pages.Select(p => p.Elements().Where(e => e.Name.LocalName == "revision").LastOrDefault())
            .Where(r => r is not null).Select(r => TextOf(r!)).ToArray();
        var pageIds = pages.Select(p => ChildValue(p, "id")).Where(x => x.Length > 0).ToArray();
        var pageTitles = pages.Select(p => ChildValue(p, "title")).Where(x => x.Length > 0).ToArray();
        var revisionIds = revisions.Select(r => ChildValue(r, "id")).Where(x => x.Length > 0).ToArray();
        var namespaceIds = pages.Select(p => ChildValue(p, "ns") is { Length: > 0 } ns ? ns : "0")
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var namespaceNames = document.Descendants().Where(e => e.Name.LocalName == "namespace")
            .Select(e => $"{e.Attribute("key")?.Value ?? ""}:{e.Value.Trim()}")
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var contributors = revisions.Select(ContributorOf).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        var anonymous = revisions.Select(ContributorOf).Count(x => x.StartsWith("ip:", StringComparison.Ordinal));
        var tags = texts.SelectMany(t => Tag.Matches(t).Select(m => m.Groups[1].Value.ToLowerInvariant()))
            .Where(t => t is not ("ref" or "references" or "br" or "nowiki" or "pre" or "includeonly" or "noinclude"))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        int Count(Regex regex, IEnumerable<string> values) => values.Sum(t => regex.Matches(t).Count);
        var redirectPages = pages.Count(p => p.Elements().Any(e => e.Name.LocalName == "redirect") ||
            TextOf(p.Elements().Where(e => e.Name.LocalName == "revision").LastOrDefault()).TrimStart().StartsWith("#redirect", StringComparison.OrdinalIgnoreCase));
        var languageLinks = document.Descendants().Count(e => e.Name.LocalName == "langlinks");
        var names = namespaceNames.Length > 0 ? namespaceNames : namespaceIds;
        return new InventoryReport
        {
            SourceFormat = "MediaWiki XML export", PageCount = pages.Length,
            CurrentArticleBodyCount = latestTexts.Count(t => t.Length > 0), RevisionCount = revisions.Length,
            RevisionTimestampCount = revisions.Count(r => ChildValue(r, "timestamp").Length > 0),
            NamespaceCount = namespaceIds.Length, RedirectCount = redirectPages,
            DistinctContributorCount = contributors.Length, AnonymousContributorCount = anonymous,
            TemplateDefinitionPageCount = pages.Count(p => ChildValue(p, "ns") == "10"),
            TemplateTransclusionCount = Count(Template, texts), TemplateParameterCount = Count(TemplateParameter, texts),
            ParserFunctionCount = Count(ParserFunction, texts), LuaInvocationCount = Count(Lua, texts),
            ModulePageCount = pages.Count(p => ChildValue(p, "ns") == "828"),
            ModuleInvocationCount = scans.Sum(scan => scan.Invocations.Count),
            ModuleDependencyCount = moduleDependencies.Length,
            ModuleCycleCount = moduleCycleCount,
            ReferenceTagCount = Count(Reference, texts), CategoryLinkCount = Count(Category, texts),
            FileLinkCount = Count(FileLink, texts), FilePageCount = pages.Count(p => ChildValue(p, "ns") == "6"),
            FileMetadataRecordCount = pages.Count(p => ChildValue(p, "ns") == "6" &&
                p.Descendants().Any(e => e.Name.LocalName is "sha1" or "size" or "width" or "height")),
            InternalLinkCount = Count(Internal, texts), ExternalLinkCount = Count(External, texts),
            MagicWordCount = Count(MagicWord, texts), LanguageLinkCount = languageLinks,
            LanguageVariantMarkupCount = Count(LanguageVariant, texts), ExtensionTagCount = tags.Length,
            Namespaces = names, ExtensionTags = tags, PageTitles = pageTitles,
            SourcePageIds = pageIds, SourceRevisionIds = revisionIds,
            ModuleTitles = pages.Where(p => ChildValue(p, "ns") == "828").Select(p => ChildValue(p, "title")).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            ModuleDependencies = moduleDependencies,
            UncertainModuleDependencies = uncertainModuleDependencies
        };
    }

    private static string ChildValue(XElement element, string name) =>
        element.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() ?? string.Empty;

    private static string TextOf(XElement? revision)
    {
        var text = revision?.Elements().FirstOrDefault(e => e.Name.LocalName == "text");
        return text is null ? string.Empty : NamespaceDeclaration.Replace(
            string.Concat(text.Nodes().Select(n => n.ToString(SaveOptions.DisableFormatting))), string.Empty);
    }

    private static string ContributorOf(XElement revision)
    {
        var contributor = revision.Elements().FirstOrDefault(e => e.Name.LocalName == "contributor");
        if (contributor is null) return string.Empty;
        var ip = ChildValue(contributor, "ip");
        return ip.Length > 0 ? $"ip:{ip}" : $"user:{ChildValue(contributor, "username")}";
    }

    private static int CountModuleCycles(IEnumerable<XElement> pages)
    {
        var graph = pages.Where(page => ChildValue(page, "ns") == "828")
            .ToDictionary(page => MediaWikiScribuntoScanner.NormalizeModule(ChildValue(page, "title")), page =>
                page.Elements().Where(e => e.Name.LocalName == "revision").SelectMany(revision => MediaWikiScribuntoScanner.Scan(TextOf(revision)).Dependencies.Select(MediaWikiScribuntoScanner.NormalizeModule)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
        var cycles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in graph.Keys) Walk(node, [], new HashSet<string>(StringComparer.OrdinalIgnoreCase), graph, cycles);
        return cycles.Count;
    }

    private static void Walk(string node, IReadOnlyList<string> path, HashSet<string> active, IReadOnlyDictionary<string, string[]> graph, HashSet<string> cycles)
    {
        if (!active.Add(node))
        {
            var cycle = path.SkipWhile(value => !string.Equals(value, node, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase);
            cycles.Add(string.Join('|', cycle));
            return;
        }
        foreach (var next in graph.GetValueOrDefault(node, [])) Walk(next, path.Append(node).ToArray(), active, graph, cycles);
        active.Remove(node);
    }
}
