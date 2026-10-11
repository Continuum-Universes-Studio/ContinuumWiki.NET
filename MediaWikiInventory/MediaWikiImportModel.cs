namespace MediaWikiInventory;

public sealed record MediaWikiImportHeader(
    string? SiteName,
    string? BaseUrl,
    string? Generator,
    string? CaseRule,
    string? ExportVersion,
    IReadOnlyList<MediaWikiNamespaceDefinition> Namespaces,
    IReadOnlyList<MediaWikiImportWarning> Warnings,
    IReadOnlyList<MediaWikiUnsupportedXmlElement> UnsupportedElements);

public sealed record MediaWikiNamespaceDefinition(
    string SourceNamespaceId,
    string Name,
    string? CanonicalName,
    string? CaseRule);

public sealed record MediaWikiPage(
    string SourcePageId,
    string Title,
    string SourceNamespaceId,
    string? RedirectTarget,
    bool IsRedirect,
    IReadOnlyList<MediaWikiRevision> Revisions,
    IReadOnlyList<MediaWikiImportWarning> Warnings,
    IReadOnlyList<MediaWikiUnsupportedXmlElement> UnsupportedElements);

public sealed record MediaWikiRevision(
    string SourceRevisionId,
    string? ParentRevisionId,
    DateTimeOffset? Timestamp,
    string? Comment,
    MediaWikiContributor Contributor,
    string Wikitext,
    bool WikitextPresent,
    string? ContentModel,
    string? ContentFormat,
    string? Sha1,
    bool IsMinor,
    bool IsDeleted,
    bool IsSuppressed,
    IReadOnlyList<MediaWikiImportWarning> Warnings,
    IReadOnlyList<MediaWikiUnsupportedXmlElement> UnsupportedElements);

public enum MediaWikiContributorKind
{
    Missing,
    NamedUser,
    AnonymousIp,
    AnonymousUnknown
}

public sealed record MediaWikiContributor(
    MediaWikiContributorKind Kind,
    string? SourceId,
    string? DisplayName,
    string? IpAddress,
    bool IsSuppressed);

public sealed record MediaWikiImportWarning(string Code, string Message, string Location);

public sealed record MediaWikiUnsupportedXmlElement(string Name, string RawXml, string Location);

public sealed class MediaWikiImportFormatException(string message, Exception? innerException = null)
    : Exception(message, innerException);
