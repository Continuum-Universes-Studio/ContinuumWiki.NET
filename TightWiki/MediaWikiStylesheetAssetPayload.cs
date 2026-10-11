namespace TightWiki;

/// <summary>
/// Signed, time-limited identity for a renderer-approved TemplateStyles asset.
/// The endpoint treats every field as an assertion to revalidate, never as a
/// permission or a filesystem location.
/// </summary>
public sealed record MediaWikiStylesheetAssetPayload(
    long ImportBatchId,
    string SourceTitle,
    string SourcePageId,
    string SourceRevisionId,
    int TargetRevision,
    DateTime? SourceTimestampUtc,
    string SourceSha256,
    string SanitizerPolicyVersion,
    string SanitizedCssSha256);
