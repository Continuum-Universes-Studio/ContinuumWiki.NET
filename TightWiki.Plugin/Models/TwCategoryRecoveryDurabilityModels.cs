using System.Security.Cryptography;
using System.Text;

namespace TightWiki.Plugin.Models;

#pragma warning disable CS1591

public static class TwCategoryRecoveryDurabilityConstants
{
    public const int CurrentSchemaVersion = 1;
    public const string PolicyVersion = "cw-m5-017-category-recovery-v1";
    public const int MaxExpectedNavigationCharacters = 512;
    public const int MaxAuditEventsPerOperation = 4096;
}

public enum TwCategoryRecoveryExecutionMode
{
    Preview,
    SourceOnlyExecution,
    DurableExecutionRequested
}

public enum TwCategoryRecoveryOperationStatus
{
    Planned,
    Running,
    Interrupted,
    Recoverable,
    Completed,
    PartiallyCompleted,
    Failed,
    Cancelled
}

public enum TwCategoryRecoveryAuditEventKind
{
    IntentRequested,
    AuthorizationDecision,
    RevisionResolved,
    ReconciliationAttempted,
    ReconciliationCommitted,
    NoChangeObserved,
    ConflictObserved,
    IncompleteExpansion,
    Unavailable,
    Failure,
    Cancellation,
    Resumed
}

public enum TwCategoryRecoveryAuditConfidence
{
    Intent,
    Observation,
    Attempted,
    VerifiedCommit
}

public enum TwCategoryRecoveryDurabilityDisposition
{
    Created,
    Replayed,
    Loaded,
    Appended,
    DuplicateSame,
    Advanced,
    Finalized,
    ProgressRead,
    ScopeConflict,
    DuplicateConflict,
    GenerationConflict,
    OutcomeNotRecorded,
    BackwardsCheckpoint,
    InvalidTransition,
    InvalidRequest,
    IncompatibleVersion,
    Unavailable
}

public enum TwCategoryDurableRecoveryDisposition
{
    Completed,
    Recoverable,
    PartiallyCompleted,
    Interrupted,
    Failed,
    Cancelled,
    Unauthorized,
    Unavailable,
    ScopeConflict,
    AlreadyCompleted,
    InvalidRequest,
    IncompatibleVersion,
    GenerationConflict
}

public sealed record TwCategoryRecoveryScope(
    IReadOnlyList<TwCategoryRecoveryTarget> Targets,
    string Fingerprint,
    string PolicyVersion)
{
    public static bool TryCreate(
        IReadOnlyList<TwCategoryRecoveryTarget>? input,
        out TwCategoryRecoveryScope scope,
        out string diagnosticCode)
    {
        scope = null!;
        diagnosticCode = string.Empty;
        if (input is null || input.Count == 0)
        {
            diagnosticCode = "RecoveryTargetsRequired";
            return false;
        }
        if (input.Count > TwCategoryRecoveryLimits.MaxBatchPages)
        {
            diagnosticCode = "RecoveryBatchLimitExceeded";
            return false;
        }
        if (input.Any(target => target is null || target.PageId <= 0))
        {
            diagnosticCode = "InvalidRecoveryTarget";
            return false;
        }
        if (input.Any(target => target.ExpectedNavigation?.Length > TwCategoryRecoveryDurabilityConstants.MaxExpectedNavigationCharacters))
        {
            diagnosticCode = "ExpectedNavigationLimitExceeded";
            return false;
        }
        if (input.Select(target => target.PageId).Distinct().Count() != input.Count)
        {
            diagnosticCode = "DuplicateRecoveryTarget";
            return false;
        }

        var targets = input.OrderBy(target => target.PageId).ToArray();
        var fingerprintInput = TwCategoryRecoveryDurabilityConstants.PolicyVersion
            + "\0"
            + string.Join(",", targets.Select(target => $"{target.PageId}:{target.ExpectedNavigation ?? string.Empty}"));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintInput))).ToLowerInvariant();
        scope = new(targets, fingerprint, TwCategoryRecoveryDurabilityConstants.PolicyVersion);
        return true;
    }
}

public sealed record TwCategoryRecoveryOperationRecord(
    Guid OperationId,
    TwCategoryRecoveryScope Scope,
    Guid? ActorUserId,
    DateTime CreatedAtUtc,
    string PolicyVersion,
    TwCategoryRecoveryOperationStatus Status,
    TwCategoryRecoveryDurableCheckpoint Checkpoint,
    string? TerminalReason = null);

