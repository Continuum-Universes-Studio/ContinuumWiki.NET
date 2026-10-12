namespace TightWiki.Plugin.Models;

#pragma warning disable CS1591

public static class TwCategoryRecoveryLimits
{
    public const int MaxBatchPages = 100;
    public const int MaxCheckpointCharacters = 128;
}

public enum TwCategoryRecoveryOperation
{
    Preview,
    Execute
}

public enum TwCategoryRecoveryPlanDisposition
{
    Ready,
    NoChange,
    NeedsReindex,
    IndexedEmpty,
    Unindexed,
    Inconsistent,
    IncompleteExtraction,
    Unsupported,
    Unauthorized,
    RepositoryUnavailable,
    PageMissing,
    PageUnavailable,
    RevisionChanged,
    InvalidRequest
}

public enum TwCategoryRecoveryExecutionDisposition
{
    Reindexed,
    NoChange,
    IncompleteExtraction,
    RevisionConflict,
    PageMissing,
    Unauthorized,
    RepositoryUnavailable,
    PersistenceFailure,
    InvalidRequest,
    Cancelled
}

/// <summary>
/// A bounded operator-supplied target. Revisions are intentionally absent:
/// the recovery service always re-resolves the authoritative current revision.
/// </summary>
public sealed record TwCategoryRecoveryTarget(int PageId, string? ExpectedNavigation = null);

public sealed record TwCategoryRecoveryPlanRequest(
    IReadOnlyList<TwCategoryRecoveryTarget> Targets,
    Guid? ActorUserId = null);

public sealed record TwCategoryRecoveryExecutionRequest(
    IReadOnlyList<TwCategoryRecoveryTarget> Targets,
    TwCategoryRecoveryCheckpoint? Checkpoint = null,
    Guid? ActorUserId = null,
    TwCategoryRecoveryExecutionMode Mode = TwCategoryRecoveryExecutionMode.SourceOnlyExecution);

public sealed record TwCategoryRecoveryCheckpoint(string ScopeFingerprint, int NextTargetIndex)
{
    public bool IsBounded => !string.IsNullOrWhiteSpace(ScopeFingerprint)
        && ScopeFingerprint.Length <= TwCategoryRecoveryLimits.MaxCheckpointCharacters
        && NextTargetIndex >= 0;
}

public sealed record TwCategoryRecoveryOperationAuthorizationContext(
    TwCategoryRecoveryOperation Operation,
    Guid? ActorUserId);

public sealed record TwCategoryRecoveryPageAuthorizationContext(
    TwCategoryRecoveryOperation Operation,
    int PageId,
    string PageNavigation,
    int CurrentRevision,
    Guid? ActorUserId);

public enum TwCategoryRecoveryOutcomeKind
{
    NoChange,
    NeedsReindex,
    IndexedEmpty,
    Unindexed,
    Inconsistent,
    IncompleteExtraction,
    Unsupported,
    Reindexed,
    RevisionConflict,
    PageMissing,
    Unauthorized,
    RepositoryUnavailable,
    InvalidRequest,
    Cancelled,
    PersistenceFailure
}

public sealed record TwCategoryRecoveryOutcomeCount(
    TwCategoryRecoveryOutcomeKind Outcome,
    int Count);

public sealed record TwCategoryRecoverySummary(
    Guid CorrelationId,
    DateTime StartedAtUtc,
    DateTime CompletedAtUtc,
    int TargetCount,
    int RedactedTargetCount,
    IReadOnlyList<TwCategoryRecoveryOutcomeCount> Outcomes);

public sealed record TwCategoryRecoveryPlanEntry(
    int? PageId,
    string? PageNavigation,
    int? CurrentRevision,
    TwCategoryRecoveryPlanDisposition Disposition,
    TwCategoryIndexConsistencyState ConsistencyState,
    bool ExtractionComplete,
    string? DiagnosticCode = null);

public sealed record TwCategoryRecoveryPlan(
    TwCategoryRecoveryPlanDisposition Disposition,
    string? ScopeFingerprint,
    IReadOnlyList<TwCategoryRecoveryPlanEntry> Entries,
    TwCategoryRecoveryCheckpoint? Checkpoint = null,
    string? DiagnosticCode = null,
    TwCategoryRecoverySummary? Summary = null);

public sealed record TwCategoryRecoveryExecutionEntry(
    int? PageId,
    string? PageNavigation,
    int? RequestedRevision,
    int? ResolvedRevision,
    TwCategoryRecoveryExecutionDisposition Disposition,
    string? DiagnosticCode = null);

public sealed record TwCategoryRecoveryExecutionResult(
    TwCategoryRecoveryExecutionDisposition Disposition,
    IReadOnlyList<TwCategoryRecoveryExecutionEntry> Entries,
    TwCategoryRecoveryCheckpoint? NextCheckpoint = null,
    string? DiagnosticCode = null,
    TwCategoryRecoverySummary? Summary = null);

#pragma warning restore CS1591
