namespace TightWiki.Plugin.Models;

/// <summary>
/// Immutable, renderer-produced identity for one sanitized MediaWiki
/// TemplateStyles asset. The application may turn this into a delivery URL,
/// but the identity itself is not an authorization grant.
/// </summary>
public sealed record TwMediaWikiStylesheet(
    long ImportBatchId,
    string SourceTitle,
    string SourcePageId,
    string SourceRevisionId,
    int TargetRevision,
    DateTime? SourceTimestampUtc,
    string ContentModel,
    string SourceSha256,
    string ScopeSelector,
    string CacheKey,
    string SanitizerPolicyVersion,
    string SanitizedCssSha256,
    int ReferenceOrder = 0);