public sealed record TwCategoryRecoveryPageWorkIdentity(
    Guid OperationId,
    int Position,
    int PageId,
    string? ExpectedNavigation,
    int AttemptNumber,
    Guid AttemptId,
    int? PlannedRevision = null,
    int? ObservedRevision = null);

public sealed record TwCategoryRecoveryDurableCheckpoint(
    Guid OperationId,
    string ScopeFingerprint,
    int CommittedPosition,
    long Generation,
    long LastOutcomeSequence,
    int SchemaVersion = TwCategoryRecoveryDurabilityConstants.CurrentSchemaVersion)
{
    public bool IsAtEnd(int targetCount) => CommittedPosition == targetCount;
}

public sealed record TwCategoryRecoveryDurablePageOutcome(
    TwCategoryRecoveryPageWorkIdentity Work,
    TwCategoryRecoveryExecutionDisposition Disposition,
    TwCategoryRecoveryAuditConfidence Confidence,
    int? ResolvedRevision,
    string? DiagnosticCode = null);

public sealed record TwCategoryRecoveryAuditEvent(
    Guid EventId,
    string EventKey,
    Guid OperationId,
    int Position,
    Guid AttemptId,
    TwCategoryRecoveryAuditEventKind Kind,
    TwCategoryRecoveryAuditConfidence Confidence,
    int? PageId,
    int? ObservedRevision,
    TwCategoryRecoveryExecutionDisposition? Outcome,
    Guid? ActorUserId,
    DateTime OccurredAtUtc,
    string? DiagnosticCode = null,
    int SchemaVersion = TwCategoryRecoveryDurabilityConstants.CurrentSchemaVersion,
    long? Sequence = null);

public sealed record TwCategoryRecoveryDurableExecutionRequest(
    Guid OperationId,
    IReadOnlyList<TwCategoryRecoveryTarget> Targets,
    Guid? ActorUserId = null,
    TwCategoryRecoveryDurableCheckpoint? ExpectedCheckpoint = null);

public sealed record TwCategoryRecoveryDurablePageResult(
    int Position,
    int? PageId,
    int? ResolvedRevision,
    TwCategoryRecoveryExecutionDisposition Disposition,
    TwCategoryRecoveryAuditConfidence Confidence,
    string? DiagnosticCode = null);

public sealed record TwCategoryDurableRecoveryExecutionResult(
    TwCategoryDurableRecoveryDisposition Disposition,
    Guid OperationId,
    IReadOnlyList<TwCategoryRecoveryDurablePageResult> Entries,
    TwCategoryRecoveryDurableCheckpoint? Checkpoint = null,
    TwCategoryRecoveryOperationStatus? OperationStatus = null,
    string? DiagnosticCode = null);

public sealed record TwCategoryRecoveryCreateOperationResult(
    TwCategoryRecoveryDurabilityDisposition Disposition,
    TwCategoryRecoveryOperationRecord? Operation = null,
    string? DiagnosticCode = null);

public sealed record TwCategoryRecoveryLoadOperationResult(
    TwCategoryRecoveryDurabilityDisposition Disposition,
    TwCategoryRecoveryOperationRecord? Operation = null,
    string? DiagnosticCode = null);

public sealed record TwCategoryRecoveryAppendAuditResult(
    TwCategoryRecoveryDurabilityDisposition Disposition,
    long? EventSequence = null,
    string? DiagnosticCode = null);

public sealed record TwCategoryRecoveryCheckpointAdvanceRequest(
    Guid OperationId,
    string ScopeFingerprint,
    int ExpectedCommittedPosition,
    long ExpectedGeneration,
    long ExpectedOutcomeSequence,
    int NextCommittedPosition,
    long RecordedOutcomeSequence,
    string RecordedOutcomeEventKey,
    int TargetCount,
    int SchemaVersion = TwCategoryRecoveryDurabilityConstants.CurrentSchemaVersion);

public sealed record TwCategoryRecoveryAdvanceCheckpointResult(
    TwCategoryRecoveryDurabilityDisposition Disposition,
    TwCategoryRecoveryDurableCheckpoint? Checkpoint = null,
    string? DiagnosticCode = null);

public sealed record TwCategoryRecoveryTransitionOperationRequest(
    Guid OperationId,
    TwCategoryRecoveryOperationStatus ExpectedStatus,
    TwCategoryRecoveryOperationStatus NewStatus,
    long ExpectedGeneration,
    string? TerminalReason = null,
    int SchemaVersion = TwCategoryRecoveryDurabilityConstants.CurrentSchemaVersion);

