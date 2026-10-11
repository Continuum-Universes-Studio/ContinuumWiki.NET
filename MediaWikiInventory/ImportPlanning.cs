using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MediaWikiInventory;

public enum PlanFindingSeverity { Informational, Warning, Blocking }
public enum ImportDisposition { Ready, ReadyWithWarnings, Blocked }
public enum MediaWikiImportPolicy { Strict, DisposableDiagnosticRendering }

public static class MediaWikiImportPolicyRules
{
    public static bool IsAllowedDiagnosticFinding(PlanFinding finding)
        => finding.Code == "UnsupportedExtensionTag"
           && (finding.Message.Contains("<templatedata>", StringComparison.OrdinalIgnoreCase)
               || finding.Message.Contains("<mermaid>", StringComparison.OrdinalIgnoreCase)
               || finding.Message.Contains("<templatestyles>", StringComparison.OrdinalIgnoreCase));
}

public sealed record PlanFinding(PlanFindingSeverity Severity, string Code, string Message,
    string? SourcePageId = null, string? SourceRevisionId = null);

public sealed record NamespaceMappingRule(
    string SourceNamespaceId,
    string TargetNamespace,
    IReadOnlyList<string> Aliases);

public sealed record NamespaceMappingConfiguration(
    string Version,
    IReadOnlyList<NamespaceMappingRule> Mappings)
{
    public static NamespaceMappingConfiguration Defaults => new("defaults-1", [
        new("0", "", ["Main", ""]), new("1", "Talk", ["Talk"]),
        new("2", "User", ["User"]), new("3", "User talk", ["User talk"]),
        new("4", "Project", ["Project"]), new("5", "Project talk", ["Project talk"]),
        new("6", "File", ["File", "Image"]), new("7", "File talk", ["File talk", "Image talk"]),
        new("8", "MediaWiki", ["MediaWiki"]), new("9", "MediaWiki talk", ["MediaWiki talk"]),
        new("10", "Template", ["Template"]), new("11", "Template talk", ["Template talk"]),
        new("12", "Help", ["Help"]), new("13", "Help talk", ["Help talk"]),
        new("14", "Category", ["Category"]), new("15", "Category talk", ["Category talk"]),
        new("828", "Module", ["Module"]), new("829", "Module talk", ["Module talk"])
    ]);

    public static NamespaceMappingConfiguration Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Defaults;
        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var value = JsonSerializer.Deserialize<NamespaceMappingConfiguration>(File.ReadAllText(path), options);
            if (value is null || value.Mappings is null) throw new InvalidDataException("Mapping configuration is empty.");
            return value;
        }
        catch (JsonException ex) { throw new InvalidDataException("Namespace mapping configuration is malformed.", ex); }
    }

    [JsonIgnore]
    public string Identity => Hashing.Sha256(Encoding.UTF8.GetBytes(CanonicalJson(this)));

    public NamespaceResolution Resolve(string sourceId, string? sourceName, string? canonicalName)
    {
        var exact = Mappings.Where(m => string.Equals(m.SourceNamespaceId, sourceId, StringComparison.Ordinal)).ToArray();
        if (exact.Length == 1) return new(true, exact[0].TargetNamespace, exact[0], null);
        if (exact.Length > 1) return new(false, null, null, "Multiple mapping rules use source namespace id " + sourceId + ".");
        var names = new[] { sourceName, canonicalName }.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        var byAlias = Mappings.Where(m => m.Aliases.Any(alias => names.Any(name =>
            string.Equals(alias, name, StringComparison.OrdinalIgnoreCase)))).ToArray();
        return byAlias.Length == 1
            ? new(true, byAlias[0].TargetNamespace, byAlias[0], null)
            : new(false, null, null, byAlias.Length > 1 ? "Namespace alias matches multiple mapping rules." : "No namespace mapping exists for source namespace " + sourceId + ".");
    }

    private static string CanonicalJson<T>(T value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = false });
}

public sealed record NamespaceResolution(bool IsMapped, string? TargetNamespace, NamespaceMappingRule? Rule, string? Error);

public sealed record SourceExportIdentity(string Sha256, long ByteLength, string? ExportVersion, string? SourceWikiIdentity = null);

public sealed record MediaWikiDependencyReport(
    IReadOnlyList<string> Templates,
    IReadOnlyList<string> ParserFunctions,
    IReadOnlyList<string> Modules,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> InternalLinks,
    IReadOnlyList<string> Files,
    int ReferenceTagCount,
    IReadOnlyList<string> ExtensionTags,
    IReadOnlyList<PlanFinding> Findings,
    IReadOnlyList<MediaWikiModuleInvocation>? ModuleInvocations = null,
    IReadOnlyList<string>? ModuleDependencies = null,
    IReadOnlyList<string>? UncertainModuleDependencies = null,
    IReadOnlyList<IReadOnlyList<string>>? ModuleCycles = null);

public sealed record PlannedContributor(
    MediaWikiContributorKind Kind,
    string? SourceId,
    string? DisplayName,
    string? IpAddress,
    string IdentityResolution);

public sealed record PlannedRevision(
    string SourceRevisionId,
    int RevisionOrdinal,
    string? ParentRevisionId,
    DateTimeOffset? Timestamp,
    string? Comment,
    PlannedContributor Contributor,
    string? ContentModel,
    string? ContentFormat,
    string? Sha1,
    bool IsMinor,
    bool IsDeleted,
    bool IsSuppressed,
    bool WikitextPresent,
    string Wikitext,
    string WikitextSha256,
    MediaWikiDependencyReport Dependencies,
    IReadOnlyList<PlanFinding> Findings);

