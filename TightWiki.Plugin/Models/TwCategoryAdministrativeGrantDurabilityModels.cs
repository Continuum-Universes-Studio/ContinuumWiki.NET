using TightWiki.Plugin.Interfaces.Persistence;

namespace TightWiki.Plugin.Models;

#pragma warning disable CS1591

public static class TwCategoryAdministrativeGrantDurabilityConstants
{
    public const int CurrentSchemaVersion = 3;
    public const string PolicyVersion = "cw-m5-024-admin-grant-v1";
    public const int MaxAuditEventsPerGrant = 128;
    public const int MaxIssuerEvidenceCharacters = 192;
    public const int MaxRevocationReasonCharacters = 256;
    public const int MaxOutcomeCharacters = 64;
}

public enum TwCategoryAdministrativeGrantState
{
    Issued,
    Consumed,
    Revoked
}

public enum TwCategoryAdministrativeGrantDurabilityDisposition
{
    Created,
    Loaded,
    Consumed,
    AlreadyConsumed,
    Revoked,
    AlreadyRevoked,
    GenerationConflict,
    ScopeConflict,
    DuplicateConflict,
    OutcomeRecorded,
    DuplicateSame,
    InvalidRequest,
    IncompatibleVersion,
    Unavailable
}

public enum TwCategoryAdministrativeGrantAuditKind
{
    Issued,
    Rejected,
    Revoked,
    Consumed,
    ExecutionOutcome
}

public enum TwCategoryAdministrativeGrantFaultBoundary
{
    BeforeTransitionAuditCommit
}

public interface ITwCategoryAdministrativeGrantFaultInjector
{
    Task InjectAsync(
        TwCategoryAdministrativeGrantFaultBoundary boundary,
        CancellationToken cancellationToken = default);
}

public sealed record TwCategoryAdministrativeGrantRecord(
    TwCategoryAdministrativeGrant Grant,
    long Version,
    TwCategoryAdministrativeGrantState State,
    DateTime? RevokedAtUtc,
    Guid? RevokedByActorUserId,
    string? RevocationReason,
    DateTime? ConsumedAtUtc,
    Guid? ConsumedByActorUserId,
    Guid? ConsumedOperationId,
    Guid? ConsumedCorrelationId,
    long LastAuditSequence);

public sealed record TwCategoryAdministrativeGrantCreateRequest(
    TwCategoryAdministrativeGrant Grant,
    Guid IssuerActorUserId,
    Guid CorrelationId,
    DateTime IssuedAtUtc);

public sealed record TwCategoryAdministrativeGrantLoadResult(
    TwCategoryAdministrativeGrantDurabilityDisposition Disposition,
    TwCategoryAdministrativeGrantRecord? Grant = null,
    string? DiagnosticCode = null);

public sealed record TwCategoryAdministrativeGrantCreateResult(
    TwCategoryAdministrativeGrantDurabilityDisposition Disposition,
    TwCategoryAdministrativeGrantRecord? Grant = null,
    string? DiagnosticCode = null);

public sealed record TwCategoryAdministrativeGrantConsumeRequest(
    Guid GrantId,
    long ExpectedVersion,
    Guid ActorUserId,
    TwCategoryAdministrativeOperation Operation,
    TwCategoryRecoveryScope Scope,
    Guid CorrelationId,
    DateTime NowUtc,
    int SchemaVersion = TwCategoryAdministrativeGrantDurabilityConstants.CurrentSchemaVersion);

public sealed record TwCategoryAdministrativeGrantConsumeResult(
    TwCategoryAdministrativeGrantDurabilityDisposition Disposition,
    TwCategoryAdministrativeGrantRecord? Grant = null,
    string? DiagnosticCode = null);

public sealed record TwCategoryAdministrativeGrantRevokeRequest(
    Guid GrantId,
    long ExpectedVersion,
    Guid RevokingActorUserId,
    Guid CorrelationId,
    DateTime RevokedAtUtc,
    string Reason,
    int SchemaVersion = TwCategoryAdministrativeGrantDurabilityConstants.CurrentSchemaVersion);

public sealed record TwCategoryAdministrativeGrantRevokeResult(
    TwCategoryAdministrativeGrantDurabilityDisposition Disposition,
    TwCategoryAdministrativeGrantRecord? Grant = null,
    string? DiagnosticCode = null);