public sealed record TwCategoryRecoveryTransitionOperationResult(
    TwCategoryRecoveryDurabilityDisposition Disposition,
    TwCategoryRecoveryOperationRecord? Operation = null,
    string? DiagnosticCode = null);

public sealed record TwCategoryRecoveryProgressRequest(
    Guid OperationId,
    string ScopeFingerprint,
    Guid? ActorUserId);

/// <summary>
/// Progress deliberately contains no page identities or private failure data.
/// A future authorized operator projection may add page details separately.
/// </summary>
public sealed record TwCategoryRecoveryDurableProgress(
    Guid OperationId,
    string ScopeFingerprint,
    TwCategoryRecoveryOperationStatus Status,
    TwCategoryRecoveryDurableCheckpoint Checkpoint,
    IReadOnlyList<TwCategoryRecoveryOutcomeCount> OutcomeCounts,
    bool Redacted = true);

public sealed record TwCategoryRecoveryProgressResult(
    TwCategoryRecoveryDurabilityDisposition Disposition,
    TwCategoryRecoveryDurableProgress? Progress = null,
    string? DiagnosticCode = null);

public static class TwCategoryRecoveryDurabilityPolicy
{
    public static bool IsTerminal(TwCategoryRecoveryOperationStatus status)
        => status is TwCategoryRecoveryOperationStatus.Completed
            or TwCategoryRecoveryOperationStatus.Failed
            or TwCategoryRecoveryOperationStatus.Cancelled;

    public static bool CanTransition(TwCategoryRecoveryOperationStatus current, TwCategoryRecoveryOperationStatus next)
        => current switch
        {
            TwCategoryRecoveryOperationStatus.Planned => next is TwCategoryRecoveryOperationStatus.Running or TwCategoryRecoveryOperationStatus.Interrupted or TwCategoryRecoveryOperationStatus.Cancelled,
            TwCategoryRecoveryOperationStatus.Running => next is TwCategoryRecoveryOperationStatus.Interrupted or TwCategoryRecoveryOperationStatus.Recoverable or TwCategoryRecoveryOperationStatus.Completed or TwCategoryRecoveryOperationStatus.PartiallyCompleted or TwCategoryRecoveryOperationStatus.Failed or TwCategoryRecoveryOperationStatus.Cancelled,
            TwCategoryRecoveryOperationStatus.Interrupted => next is TwCategoryRecoveryOperationStatus.Running or TwCategoryRecoveryOperationStatus.Cancelled,
            TwCategoryRecoveryOperationStatus.Recoverable => next is TwCategoryRecoveryOperationStatus.Running or TwCategoryRecoveryOperationStatus.Cancelled,
            TwCategoryRecoveryOperationStatus.PartiallyCompleted => next is TwCategoryRecoveryOperationStatus.Running or TwCategoryRecoveryOperationStatus.Completed or TwCategoryRecoveryOperationStatus.Failed,
            _ => false
        };

    public static string? ValidateCheckpointAdvance(
        TwCategoryRecoveryDurableCheckpoint checkpoint,
        TwCategoryRecoveryCheckpointAdvanceRequest request,
        bool recordedOutcomeExists)
    {
        if (request.SchemaVersion != TwCategoryRecoveryDurabilityConstants.CurrentSchemaVersion)
            return "IncompatibleCheckpointVersion";
        if (checkpoint.OperationId != request.OperationId || !string.Equals(checkpoint.ScopeFingerprint, request.ScopeFingerprint, StringComparison.Ordinal))
            return "CheckpointScopeMismatch";
        if (checkpoint.CommittedPosition != request.ExpectedCommittedPosition || checkpoint.Generation != request.ExpectedGeneration || checkpoint.LastOutcomeSequence != request.ExpectedOutcomeSequence)
            return "CheckpointGenerationConflict";
        if (request.NextCommittedPosition < checkpoint.CommittedPosition)
            return "CheckpointCannotMoveBackwards";
        if (request.NextCommittedPosition > request.TargetCount)
            return "CheckpointBeyondScope";
        if (request.RecordedOutcomeSequence <= checkpoint.LastOutcomeSequence || !recordedOutcomeExists)
            return "OutcomeNotRecorded";
        return null;
    }
}

#pragma warning restore CS1591