public sealed record PlannedPage(
    string SourcePageId,
    string OriginalTitle,
    string SourceNamespaceId,
    string? SourceNamespaceName,
    string? TargetNamespace,
    string? ProposedTargetNavigation,
    bool IsRedirect,
    string? RedirectTargetSourceTitle,
    string? RedirectTargetNavigation,
    IReadOnlyList<PlannedRevision> Revisions,
    IReadOnlyList<PlanFinding> Findings,
    ImportDisposition Disposition);

public sealed record MediaWikiImportPlan(
    string PlannerVersion,
    string PlanSchemaVersion,
    SourceExportIdentity Source,
    string MappingConfigurationIdentity,
    NamespaceMappingConfiguration MappingConfiguration,
    int PlannedPageCount,
    int PlannedRevisionCount,
    int RedirectCount,
    IReadOnlyDictionary<string, int> ContributorDistribution,
    IReadOnlyDictionary<string, int> NamespaceDistribution,
    IReadOnlyDictionary<string, int> ContentModelDistribution,
    IReadOnlyDictionary<string, string> WikitextContentHashes,
    IReadOnlyList<PlannedPage> Pages,
    IReadOnlyList<PlanFinding> Findings,
    ImportDisposition OverallDisposition)
{
    public MediaWikiMediaPlan? Media { get; init; }
}

