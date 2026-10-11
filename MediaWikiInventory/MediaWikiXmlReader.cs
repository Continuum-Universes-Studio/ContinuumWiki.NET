using System.Xml;
using System.Xml.Linq;
using System.Globalization;

namespace MediaWikiInventory;

/// <summary>
/// Opens a MediaWiki XML export and exposes pages one at a time. The XML reader
/// never resolves DTDs or external resources. Only the current page is materialized.
/// </summary>
public sealed class MediaWikiXmlReader
{
    public MediaWikiXmlStream Open(string path) => new(path);
}

public sealed class MediaWikiXmlStream : IDisposable
{
    private readonly XmlReader _reader;
    private bool _atPage;
    private bool _enumerated;
    private bool _disposed;

    public MediaWikiImportHeader Header { get; }

    public MediaWikiXmlStream(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("MediaWiki XML export was not found.", path);
        try
        {
            _reader = XmlReader.Create(path, new XmlReaderSettings
            {
                Async = false,
                CheckCharacters = true,
                DtdProcessing = DtdProcessing.Prohibit,
                IgnoreComments = false,
                IgnoreWhitespace = false,
                XmlResolver = null,
                MaxCharactersFromEntities = 0
            });
            Header = ReadHeader();
        }
        catch (XmlException ex)
        {
            throw new MediaWikiImportFormatException("The MediaWiki XML export is malformed or uses a prohibited XML feature.", ex);
        }
    }

    public IEnumerable<MediaWikiPage> ReadPages()
    {
        ThrowIfDisposed();
        if (_enumerated) throw new InvalidOperationException("A MediaWiki XML stream can only be enumerated once.");
        _enumerated = true;
        if (_atPage)
        {
            _atPage = false;
            yield return ReadPage();
        }

        while (true)
        {
            if (_reader.NodeType == XmlNodeType.Element && _reader.LocalName == "page")
                yield return ReadPage();
            else if (_reader.NodeType == XmlNodeType.EndElement && _reader.Depth == 0)
                yield break;
            else if (!_reader.Read())
                yield break;
        }
    }

    private MediaWikiImportHeader ReadHeader()
    {
        if (!_reader.Read() || _reader.NodeType != XmlNodeType.XmlDeclaration)
            _reader.MoveToContent();
        else
            _reader.MoveToContent();

        if (_reader.NodeType != XmlNodeType.Element || _reader.LocalName != "mediawiki")
            throw new MediaWikiImportFormatException("The XML root element must be <mediawiki>.");

        var version = _reader.GetAttribute("version");
        var namespaces = new List<MediaWikiNamespaceDefinition>();
        var warnings = new List<MediaWikiImportWarning>();
        var unsupported = new List<MediaWikiUnsupportedXmlElement>();
        string? siteName = null, baseUrl = null, generator = null, caseRule = null;
        var rootDepth = _reader.Depth;

        if (_reader.IsEmptyElement) return new(siteName, baseUrl, generator, caseRule, version, namespaces, warnings, unsupported);
        if (!_reader.Read())
            return new(siteName, baseUrl, generator, caseRule, version, namespaces, warnings, unsupported);
        while (true)
        {
            if (_reader.NodeType == XmlNodeType.EndElement && _reader.Depth == rootDepth) break;
            if (_reader.NodeType != XmlNodeType.Element || _reader.Depth != rootDepth + 1)
            {
                if (!_reader.Read()) break;
                continue;
            }
            if (_reader.LocalName == "siteinfo")
            {
                ParseSiteInfo(ref siteName, ref baseUrl, ref generator, ref caseRule, namespaces, warnings, unsupported);
                continue;
            }
            else if (_reader.LocalName == "page")
            {
                _atPage = true;
                break;
            }
            else
            {
                unsupported.Add(CaptureUnsupported("mediawiki", warnings));
                continue;
            }
        }
        return new(siteName, baseUrl, generator, caseRule, version, namespaces, warnings, unsupported);
    }

