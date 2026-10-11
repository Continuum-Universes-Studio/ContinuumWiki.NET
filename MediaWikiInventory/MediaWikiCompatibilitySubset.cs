using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace MediaWikiInventory;

public static class MediaWikiCompatibilitySubset
{
    private const int MaxPasses = 32;
    private const int MaxPages = 2000;
    private const int MaxRevisions = 20000;
    private const long MaxBytes = 256L * 1024 * 1024;
    private static readonly Regex Template = new(@"\{\{\s*([^{}:#|\s][^{}|]*)", RegexOptions.Compiled);

    public static (int Pages, int Revisions) Write(string inputPath, string corpusPath, string outputPath)
    {
        using var corpus = JsonDocument.Parse(File.ReadAllText(corpusPath));
        var needs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rootRevisions = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in corpus.RootElement.GetProperty("Cases").EnumerateArray())
        {
            var pageTitle = item.GetProperty("PageTitle").GetString() ?? string.Empty;
            AddTitle(needs, pageTitle);
            var revisionElement = item.TryGetProperty("RequestedRevisionId", out var requested)
                ? requested
                : item.GetProperty("SourceRevisionId");
            var revisionId = revisionElement.ValueKind == JsonValueKind.Number
                ? revisionElement.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture)
                : revisionElement.GetString() ?? string.Empty;
            if (!rootRevisions.TryGetValue(RootKey(pageTitle), out var revisions))
                rootRevisions[RootKey(pageTitle)] = revisions = new HashSet<string>(StringComparer.Ordinal);
            revisions.Add(revisionId);
            AddModule(needs, item.GetProperty("ModuleTitle").GetString() ?? string.Empty);
        }

        var selected = new Dictionary<string, MediaWikiPage>(StringComparer.OrdinalIgnoreCase);
        for (var pass = 0; pass < MaxPasses; pass++)
        {
            var before = needs.Count;
            using var stream = new MediaWikiXmlReader().Open(inputPath);
            foreach (var page in stream.ReadPages())
            {
                if (!needs.Contains(PageKey(page))) continue;
                selected[page.SourcePageId] = page;
                if (selected.Count > MaxPages || selected.Values.Sum(page => page.Revisions.Count) > MaxRevisions)
                    throw new InvalidDataException($"Compatibility dependency closure exceeded limits ({MaxPages} pages/{MaxRevisions} revisions).");
                var revisions = rootRevisions.TryGetValue(PageKey(page), out var rootRevisionIds)
                    ? page.Revisions.Where(revision => rootRevisionIds.Contains(revision.SourceRevisionId))
                    : page.Revisions;
                foreach (var revision in revisions)
                {
                    foreach (var template in Template.Matches(revision.Wikitext).Select(match => match.Groups[1].Value.Trim()))
                        AddTemplate(needs, template);
                    foreach (var module in MediaWikiScribuntoScanner.Scan(revision.Wikitext).Dependencies)
                        AddModule(needs, module);
                }
            }
            if (needs.Count == before) break;
            if (pass == MaxPasses - 1) throw new InvalidDataException("Compatibility dependency closure exceeded traversal-pass limit.");
        }

        var selectedIds = selected.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedIds.Count == 0) throw new InvalidDataException("The compatibility corpus selected no source pages.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var pageCount = 0;
        var revisionCount = 0;
        using var reader = XmlReader.Create(inputPath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        using var writer = XmlWriter.Create(outputPath, new XmlWriterSettings { Indent = false, OmitXmlDeclaration = false });
        reader.MoveToContent();
        writer.WriteStartDocument();
        writer.WriteStartElement(reader.Prefix, reader.LocalName, reader.NamespaceURI);
        writer.WriteAttributes(reader, false);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.LocalName != "page") { writer.WriteNode(reader, true); continue; }
            using var subtree = reader.ReadSubtree();
            subtree.MoveToContent();
            var page = XElement.Load(subtree, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            reader.Skip();
            var id = page.Elements().FirstOrDefault(value => value.Name.LocalName == "id")?.Value.Trim() ?? string.Empty;
            if (!selectedIds.Contains(id)) continue;
            if (rootRevisions.TryGetValue(PageKey(page), out var requestedRevisions))
            {
                var filtered = new XElement(page);
                filtered.Elements().Where(value => value.Name.LocalName == "revision"
                    && !requestedRevisions.Contains(value.Elements().FirstOrDefault(id => id.Name.LocalName == "id")?.Value.Trim() ?? string.Empty)).Remove();
                if (!filtered.Elements().Any(value => value.Name.LocalName == "revision"))
                    throw new InvalidDataException($"Requested root revision(s) were not found for page '{PageKey(page)}'.");
                filtered.WriteTo(writer);
                revisionCount++;
            }
            else
            {
                page.WriteTo(writer);
                revisionCount += page.Elements().Count(value => value.Name.LocalName == "revision");
            }
            pageCount++;
        }
        writer.WriteEndElement();
        writer.WriteEndDocument();
        if (new FileInfo(outputPath).Length > MaxBytes)
            throw new InvalidDataException($"Compatibility subset exceeded the {MaxBytes} byte output limit.");
        return (pageCount, revisionCount);
    }

    private static string PageKey(MediaWikiPage page) => page.SourceNamespaceId + "|" + Normalize(page.Title);

    private static string PageKey(XElement page)
    {
        var ns = page.Elements().FirstOrDefault(value => value.Name.LocalName == "ns")?.Value.Trim() ?? string.Empty;
        var title = page.Elements().FirstOrDefault(value => value.Name.LocalName == "title")?.Value.Trim() ?? string.Empty;
        return ns + "|" + Normalize(title);
    }

    private static string RootKey(string title)
    {
        var trimmed = title.Trim();
        var prefix = trimmed.StartsWith("Module:", StringComparison.OrdinalIgnoreCase) ? "828" :
            trimmed.StartsWith("Template:", StringComparison.OrdinalIgnoreCase) ? "10" : "0";
        return prefix + "|" + Normalize(trimmed);
    }

    private static void AddTitle(HashSet<string> needs, string title)
    {
        var trimmed = title.Trim();
        if (trimmed.Length == 0) return;
        var prefix = trimmed.StartsWith("Module:", StringComparison.OrdinalIgnoreCase) ? "828" :
            trimmed.StartsWith("Template:", StringComparison.OrdinalIgnoreCase) ? "10" : "0";
        needs.Add(prefix + "|" + Normalize(trimmed));
    }

    private static void AddTemplate(HashSet<string> needs, string title)
        => needs.Add("10|" + Normalize(title.StartsWith("Template:", StringComparison.OrdinalIgnoreCase) ? title : "Template:" + title));

    private static void AddModule(HashSet<string> needs, string title)
        => needs.Add("828|" + Normalize(title.StartsWith("Module:", StringComparison.OrdinalIgnoreCase) ? title : "Module:" + title));

    private static string Normalize(string title)
    {
        var value = title.Trim().Replace('_', ' ');
        var separator = value.IndexOf(':');
        if (separator >= 0 && (value[..separator].Equals("Module", StringComparison.OrdinalIgnoreCase)
            || value[..separator].Equals("Template", StringComparison.OrdinalIgnoreCase))) value = value[(separator + 1)..];
        return value.Trim().Normalize();
    }
}