public static class MediaWikiImportPlanner
{
    public const string Version = "CW-M3-005A.1";
    private static readonly Regex Template = new(@"(?<!\{)\{\{\s*(?!#|\{)([^{}|\n]+)", RegexOptions.Compiled);
    private static readonly Regex ParserFunction = new(@"\{\{\s*#([\w:.-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Module = new(@"\{\{\s*#invoke:\s*([^|}\n]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Category = new(@"\[\[\s*Category\s*:\s*([^|\]]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex File = new(@"\[\[\s*(?:File|Image)\s*:\s*([^|\]]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Link = new(@"\[\[\s*([^|\]]+)", RegexOptions.Compiled);
    private static readonly Regex Reference = new(@"<(?:ref|references)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Tag = new(@"<([a-z][a-z0-9-]*)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Redirect = new(@"^\s*#redirect\s*\[\[([^\]]+)\]\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly HashSet<string> NonExtensionTags = new(StringComparer.OrdinalIgnoreCase)
        { "ref", "references", "br", "nowiki", "pre", "includeonly", "noinclude", "onlyinclude", "div", "span", "table", "tr", "td", "th", "p", "gallery" };
    private static readonly HashSet<string> PortableInfoboxSchemaTags = new(StringComparer.OrdinalIgnoreCase)
        { "infobox", "title", "default", "group", "header", "data", "audio", "panel", "section", "label", "image", "caption", "format" };
    private static readonly HashSet<string> UnsupportedCustomParserFunctions = new(StringComparer.OrdinalIgnoreCase)
        { "cuinfobox" };

    public static MediaWikiImportPlan Create(string inputPath, NamespaceMappingConfiguration mapping)
        => Create(inputPath, mapping, null, null);

    public static MediaWikiImportPlan Create(string inputPath, NamespaceMappingConfiguration mapping, string? mediaManifestPath, string? mediaRoot)
    {
        var first = ReadPass(inputPath, mapping, collectPageNames: true);
        var source = new SourceExportIdentity(Hashing.Sha256File(inputPath), new FileInfo(inputPath).Length, null, first.SourceWikiIdentity);
        var second = ReadPass(inputPath, mapping, collectPageNames: false, knownPages: first.KnownPages, sourceIdentity: source);
        var pages = second.Pages.OrderBy(p => p.SourcePageId, StringComparer.Ordinal).ToArray();
        var findings = second.Findings.OrderBy(f => f.Severity).ThenBy(f => f.Code, StringComparer.Ordinal).ThenBy(f => f.SourcePageId, StringComparer.Ordinal).ToList();
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var contributors = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var namespaces = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var models = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var revisions = 0;
        foreach (var page in pages)
        {
            namespaces[page.SourceNamespaceId] = namespaces.TryGetValue(page.SourceNamespaceId, out var n) ? n + 1 : 1;
            foreach (var revision in page.Revisions)
            {
                revisions++;
                hashes[$"{page.SourcePageId}/{revision.SourceRevisionId}"] = revision.WikitextSha256;
                var key = revision.Contributor.Kind.ToString();
                contributors[key] = contributors.TryGetValue(key, out var c) ? c + 1 : 1;
                var model = string.IsNullOrEmpty(revision.ContentModel) ? "UNSPECIFIED" : revision.ContentModel;
                models[model] = models.TryGetValue(model, out var m) ? m + 1 : 1;
            }
        }
        MediaWikiMediaPlan? media = null;
        if (!string.IsNullOrWhiteSpace(mediaManifestPath) || !string.IsNullOrWhiteSpace(mediaRoot))
        {
            if (string.IsNullOrWhiteSpace(mediaManifestPath) || string.IsNullOrWhiteSpace(mediaRoot))
                findings.Add(new(PlanFindingSeverity.Blocking, "MediaOptionsIncomplete", "Both a media manifest and an offline media root are required when media planning is enabled."));
            else
            {
                media = MediaWikiMediaPlanner.Create(mediaManifestPath, mediaRoot, pages);
                findings.AddRange(media.Findings);
            }
        }
        var disposition = DetermineDisposition(findings);
        return new(Version, "1", source with { ExportVersion = second.ExportVersion }, mapping.Identity, mapping,
            pages.Length, revisions, pages.Count(p => p.RedirectTargetSourceTitle is not null), contributors, namespaces, models,
            hashes, pages, findings, disposition) { Media = media };
    }

    private static PassResult ReadPass(string path, NamespaceMappingConfiguration mapping, bool collectPageNames,
        IReadOnlyDictionary<string, SourcePageKey>? knownPages = null, SourceExportIdentity? sourceIdentity = null)
    {
        using var stream = new MediaWikiXmlReader().Open(path);
        var known = collectPageNames ? new Dictionary<string, SourcePageKey>(StringComparer.Ordinal) : new Dictionary<string, SourcePageKey>(knownPages!, StringComparer.Ordinal);
        var pages = new List<PlannedPage>();
        var findings = new List<PlanFinding>();
        foreach (var page in stream.ReadPages())
        {
            var namespaceName = stream.Header.Namespaces.FirstOrDefault(n => n.SourceNamespaceId == page.SourceNamespaceId)?.Name;
            if (collectPageNames) known[page.SourcePageId] = new(page.SourceNamespaceId, page.Title);
            if (collectPageNames)
            {
                if (!known.Values.GroupBy(v => SourceKey(v.NamespaceId, v.Title), StringComparer.Ordinal).All(g => g.Count() == 1))
                    findings.Add(new(PlanFindingSeverity.Blocking, "DuplicateSourcePageTitle", $"Source title collision detected for {page.Title}.", page.SourcePageId));
                continue;
            }
            var resolution = mapping.Resolve(page.SourceNamespaceId, namespaceName, stream.Header.Namespaces.FirstOrDefault(n => n.SourceNamespaceId == page.SourceNamespaceId)?.CanonicalName);
            var pageFindings = page.Warnings.Select(w => new PlanFinding(PlanFindingSeverity.Warning, w.Code, w.Message, page.SourcePageId)).ToList();
            pageFindings.AddRange(page.UnsupportedElements.Select(e => new PlanFinding(PlanFindingSeverity.Blocking, "UnsupportedXmlElement", $"Preserved unsupported XML element <{e.Name}>.", page.SourcePageId)));
            if (!resolution.IsMapped) pageFindings.Add(new(PlanFindingSeverity.Blocking, "UnknownNamespace", resolution.Error ?? "Namespace mapping failed.", page.SourcePageId));
            var targetNavigation = resolution.IsMapped ? TargetNavigation(resolution.TargetNamespace!, page.Title) : null;
            var plannedRevisions = new List<PlannedRevision>();
            foreach (var (revision, ordinal) in page.Revisions.Select((value, index) => (value, index + 1)))
            {
                var revisionFindings = revision.Warnings.Select(w => new PlanFinding(PlanFindingSeverity.Warning, w.Code, w.Message, page.SourcePageId, revision.SourceRevisionId)).ToList();
                revisionFindings.AddRange(revision.UnsupportedElements.Select(e => new PlanFinding(PlanFindingSeverity.Blocking, "UnsupportedXmlElement", $"Preserved unsupported XML element <{e.Name}>.", page.SourcePageId, revision.SourceRevisionId)));
                var moduleContentModel = page.SourceNamespaceId == "828" && revision.ContentModel is not null
                    && (string.Equals(revision.ContentModel, "Scribunto", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(revision.ContentModel, "Json", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(revision.ContentModel)
                    && !string.Equals(revision.ContentModel, "wikitext", StringComparison.OrdinalIgnoreCase)
                    && !moduleContentModel)
                    revisionFindings.Add(new(PlanFindingSeverity.Blocking, "UnsupportedContentModel", $"Content model '{revision.ContentModel}' is not the supported Wikitext model.", page.SourcePageId, revision.SourceRevisionId));
                if (revision.Contributor.Kind == MediaWikiContributorKind.Missing)
                    revisionFindings.Add(new(PlanFindingSeverity.Warning, "UnresolvedContributor", "Contributor metadata is missing; no login identity will be created.", page.SourcePageId, revision.SourceRevisionId));
                var dependencies = moduleContentModel
                    ? AnalyzeModule(revision.Wikitext, page.SourcePageId, revision.SourceRevisionId)
                    : Analyze(revision.Wikitext, page.SourcePageId, revision.SourceRevisionId);
                revisionFindings.AddRange(dependencies.Findings);
                findings.AddRange(revisionFindings);
                plannedRevisions.Add(new(revision.SourceRevisionId, ordinal, revision.ParentRevisionId, revision.Timestamp, revision.Comment,
                    new(revision.Contributor.Kind, revision.Contributor.SourceId, revision.Contributor.DisplayName, revision.Contributor.IpAddress, "UnresolvedProvenance"),
                    revision.ContentModel, revision.ContentFormat, revision.Sha1, revision.IsMinor, revision.IsDeleted, revision.IsSuppressed, revision.WikitextPresent, revision.Wikitext,
                    Hashing.Sha256(Encoding.UTF8.GetBytes(revision.Wikitext)), dependencies, revisionFindings));
            }
            pageFindings.AddRange(plannedRevisions.SelectMany(r => r.Findings));
            var redirectTarget = page.RedirectTarget ?? ExtractRedirectTarget(plannedRevisions.FirstOrDefault()?.Wikitext);
            if (page.IsRedirect && string.IsNullOrWhiteSpace(redirectTarget))
                pageFindings.Add(new(PlanFindingSeverity.Blocking, "MissingRedirectTarget", "Redirect page has no target title.", page.SourcePageId));
            var redirectResolved = true;
            string? redirectNavigation = null;
            if (redirectTarget is not null)
                redirectNavigation = ResolveRedirectNavigation(redirectTarget, page.SourceNamespaceId, stream.Header, mapping, known, out redirectResolved);
            if (redirectTarget is not null && !redirectResolved)
                pageFindings.Add(new(PlanFindingSeverity.Blocking, "InvalidRedirectTarget", $"Redirect target '{redirectTarget}' does not resolve to an exported source page.", page.SourcePageId));
            pages.Add(new(page.SourcePageId, page.Title, page.SourceNamespaceId, namespaceName, resolution.TargetNamespace, targetNavigation, page.IsRedirect,
                redirectTarget, redirectNavigation, plannedRevisions,
                pageFindings, DetermineDisposition(pageFindings)));
            findings.AddRange(pageFindings);
        }
        ResolveModuleDependencyClosure(pages, findings);
        ResolveMissingDependencies(pages, known, findings);
        ResolveModuleCycles(pages, findings);
        DetectSourceIdentityConflicts(pages, findings);
        DetectNavigationCollisions(pages, findings);
        return new(stream.Header.ExportVersion, SourceWikiIdentity(stream.Header), known, pages, findings);
    }

    private static void ResolveMissingDependencies(List<PlannedPage> pages, IReadOnlyDictionary<string, SourcePageKey> known, List<PlanFinding> findings)
    {
        var templates = known.Values.Where(v => v.NamespaceId == "10").Select(v => DependencyKey(v.Title)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var modules = known.Values.Where(v => v.NamespaceId == "828").Select(v => DependencyKey(v.Title)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            var pageFindings = page.Findings.ToList();
            foreach (var revision in page.Revisions)
            {
                foreach (var template in revision.Dependencies.Templates.Where(t => !templates.Contains(DependencyKey(t))))
                    pageFindings.Add(new(PlanFindingSeverity.Blocking, "MissingTemplateDependency", $"Template dependency '{template}' is not present in the export.", page.SourcePageId, revision.SourceRevisionId));
                foreach (var module in revision.Dependencies.Modules.Concat(revision.Dependencies.ModuleDependencies ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Where(m => !modules.Contains(DependencyKey(m))))
                    pageFindings.Add(new(PlanFindingSeverity.Blocking, "MissingModuleDependency", $"Module dependency '{module}' is not present in the export.", page.SourcePageId, revision.SourceRevisionId));
            }
            pages[i] = page with { Findings = pageFindings, Disposition = DetermineDisposition(pageFindings) };
            foreach (var finding in pageFindings)
                if (!findings.Contains(finding)) findings.Add(finding);
        }
    }

    private static void ResolveModuleDependencyClosure(List<PlannedPage> pages, List<PlanFinding> findings)
    {
        var modulePages = BuildDependencyIndex(pages.Where(page => page.SourceNamespaceId == "828"), "Module", findings);
        var templates = BuildDependencyIndex(pages.Where(page => page.SourceNamespaceId == "10"), "Template", findings);
        for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            var page = pages[pageIndex];
            var revisions = page.Revisions.Select(revision =>
            {
                var direct = new HashSet<string>(revision.Dependencies.Modules.Concat(revision.Dependencies.ModuleDependencies ?? []), StringComparer.OrdinalIgnoreCase);
                foreach (var template in revision.Dependencies.Templates.Select(DependencyKey))
                    if (templates.TryGetValue(template, out var templatePage))
                        foreach (var module in templatePage.Revisions.SelectMany(value => value.Dependencies.Modules.Concat(value.Dependencies.ModuleDependencies ?? []))) direct.Add(module);
                var closure = new HashSet<string>(direct, StringComparer.OrdinalIgnoreCase);
                var pending = new Queue<string>(direct);
                while (pending.Count > 0)
                {
                    var module = DependencyKey(pending.Dequeue());
                    if (!modulePages.TryGetValue(module, out var modulePage)) continue;
                    foreach (var dependency in modulePage.Revisions.SelectMany(value => value.Dependencies.Modules.Concat(value.Dependencies.ModuleDependencies ?? [])))
                        if (closure.Add(dependency)) pending.Enqueue(dependency);
                }
                return revision with { Dependencies = revision.Dependencies with { Modules = closure.Order(StringComparer.OrdinalIgnoreCase).ToArray(), ModuleDependencies = closure.Order(StringComparer.OrdinalIgnoreCase).ToArray() } };
            }).ToArray();
            pages[pageIndex] = page with { Revisions = revisions };
        }
    }

    private static Dictionary<string, PlannedPage> BuildDependencyIndex(
        IEnumerable<PlannedPage> pages,
        string dependencyKind,
        List<PlanFinding> findings)
    {
        var index = new Dictionary<string, PlannedPage>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in pages.GroupBy(page => DependencyKey(page.OriginalTitle), StringComparer.OrdinalIgnoreCase))
        {
            var candidates = group.OrderBy(page => page.SourcePageId, StringComparer.Ordinal).ToArray();
            var selected = candidates[0];
            index[group.Key] = selected;
            if (candidates.Length <= 1) continue;

            var titles = string.Join(", ", candidates.Select(page => $"'{page.OriginalTitle}' (page {page.SourcePageId})"));
            var finding = new PlanFinding(
                PlanFindingSeverity.Blocking,
                "DependencyKeyCollision",
                $"{dependencyKind} dependency key '{group.Key}' resolves to multiple source pages: {titles}.");
            findings.Add(finding);
        }
        return index;
    }

    private static void ResolveModuleCycles(List<PlannedPage> pages, List<PlanFinding> findings)
    {
        var modulePages = pages.Where(page => page.SourceNamespaceId == "828")
            .ToDictionary(page => DependencyKey(page.OriginalTitle), StringComparer.OrdinalIgnoreCase);
        var graph = modulePages.ToDictionary(pair => pair.Key, pair => pair.Value.Revisions.SelectMany(value => value.Dependencies.ModuleDependencies ?? value.Dependencies.Modules).Select(DependencyKey).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
        var cycles = new List<string[]>();
        foreach (var node in graph.Keys)
            FindCycles(node, graph, [], [], cycles);
        var normalizedCycles = cycles.Select(value => value.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray()).DistinctBy(value => string.Join('|', value), StringComparer.OrdinalIgnoreCase).ToArray();
        for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            var page = pages[pageIndex];
            if (page.SourceNamespaceId != "828") continue;
            var pageName = DependencyKey(page.OriginalTitle);
            var pageCycles = normalizedCycles.Where(cycle => cycle.Contains(pageName, StringComparer.OrdinalIgnoreCase)).Cast<IReadOnlyList<string>>().ToArray();
            if (pageCycles.Length > 0)
                pages[pageIndex] = page with { Revisions = page.Revisions.Select(revision => revision with { Dependencies = revision.Dependencies with { ModuleCycles = pageCycles } }).ToArray() };
        }
        foreach (var cycle in normalizedCycles)
        {
            foreach (var module in cycle)
                if (modulePages.TryGetValue(module, out var page))
                    foreach (var revision in page.Revisions)
                        findings.Add(new(PlanFindingSeverity.Warning, "CyclicModuleDependency", $"Module dependency cycle detected: {string.Join(" -> ", cycle)}.", page.SourcePageId, revision.SourceRevisionId));
        }
    }

    private static void FindCycles(string node, IReadOnlyDictionary<string, string[]> graph, IReadOnlyList<string> path, HashSet<string> visited, List<string[]> cycles)
    {
        if (path.Contains(node, StringComparer.OrdinalIgnoreCase)) { cycles.Add(path.SkipWhile(value => !string.Equals(value, node, StringComparison.OrdinalIgnoreCase)).Append(node).ToArray()); return; }
        if (!visited.Add(node)) return;
        foreach (var next in graph.GetValueOrDefault(node, [])) FindCycles(next, graph, path.Append(node).ToArray(), visited, cycles);
        visited.Remove(node);
    }

    private static void DetectSourceIdentityConflicts(List<PlannedPage> pages, List<PlanFinding> findings)
    {
        foreach (var group in pages.GroupBy(p => p.SourcePageId, StringComparer.Ordinal).Where(g => g.Count() > 1))
            foreach (var page in group) AddPageFinding(pages, page, new(PlanFindingSeverity.Blocking, "DuplicateSourcePageId", $"Source page id {page.SourcePageId} is duplicated.", page.SourcePageId), findings);
        foreach (var group in pages.GroupBy(p => SourceKey(p.SourceNamespaceId, p.OriginalTitle), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            foreach (var page in group) AddPageFinding(pages, page, new(PlanFindingSeverity.Blocking, "DuplicateSourcePageTitle", $"Source title '{page.OriginalTitle}' is duplicated within source namespace {page.SourceNamespaceId}.", page.SourcePageId), findings);
        foreach (var group in pages.SelectMany(p => p.Revisions.Select(r => (Page: p, Revision: r))).GroupBy(x => x.Revision.SourceRevisionId, StringComparer.Ordinal).Where(g => g.Count() > 1))
            foreach (var item in group) AddPageFinding(pages, item.Page, new(PlanFindingSeverity.Blocking, "DuplicateSourceRevisionId", $"Source revision id {item.Revision.SourceRevisionId} is duplicated.", item.Page.SourcePageId, item.Revision.SourceRevisionId), findings);
    }

    private static void DetectNavigationCollisions(List<PlannedPage> pages, List<PlanFinding> findings)
    {
        foreach (var group in pages.Where(p => p.ProposedTargetNavigation is not null).GroupBy(p => NavigationKey(p.ProposedTargetNavigation!), StringComparer.Ordinal))
            if (group.Count() > 1)
                foreach (var page in group)
                    AddPageFinding(pages, page, new(PlanFindingSeverity.Blocking, "TargetNavigationCollision", $"Target navigation '{page.ProposedTargetNavigation}' collides case-insensitively with another source page.", page.SourcePageId), findings);
    }

    private static void AddPageFinding(List<PlannedPage> pages, PlannedPage page, PlanFinding finding, List<PlanFinding> findings)
    {
        var index = pages.FindIndex(p => ReferenceEquals(p, page));
        if (index < 0) index = pages.FindIndex(p => p.SourcePageId == page.SourcePageId && p.OriginalTitle == page.OriginalTitle);
        if (index >= 0)
        {
            var updated = pages[index];
            pages[index] = updated with { Findings = updated.Findings.Append(finding).ToArray(), Disposition = ImportDisposition.Blocked };
        }
        findings.Add(finding);
    }

    private static string? ResolveRedirectNavigation(string target, string sourceNamespaceId, MediaWikiImportHeader header,
        NamespaceMappingConfiguration mapping, IReadOnlyDictionary<string, SourcePageKey> known, out bool resolved)
    {
        var targetNamespaceId = sourceNamespaceId;
        var targetTitle = target;
        var separator = target.IndexOf(':');
        if (separator > 0)
        {
            var prefix = target[..separator];
            var targetNamespace = header.Namespaces.FirstOrDefault(n => string.Equals(n.Name, prefix, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(n.CanonicalName, prefix, StringComparison.OrdinalIgnoreCase));
            if (targetNamespace is not null)
            {
                targetNamespaceId = targetNamespace.SourceNamespaceId;
                targetTitle = target[(separator + 1)..];
            }
        }
        resolved = known.Values.Any(v => string.Equals(SourceKey(v.NamespaceId, v.Title), SourceKey(targetNamespaceId, targetTitle), StringComparison.OrdinalIgnoreCase));
        if (!resolved) return null;
        var name = header.Namespaces.FirstOrDefault(n => n.SourceNamespaceId == targetNamespaceId);
        var targetMapping = mapping.Resolve(targetNamespaceId, name?.Name, name?.CanonicalName);
        return targetMapping.IsMapped ? TargetNavigation(targetMapping.TargetNamespace!, targetTitle) : null;
    }

    private static MediaWikiDependencyReport Analyze(string text, string pageId, string revisionId)
    {
        var templates = Template.Matches(text).Select(m => m.Groups[1].Value.Trim()).Where(x => x.Length > 0 && x != ":" && !IsSupportedMagicWord(x)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var parserFunctions = ParserFunction.Matches(text).Select(m => m.Groups[1].Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var scribunto = MediaWikiScribuntoScanner.Scan(text, pageId, revisionId);
        var modules = scribunto.Dependencies.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var categories = Category.Matches(text).Select(m => m.Groups[1].Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var files = File.Matches(text).Select(m => m.Groups[1].Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var links = Link.Matches(text).Select(m => m.Groups[1].Value.Trim()).Where(x => !x.StartsWith("Category:", StringComparison.OrdinalIgnoreCase) && !x.StartsWith("File:", StringComparison.OrdinalIgnoreCase) && !x.StartsWith("Image:", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var extensions = Tag.Matches(text)
            .Where(match => !NonExtensionTags.Contains(match.Groups[1].Value)
                && !IsPortableInfoboxSchemaTag(text, match.Index, match.Groups[1].Value))
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var findings = new List<PlanFinding> { new(PlanFindingSeverity.Warning, "HeuristicDependencyScan", "Dependency extraction uses bounded lexical scanning, not a complete MediaWiki parser; results require compatibility review.", pageId, revisionId) };
        findings.AddRange(scribunto.Findings);
        findings.AddRange(parserFunctions.Where(function => UnsupportedCustomParserFunctions.Any(custom =>
                function.Equals(custom, StringComparison.OrdinalIgnoreCase)
                || function.StartsWith(custom + ":", StringComparison.OrdinalIgnoreCase)))
            .Select(function => new PlanFinding(PlanFindingSeverity.Blocking, "UnsupportedCustomParserFunction",
                $"Custom parser function '#{function}' is provided by an external MediaWiki extension and is not executed by ContinuumWiki.", pageId, revisionId)));
        findings.AddRange(extensions.Select(tag => new PlanFinding(PlanFindingSeverity.Blocking, "UnsupportedExtensionTag", $"Extension or unknown tag <{tag}> was found and is preserved but not executable.", pageId, revisionId)));
        return new(templates, parserFunctions, modules, categories, links, files, Reference.Matches(text).Count, extensions, findings,
            scribunto.Invocations, scribunto.Dependencies, scribunto.UncertainDependencies, []);
    }

    private static MediaWikiDependencyReport AnalyzeModule(string text, string pageId, string revisionId)
    {
        var scan = MediaWikiScribuntoScanner.Scan(text, pageId, revisionId);
        var findings = new List<PlanFinding> { new(PlanFindingSeverity.Warning, "HeuristicLuaDependencyScan", "Lua dependency extraction uses bounded lexical scanning; runtime loading remains authoritative.", pageId, revisionId) };
        findings.AddRange(scan.Findings);
        return new([], [], scan.Dependencies, [], [], [], 0, [], findings,
            scan.Invocations, scan.Dependencies, scan.UncertainDependencies, []);
    }

    private static string? ExtractRedirectTarget(string? text) => text is null ? null : Redirect.Match(text) is { Success: true } match ? match.Groups[1].Value.Trim() : null;
    private static bool IsPortableInfoboxSchemaTag(string text, int offset, string tag)
    {
        if (!PortableInfoboxSchemaTags.Contains(tag)) return false;
        if (tag.Equals("infobox", StringComparison.OrdinalIgnoreCase)) return true;
        var open = text.LastIndexOf("<infobox", offset, StringComparison.OrdinalIgnoreCase);
        var close = text.LastIndexOf("</infobox", offset, StringComparison.OrdinalIgnoreCase);
        return open >= 0 && open > close;
    }
    private static bool IsSupportedMagicWord(string value)
    {
        var name = value.Split(':', 2)[0].Trim().ToUpperInvariant();
        return name is "PAGENAME" or "PAGENAMEE" or "FULLPAGENAME" or "FULLPAGENAMEE" or "BASEPAGENAME" or "BASEPAGENAMEE" or "SUBPAGENAME" or "SUBPAGENAMEE" or "ROOTPAGENAME" or "ROOTPAGENAMEE" or "NAMESPACE" or "NAMESPACENUMBER" or "TALKPAGENAME" or "TALKPAGENAMEE" or "SUBJECTPAGENAME" or "SUBJECTPAGENAMEE" or "SITENAME" or "CONTENTLANGUAGE" or "REVISIONID" or "REVISIONTIMESTAMP" or "REVISIONYEAR" or "REVISIONMONTH" or "REVISIONDAY" or "CURRENTYEAR" or "CURRENTMONTH" or "CURRENTDAY" or "CURRENTDAY2" or "CURRENTTIME" or "CURRENTTIMESTAMP" or "LOCALYEAR" or "LOCALMONTH" or "LOCALDAY" or "LOCALDAY2" or "LOCALTIME" or "LOCALTIMESTAMP" or "LC" or "UC" or "LCFIRST" or "UCFIRST" or "PADLEFT" or "PADRIGHT" or "FORMATNUM" or "FULLURL" or "LOCALURL";
    }
    private static string SourceKey(string namespaceId, string title) => namespaceId + "\u001f" + title.Normalize(NormalizationForm.FormC);
    private static string DependencyKey(string title) => title.StartsWith("Template:", StringComparison.OrdinalIgnoreCase) || title.StartsWith("Module:", StringComparison.OrdinalIgnoreCase) ? title[(title.IndexOf(':') + 1)..].Trim() : title.Trim();
    private static string TargetNavigation(string targetNamespace, string title) => string.IsNullOrEmpty(targetNamespace) ? title : targetNamespace + "::" + title;
    private static string NavigationKey(string navigation) => navigation.Normalize(NormalizationForm.FormC).ToUpperInvariant();
    private static ImportDisposition DetermineDisposition(IEnumerable<PlanFinding> findings) => findings.Any(f => f.Severity == PlanFindingSeverity.Blocking) ? ImportDisposition.Blocked : findings.Any(f => f.Severity == PlanFindingSeverity.Warning) ? ImportDisposition.ReadyWithWarnings : ImportDisposition.Ready;

    private sealed record SourcePageKey(string NamespaceId, string Title);
    private static string SourceWikiIdentity(MediaWikiImportHeader header)
        => string.Join("|", new[] { header.SiteName, header.BaseUrl, header.Generator }.Select(value => value?.Trim() ?? string.Empty));

    private sealed record PassResult(string? ExportVersion, string SourceWikiIdentity, IReadOnlyDictionary<string, SourcePageKey> KnownPages, IReadOnlyList<PlannedPage> Pages, IReadOnlyList<PlanFinding> Findings);
}

public static class ImportPlanValidator
{
    public static IReadOnlyList<PlanFinding> Validate(MediaWikiImportPlan plan, string inputPath, NamespaceMappingConfiguration mapping, string? mediaManifestPath = null, string? mediaRoot = null)
    {
        var findings = new List<PlanFinding>();
        var inputHash = Hashing.Sha256File(inputPath);
        var inputLength = new FileInfo(inputPath).Length;
        if (!string.Equals(plan.Source.Sha256, inputHash, StringComparison.OrdinalIgnoreCase)) findings.Add(new(PlanFindingSeverity.Blocking, "InputChecksumMismatch", "The input export checksum does not match the plan."));
        if (plan.Source.ByteLength != inputLength) findings.Add(new(PlanFindingSeverity.Blocking, "InputLengthMismatch", "The input export byte length does not match the plan."));
        if (!string.Equals(plan.MappingConfigurationIdentity, mapping.Identity, StringComparison.OrdinalIgnoreCase)) findings.Add(new(PlanFindingSeverity.Blocking, "MappingChecksumMismatch", "The mapping configuration checksum does not match the plan."));
        if (!string.Equals(plan.PlannerVersion, MediaWikiImportPlanner.Version, StringComparison.Ordinal)) findings.Add(new(PlanFindingSeverity.Blocking, "PlannerVersionMismatch", "The plan was produced by a different planner version."));
        if (plan.PlannedPageCount != plan.Pages.Count) findings.Add(new(PlanFindingSeverity.Blocking, "PageCountMismatch", "The plan page count does not match its page records."));
        if (plan.PlannedRevisionCount != plan.Pages.Sum(p => p.Revisions.Count)) findings.Add(new(PlanFindingSeverity.Blocking, "RevisionCountMismatch", "The plan revision count does not match its revision records."));
        if (plan.Media is not null)
        {
            if (string.IsNullOrWhiteSpace(mediaManifestPath) || string.IsNullOrWhiteSpace(mediaRoot)) findings.Add(new(PlanFindingSeverity.Blocking, "MediaOptionsMissing", "A media-enabled plan requires both --media-manifest and --media-root."));
            else
            {
                try
                {
                    var actual = MediaWikiMediaManifestReader.ReadAndValidate(mediaManifestPath);
                    if (!string.Equals(actual.ManifestSha256, plan.Media.ManifestSha256, StringComparison.OrdinalIgnoreCase)) findings.Add(new(PlanFindingSeverity.Blocking, "MediaManifestChecksumMismatch", "The media manifest checksum does not match the plan."));
                    if (actual.ManifestByteLength != plan.Media.ManifestByteLength) findings.Add(new(PlanFindingSeverity.Blocking, "MediaManifestLengthMismatch", "The media manifest byte length does not match the plan."));
                    if (!string.Equals(actual.Manifest.SourceWikiIdentity, plan.Source.SourceWikiIdentity, StringComparison.Ordinal)) findings.Add(new(PlanFindingSeverity.Blocking, "MediaSourceWikiIdentityMismatch", "The media manifest source wiki identity does not match the page export."));
                    findings.AddRange(actual.Findings);
                    var actualByPage = actual.Manifest.Entries.ToDictionary(entry => entry.SourceFilePageId, StringComparer.Ordinal);
                    if (actualByPage.Count != plan.Media.Entries.Count) findings.Add(new(PlanFindingSeverity.Blocking, "MediaPlanEntryCountMismatch", "The media plan entry count does not match the manifest."));
                    foreach (var plannedMedia in plan.Media.Entries)
                    {
                        if (!actualByPage.TryGetValue(plannedMedia.SourceFilePageId, out var actualMedia)
                            || !string.Equals(plannedMedia.OriginalFileName, actualMedia.OriginalFileName, StringComparison.Ordinal)
                            || plannedMedia.Revisions.Count != actualMedia.Revisions.Count
                            || plannedMedia.Revisions.Zip(actualMedia.Revisions).Any(pair => !string.Equals(pair.First.SourceMediaRevisionId, pair.Second.SourceMediaRevisionId, StringComparison.Ordinal) || !string.Equals(pair.First.Sha256, pair.Second.Sha256, StringComparison.OrdinalIgnoreCase) || !string.Equals(pair.First.RelativeBinaryPath, pair.Second.RelativeBinaryPath, StringComparison.Ordinal)))
                            findings.Add(new(PlanFindingSeverity.Blocking, "MediaPlanEntryMismatch", $"Media plan entry '{plannedMedia.SourceFilePageId}' does not match the manifest."));
                    }
                    var resolver = new MediaWikiMediaBinaryResolver(mediaRoot);
                    foreach (var entry in actual.Manifest.Entries)
                        foreach (var revision in entry.Revisions)
                            try { resolver.ReadVerifiedAsync(revision.RelativeBinaryPath, revision.Size, revision.Sha256, declaredMimeType: revision.MimeType).GetAwaiter().GetResult(); }
                            catch (Exception exception) { findings.Add(new(PlanFindingSeverity.Blocking, "MediaBinaryInvalid", exception.Message, entry.SourceFilePageId, revision.SourceMediaRevisionId)); }
                }
                catch (Exception exception) { findings.Add(new(PlanFindingSeverity.Blocking, "MediaManifestInvalid", exception.Message)); }
            }
        }
        var pageIds = new HashSet<string>(StringComparer.Ordinal);
        var revisionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in plan.Pages)
        {
            if (page.SourcePageId.Length == 0 || !pageIds.Add(page.SourcePageId)) findings.Add(new(PlanFindingSeverity.Blocking, "InvalidSourcePageIdentity", "The plan contains a missing or duplicate source page id.", page.SourcePageId));
            for (var i = 0; i < page.Revisions.Count; i++)
            {
                var revision = page.Revisions[i];
                if (revision.SourceRevisionId.Length == 0 || !revisionIds.Add(revision.SourceRevisionId)) findings.Add(new(PlanFindingSeverity.Blocking, "InvalidSourceRevisionIdentity", "The plan contains a missing or duplicate source revision id.", page.SourcePageId, revision.SourceRevisionId));
                if (revision.RevisionOrdinal != i + 1) findings.Add(new(PlanFindingSeverity.Blocking, "RevisionOrderMismatch", "Revision ordinals are not contiguous and ordered.", page.SourcePageId, revision.SourceRevisionId));
                if (!string.Equals(revision.WikitextSha256, Hashing.Sha256(Encoding.UTF8.GetBytes(revision.Wikitext)), StringComparison.OrdinalIgnoreCase)) findings.Add(new(PlanFindingSeverity.Blocking, "WikitextHashMismatch", "A revision Wikitext hash does not match its preserved content.", page.SourcePageId, revision.SourceRevisionId));
            }
        }
        if (plan.OverallDisposition == ImportDisposition.Ready && plan.Findings.Any(f => f.Severity == PlanFindingSeverity.Blocking)) findings.Add(new(PlanFindingSeverity.Blocking, "DispositionMismatch", "A plan with blocking findings cannot be marked Ready."));
        return findings;
    }
}

internal static class Hashing
{
    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static string Sha256File(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
}