    private void ParseSiteInfo(ref string? siteName, ref string? baseUrl, ref string? generator,
        ref string? caseRule, List<MediaWikiNamespaceDefinition> namespaces,
        List<MediaWikiImportWarning> warnings, List<MediaWikiUnsupportedXmlElement> unsupported)
    {
        XElement siteInfo;
        try { siteInfo = ReadCurrentElement(); }
        catch (XmlException ex) { throw new MediaWikiImportFormatException("The siteinfo XML is malformed.", ex); }
        foreach (var child in siteInfo.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "sitename": siteName = child.Value; break;
                case "base": baseUrl = child.Value; break;
                case "generator": generator = child.Value; break;
                case "case": caseRule = child.Value; break;
                case "namespaces":
                    foreach (var ns in child.Elements().Where(e => e.Name.LocalName == "namespace"))
                    {
                        var key = ns.Attribute("key")?.Value;
                        if (string.IsNullOrEmpty(key))
                            warnings.Add(new("MissingNamespaceId", "A namespace definition has no key attribute.", "siteinfo/namespaces/namespace"));
                        namespaces.Add(new(key ?? string.Empty, ns.Value, ns.Attribute("canonical")?.Value, ns.Attribute("case")?.Value));
                    }
                    break;
                default:
                    unsupported.Add(new(child.Name.LocalName, child.ToString(SaveOptions.DisableFormatting), "siteinfo/" + child.Name.LocalName));
                    warnings.Add(new("UnsupportedXmlElement", $"Unsupported siteinfo element <{child.Name.LocalName}> was preserved.", "siteinfo"));
                    break;
            }
        }
    }

    private MediaWikiPage ReadPage()
    {
        XElement page;
        try { page = ReadCurrentElement(); }
        catch (XmlException ex) { throw new MediaWikiImportFormatException("A MediaWiki page XML element is malformed or uses a prohibited XML feature.", ex); }
        var warnings = new List<MediaWikiImportWarning>();
        var unsupported = new List<MediaWikiUnsupportedXmlElement>();
        var id = ChildValue(page, "id");
        var title = ChildValue(page, "title", preserveWhitespace: true);
        var ns = ChildValue(page, "ns");
        if (id.Length == 0) warnings.Add(new("MissingPageId", "The page has no source id.", "page"));
        if (title.Length == 0) warnings.Add(new("MissingPageTitle", "The page has no title.", "page"));
        if (ns.Length == 0) warnings.Add(new("MissingNamespaceId", "The page has no namespace id; an empty value is preserved.", "page"));
        var redirect = page.Elements().FirstOrDefault(e => e.Name.LocalName == "redirect");
        var redirectTarget = redirect?.Attribute("title")?.Value;
        var revisions = page.Elements().Where(e => e.Name.LocalName == "revision").Select(ReadRevision).ToArray();
        if (revisions.Length == 0) warnings.Add(new("MissingRevision", "The page has no revision elements.", "page"));
        foreach (var element in page.Elements().Where(e => e.Name.LocalName is not ("title" or "ns" or "id" or "redirect" or "revision")))
        {
            unsupported.Add(new(element.Name.LocalName, element.ToString(SaveOptions.DisableFormatting), "page/" + element.Name.LocalName));
            warnings.Add(new("UnsupportedXmlElement", $"Unsupported page element <{element.Name.LocalName}> was preserved.", "page"));
        }
        return new(id, title, ns, redirectTarget, redirect is not null, revisions, warnings, unsupported);
    }

    private MediaWikiRevision ReadRevision(XElement revision)
    {
        var warnings = new List<MediaWikiImportWarning>();
        var unsupported = new List<MediaWikiUnsupportedXmlElement>();
        var id = ChildValue(revision, "id");
        var parentId = ChildValue(revision, "parentid");
        var timestampText = ChildValue(revision, "timestamp");
        DateTimeOffset? timestamp = null;
        if (timestampText.Length > 0)
        {
            if (DateTimeOffset.TryParse(timestampText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)) timestamp = parsed;
            else warnings.Add(new("InvalidTimestamp", $"Revision timestamp '{timestampText}' is not a valid date/time.", "revision/timestamp"));
        }
        if (id.Length == 0) warnings.Add(new("MissingRevisionId", "The revision has no source id.", "revision"));
        var text = revision.Elements().FirstOrDefault(e => e.Name.LocalName == "text");
        var wikitext = text is null ? string.Empty : WikitextOf(text);
        var contributor = ReadContributor(revision.Elements().FirstOrDefault(e => e.Name.LocalName == "contributor"));
        var contentModel = ChildValueOrAttribute(revision, "model", "contentmodel");
        var contentFormat = ChildValueOrAttribute(revision, "format", "contentformat");
        var isDeleted = HasFlag(revision, "deleted") || HasFlag(revision, "suppressed") || (text is not null && HasFlag(text, "deleted"));
        var isSuppressed = HasFlag(revision, "suppressed") || (text is not null && HasFlag(text, "suppressed"));
        if (timestampText.Length == 0) warnings.Add(new("MissingTimestamp", "The revision has no timestamp.", "revision"));
        if (contributor.Kind == MediaWikiContributorKind.Missing)
            warnings.Add(new("MissingContributor", "The revision has no contributor element; missing attribution is preserved.", "revision"));
        if (text is null) warnings.Add(new("MissingWikitext", "The revision has no text element; empty content is preserved with WikitextPresent=false.", "revision"));
        foreach (var element in revision.Elements().Where(e => e.Name.LocalName is not ("id" or "parentid" or "timestamp" or "contributor" or "comment" or "model" or "format" or "text" or "sha1" or "minor")))
        {
            unsupported.Add(new(element.Name.LocalName, element.ToString(SaveOptions.DisableFormatting), "revision/" + element.Name.LocalName));
            warnings.Add(new("UnsupportedXmlElement", $"Unsupported revision element <{element.Name.LocalName}> was preserved.", "revision"));
        }
        return new(id, parentId, timestamp, revision.Elements().FirstOrDefault(e => e.Name.LocalName == "comment")?.Value,
            contributor, wikitext, text is not null, contentModel, contentFormat,
            ChildValue(revision, "sha1"), revision.Elements().Any(e => e.Name.LocalName == "minor"),
            isDeleted, isSuppressed, warnings, unsupported);
    }

    private static MediaWikiContributor ReadContributor(XElement? contributor)
    {
        if (contributor is null) return new(MediaWikiContributorKind.Missing, null, null, null, false);
        var suppressed = HasFlag(contributor, "suppressed");
        var ip = ChildValue(contributor, "ip");
        if (ip.Length > 0) return new(MediaWikiContributorKind.AnonymousIp, ChildValue(contributor, "id"), null, ip, suppressed);
        var username = ChildValue(contributor, "username", preserveWhitespace: true);
        if (username.Length > 0) return new(MediaWikiContributorKind.NamedUser, ChildValue(contributor, "id"), username, null, suppressed);
        return new(MediaWikiContributorKind.AnonymousUnknown, ChildValue(contributor, "id"), null, null, suppressed);
    }

    private static string WikitextOf(XElement text)
    {
        if (!text.HasElements) return text.Value;
        return string.Concat(text.Nodes().Select(n => n is XText or XCData ? n.ToString() : n.ToString(SaveOptions.DisableFormatting)));
    }

    private static string ChildValue(XElement parent, string name, bool preserveWhitespace = false) =>
        Child(parent, name)?.Value is { } value ? preserveWhitespace ? value : value.Trim() : string.Empty;

    private static XElement? Child(XElement parent, string name) => parent.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    private static string ChildValueOrAttribute(XElement parent, string elementName, string attributeName) =>
        ChildValue(parent, elementName) is { Length: > 0 } value ? value : parent.Attribute(attributeName)?.Value ?? string.Empty;

    private static bool HasFlag(XElement element, string name) =>
        string.Equals(element.Attribute(name)?.Value, name, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(element.Attribute(name)?.Value, "true", StringComparison.OrdinalIgnoreCase);

    private MediaWikiUnsupportedXmlElement CaptureUnsupported(string location, List<MediaWikiImportWarning> warnings)
    {
        var name = _reader.Name;
        var raw = _reader.ReadOuterXml();
        warnings.Add(new("UnsupportedXmlElement", $"Unsupported XML element <{name}> was preserved.", location));
        return new(name, raw, location + "/" + name);
    }

    private XElement ReadCurrentElement()
    {
        using var subtree = _reader.ReadSubtree();
        subtree.MoveToContent();
        var element = (XElement)XNode.ReadFrom(subtree);
        _reader.Skip();
        return element;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MediaWikiXmlStream));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reader.Dispose();
    }
}