public sealed record TwCategoryAdministrativeGrantExecutionOutcomeRequest(
    Guid GrantId,
    Guid ActorUserId,
    TwCategoryAdministrativeOperation Operation,
    TwCategoryRecoveryScope Scope,
    Guid CorrelationId,
    string Outcome,
    DateTime OccurredAtUtc,
    int SchemaVersion = TwCategoryAdministrativeGrantDurabilityConstants.CurrentSchemaVersion);

public sealed record TwCategoryAdministrativeGrantExecutionOutcomeResult(
    TwCategoryAdministrativeGrantDurabilityDisposition Disposition,
    string? DiagnosticCode = null);

public sealed record TwCategoryAdministrativeGrantAuthorizationRejectionRequest(
    Guid GrantId,
    Guid ActorUserId,
    TwCategoryAdministrativeOperation Operation,
    Guid CorrelationId,
    string Reason,
    DateTime OccurredAtUtc,
    int SchemaVersion = TwCategoryAdministrativeGrantDurabilityConstants.CurrentSchemaVersion);

public sealed record TwCategoryAdministrativeGrantAuthorizationRejectionResult(
    TwCategoryAdministrativeGrantDurabilityDisposition Disposition,
    string? DiagnosticCode = null);

public interface ITwCategoryAdministrativeGrantOutcomeRecorder
{
    Task<TwCategoryAdministrativeGrantAuthorizationRejectionResult> RecordAuthorizationRejectionAsync(
        TwCategoryAdministrativeGrantAuthorizationRejectionRequest request,
        CancellationToken cancellationToken = default);

    Task<TwCategoryAdministrativeGrantExecutionOutcomeResult> RecordExecutionOutcomeAsync(
        TwCategoryAdministrativeGrantExecutionOutcomeRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Fail-closed store used by the default application graph.</summary>
public sealed class RejectingTwCategoryAdministrativeGrantStore : ITwCategoryAdministrativeGrantStore
{
    public Task<TwCategoryAdministrativeGrantEvidenceResult> GetAsync(
        TwCategoryAdministrativeAuthorizationRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new TwCategoryAdministrativeGrantEvidenceResult(null));

    public Task<TwCategoryAdministrativeGrantCreateResult> CreateAsync(
        TwCategoryAdministrativeGrantCreateRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new TwCategoryAdministrativeGrantCreateResult(
            TwCategoryAdministrativeGrantDurabilityDisposition.Unavailable,
            DiagnosticCode: "AdministrativeGrantStoreDisabled"));

    public Task<TwCategoryAdministrativeGrantLoadResult> LoadAsync(
        Guid grantId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new TwCategoryAdministrativeGrantLoadResult(
            TwCategoryAdministrativeGrantDurabilityDisposition.Unavailable,
            DiagnosticCode: "AdministrativeGrantStoreDisabled"));

    public Task<TwCategoryAdministrativeGrantConsumeResult> TryConsumeAsync(
        TwCategoryAdministrativeGrantConsumeRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new TwCategoryAdministrativeGrantConsumeResult(
            TwCategoryAdministrativeGrantDurabilityDisposition.Unavailable,
            DiagnosticCode: "AdministrativeGrantStoreDisabled"));

    public Task<TwCategoryAdministrativeGrantRevokeResult> RevokeAsync(
        TwCategoryAdministrativeGrantRevokeRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new TwCategoryAdministrativeGrantRevokeResult(
            TwCategoryAdministrativeGrantDurabilityDisposition.Unavailable,
            DiagnosticCode: "AdministrativeGrantStoreDisabled"));

    public Task<TwCategoryAdministrativeGrantExecutionOutcomeResult> RecordExecutionOutcomeAsync(
        TwCategoryAdministrativeGrantExecutionOutcomeRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new TwCategoryAdministrativeGrantExecutionOutcomeResult(
            TwCategoryAdministrativeGrantDurabilityDisposition.Unavailable,
            "AdministrativeGrantStoreDisabled"));

    public Task<TwCategoryAdministrativeGrantAuthorizationRejectionResult> RecordAuthorizationRejectionAsync(
        TwCategoryAdministrativeGrantAuthorizationRejectionRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new TwCategoryAdministrativeGrantAuthorizationRejectionResult(
            TwCategoryAdministrativeGrantDurabilityDisposition.Unavailable,
            "AdministrativeGrantStoreDisabled"));
}

#pragma warning restore CS1591
