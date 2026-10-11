namespace TightWiki.Plugin.Models;

#pragma warning disable CS1591

public enum TwPageSourceFormat
{
    NativeTightWiki = 0,
    ImportedMediaWiki = 1
}

/// <summary>Immutable provenance descriptor for the revision being rendered.</summary>
public sealed record TwMediaWikiRenderSource(
    TwPageSourceFormat Format,
    long? ImportBatchId = null,
    string? SourcePageId = null,
    string? SourceRevisionId = null,
    string? SourceTitle = null,
    bool IsRedirect = false,
    string? RedirectTargetSourceTitle = null,
    string? RedirectTargetNavigation = null,
    string? SourceNamespaceId = null,
    DateTime? SourceTimestampUtc = null,
    string? SourceWikiIdentity = null,
    string? ContentModel = null,
    int? TargetPageId = null,
    int? TargetRevision = null,
    string? SourceNamespaceName = null)
{
    public static TwMediaWikiRenderSource Native { get; } = new(TwPageSourceFormat.NativeTightWiki);

    public string CacheKey => Format == TwPageSourceFormat.NativeTightWiki
        ? "native"
        : $"mediawiki:{ImportBatchId}:{SourcePageId}:{SourceRevisionId}";
}

public sealed record TwMediaWikiPageTarget(
    string SourceTitle,
    string TargetNavigation,
    int TargetPageId);

public sealed record TwMediaWikiMediaTarget(
    string SourceFileName,
    string PageNavigation,
    string FileNavigation,
    string ContentType,
    int? FileRevision,
    bool Resolved);

public enum TwMediaWikiTemplateResolutionStatus
{
    Missing = 0,
    Resolved = 1,
    Restricted = 2,
    Ambiguous = 3
}

/// <summary>
/// Internal evidence attached to a template/transclusion resolution. Public
/// rendering continues to use the conservative Restricted/Unavailable
/// diagnostics and never exposes these protected distinctions to viewers.
/// </summary>
public enum TwMediaWikiRevisionResolutionReason
{
    None = 0,
    NoEligibleRevision = 1,
    SourceBodyUnavailable = 2,
    DeletedRevision = 3,
    SuppressedRevision = 4,
    AccessDenied = 5,
    UnsupportedContentModel = 6,
    ResolutionFailed = 7
}

public sealed record TwMediaWikiTemplateTarget(
    TwMediaWikiTemplateResolutionStatus Status,
    string SourceTitle,
    string? SourcePageId = null,
    string? SourceRevisionId = null,
    int? TargetRevision = null,
    string? Body = null,
    DateTime? SourceTimestampUtc = null,
    string? ContentModel = null,
    string SourceNamespaceId = "10",
    TwMediaWikiRevisionResolutionReason ResolutionReason = TwMediaWikiRevisionResolutionReason.None)
{
    public bool IsResolved => Status == TwMediaWikiTemplateResolutionStatus.Resolved
        && SourcePageId is not null
        && SourceRevisionId is not null
        && Body is not null;

    public string CacheKey => IsResolved
        ? $"{SourcePageId}:{SourceRevisionId}:{TargetRevision}"
        : $"{Status}:{SourceTitle}";
}

public enum TwMediaWikiModuleResolutionStatus
{
    Missing = 0,
    Resolved = 1,
    Restricted = 2,
    Ambiguous = 3
}

public sealed record TwMediaWikiModuleTarget(
    TwMediaWikiModuleResolutionStatus Status,
    string SourceTitle,
    string? SourcePageId = null,
    string? SourceRevisionId = null,
    int? TargetRevision = null,
    string? Body = null,
    DateTime? SourceTimestampUtc = null,
    string? ContentModel = null,
    TwMediaWikiRevisionResolutionReason ResolutionReason = TwMediaWikiRevisionResolutionReason.None)
{
    public bool IsResolved => Status == TwMediaWikiModuleResolutionStatus.Resolved
        && SourcePageId is not null && SourceRevisionId is not null && Body is not null;
}

#pragma warning restore CS1591
