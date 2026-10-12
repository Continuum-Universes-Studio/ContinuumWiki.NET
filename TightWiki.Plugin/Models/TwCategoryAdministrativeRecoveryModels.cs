namespace TightWiki.Plugin.Models;

#pragma warning disable CS1591

public enum TwCategoryAdministrativeApiDisposition
{
    Success,
    Disabled,
    Unauthenticated,
    Forbidden,
    InvalidInput,
    ProviderUnavailable,
    IncompleteExpansion,
    ConflictingRevision,
    InconsistentIndex,
    Cancellation,
    PartialResult,
    RecoveryIndeterminate,
    MutationGated,
    Unsupported,
    PersistenceFailure
}

public enum TwCategoryAdministrativeOperation
{
    Readiness,
    Preview,
    Execute
}

public enum TwCategoryAdministrativeAuthorizationReason
{
    Granted,
    MissingGrant,
    MalformedGrant,
    GrantNotYetValid,
    GrantExpired,
    GrantRevoked,
    GrantReplayed,
    ActorMismatch,
    ActionMismatch,
    ScopeMismatch,
    IssuerEvidenceUnavailable,
    ReplayProtectionUnavailable,
    CancellationRequested,
    ProviderUnavailable
}

public enum TwCategoryAdministrativeGrantRevocationState
{
    Active,
    Revoked
}

public enum TwCategoryAdministrativeGrantReplayMode
{
    ReusableWithinValidityWindow,
    SingleUse
}

/// <summary>
/// Server-derived caller context. Controllers must construct this only from
/// the authenticated application session and never from request JSON.
/// </summary>
public sealed record TwCategoryAdministrativeCaller(
    bool IsAuthenticated,
    bool IsAdministrator,
    Guid? ActorUserId)
{
    public bool IsTrustedAdministrator
        => IsAuthenticated && IsAdministrator && ActorUserId is not null && ActorUserId != Guid.Empty;
}

/// <summary>
/// The only client-supplied recovery fields. Actor, target database, revision,
/// approval, and authorization verdicts are intentionally absent.
/// </summary>
public sealed record TwCategoryAdministrativeRecoveryRequest(
    Guid? RequestId,
    IReadOnlyList<TwCategoryRecoveryTarget>? Targets);

public sealed record TwCategoryAdministrativeAuthorizationRequest(
    TwCategoryAdministrativeOperation Operation,
    Guid ActorUserId,
    TwCategoryRecoveryScope Scope);

/// <summary>
/// A server/provider-issued grant. It is never accepted from the HTTP request
/// body. The approval and issuer identifiers are stable evidence references,
/// not claims that an approval exists by themselves.
/// </summary>
public sealed record TwCategoryAdministrativeGrant(
    Guid GrantId,
    Guid ApprovalId,
    Guid ApproverUserId,
    Guid ActorUserId,
    TwCategoryAdministrativeOperation Operation,
    TwCategoryRecoveryScope Scope,
    DateTime IssuedAtUtc,
    DateTime NotBeforeUtc,
    DateTime ExpiresAtUtc,
    TwCategoryAdministrativeGrantRevocationState RevocationState,
    TwCategoryAdministrativeGrantReplayMode ReplayMode,
    string IssuerEvidenceReference);

public sealed record TwCategoryAdministrativeAuthorizationDecision(
    bool Allowed,
    TwCategoryAdministrativeAuthorizationReason Reason,
    Guid? GrantId = null,
    bool DurableReplayProtection = false);

public sealed record TwCategoryAdministrativeGrantEvidenceResult(
    TwCategoryAdministrativeGrant? Grant,
    bool ReplayAlreadyConsumed = false,
    bool DurableReplayProtectionAvailable = false);

public interface ITwCategoryAdministrativeGrantEvidenceSource
{
    Task<TwCategoryAdministrativeGrantEvidenceResult> GetAsync(
        TwCategoryAdministrativeAuthorizationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class RejectingTwCategoryAdministrativeGrantEvidenceSource
    : ITwCategoryAdministrativeGrantEvidenceSource
{
    public Task<TwCategoryAdministrativeGrantEvidenceResult> GetAsync(
        TwCategoryAdministrativeAuthorizationRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new TwCategoryAdministrativeGrantEvidenceResult(null));
}

/// <summary>
/// Separate operation-level authorization. The default implementation rejects
/// every operation; administrator authentication alone is never sufficient.
/// </summary>
public interface ITwCategoryAdministrativeGrantProvider
{
    Task<TwCategoryAdministrativeAuthorizationDecision> EvaluateAsync(
        TwCategoryAdministrativeAuthorizationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Compatibility name retained for the existing M5-022 DI seam.
/// </summary>
public interface ITwCategoryAdministrativeAuthorization : ITwCategoryAdministrativeGrantProvider
{
}

public sealed class RejectingTwCategoryAdministrativeAuthorization : ITwCategoryAdministrativeAuthorization
{
    public Task<TwCategoryAdministrativeAuthorizationDecision> EvaluateAsync(
        TwCategoryAdministrativeAuthorizationRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new TwCategoryAdministrativeAuthorizationDecision(
            false,
            cancellationToken.IsCancellationRequested
                ? TwCategoryAdministrativeAuthorizationReason.CancellationRequested
                : TwCategoryAdministrativeAuthorizationReason.MissingGrant));
}

public static class TwCategoryAdministrativeGrantVerifier
{
    public static TwCategoryAdministrativeAuthorizationDecision Evaluate(
        TwCategoryAdministrativeGrant? grant,
        TwCategoryAdministrativeAuthorizationRequest request,
        DateTime nowUtc,
        bool replayAlreadyConsumed = false,
        bool durableReplayProtectionAvailable = false)
    {
        if (grant is null)
            return Denied(TwCategoryAdministrativeAuthorizationReason.MissingGrant);
        if (request.ActorUserId == Guid.Empty || request.Scope is null)
            return Denied(TwCategoryAdministrativeAuthorizationReason.MalformedGrant);
        if (grant.GrantId == Guid.Empty
            || grant.ApprovalId == Guid.Empty
            || grant.ApproverUserId == Guid.Empty
            || grant.ActorUserId == Guid.Empty
            || string.IsNullOrWhiteSpace(grant.IssuerEvidenceReference)
            || grant.IssuedAtUtc.Kind != DateTimeKind.Utc
            || grant.NotBeforeUtc.Kind != DateTimeKind.Utc
            || grant.ExpiresAtUtc.Kind != DateTimeKind.Utc
            || grant.NotBeforeUtc >= grant.ExpiresAtUtc
            || grant.IssuedAtUtc > grant.ExpiresAtUtc)
            return Denied(TwCategoryAdministrativeAuthorizationReason.MalformedGrant);
        if (grant.RevocationState == TwCategoryAdministrativeGrantRevocationState.Revoked)
            return Denied(TwCategoryAdministrativeAuthorizationReason.GrantRevoked, grant.GrantId);
        if (nowUtc.Kind != DateTimeKind.Utc)
            return Denied(TwCategoryAdministrativeAuthorizationReason.MalformedGrant, grant.GrantId);
        if (nowUtc < grant.NotBeforeUtc)
            return Denied(TwCategoryAdministrativeAuthorizationReason.GrantNotYetValid, grant.GrantId);
        if (nowUtc >= grant.ExpiresAtUtc)
            return Denied(TwCategoryAdministrativeAuthorizationReason.GrantExpired, grant.GrantId);
        if (grant.ActorUserId != request.ActorUserId)
            return Denied(TwCategoryAdministrativeAuthorizationReason.ActorMismatch, grant.GrantId);
        if (grant.Operation != request.Operation)
            return Denied(TwCategoryAdministrativeAuthorizationReason.ActionMismatch, grant.GrantId);
        if (!string.Equals(grant.Scope.PolicyVersion, request.Scope.PolicyVersion, StringComparison.Ordinal)
            || !string.Equals(grant.Scope.Fingerprint, request.Scope.Fingerprint, StringComparison.Ordinal))
            return Denied(TwCategoryAdministrativeAuthorizationReason.ScopeMismatch, grant.GrantId);
        if (replayAlreadyConsumed)
            return Denied(TwCategoryAdministrativeAuthorizationReason.GrantReplayed, grant.GrantId, durableReplayProtectionAvailable);
        if (grant.ReplayMode == TwCategoryAdministrativeGrantReplayMode.SingleUse
            && !durableReplayProtectionAvailable)
            return Denied(TwCategoryAdministrativeAuthorizationReason.ReplayProtectionUnavailable, grant.GrantId);

        return new(true, TwCategoryAdministrativeAuthorizationReason.Granted, grant.GrantId, durableReplayProtectionAvailable);
    }

    private static TwCategoryAdministrativeAuthorizationDecision Denied(
        TwCategoryAdministrativeAuthorizationReason reason,
        Guid? grantId = null,
        bool durableReplayProtection = false)
        => new(false, reason, grantId, durableReplayProtection);
}

/// <summary>
/// Retained readiness evidence is supplied by an explicit adapter. The default
/// source is source-only and never probes a database or provider.
/// </summary>
public interface ITwCategoryOperationalReadinessEvidenceSource
{
    TwCategoryOperationalReadinessInput Get();

    TwCategoryOperationalReadinessEvidence GetEvidence()
        => TwCategoryOperationalReadinessEvidence.SourceOnly(Get());
}

public enum TwCategoryOperationalReadinessEvidenceProvenance
{
    SourceOnly,
    OperatorReadinessProof,
    DisposableCertification,
    ProductionDeployment
}

public sealed record TwCategoryOperationalReadinessEvidence(
    TwCategoryOperationalReadinessInput Input,
    TwCategoryOperationalReadinessEvidenceProvenance Provenance,
    Guid? EvidenceId,
    DateTime? ObservedAtUtc,
    DateTime? ExpiresAtUtc,
    bool TargetBound,
    bool IndependentlySourced,
    string? TargetBindingHash)
{
    public static TwCategoryOperationalReadinessEvidence SourceOnly(
        TwCategoryOperationalReadinessInput input)
        => new(input, TwCategoryOperationalReadinessEvidenceProvenance.SourceOnly, null, null, null, false, false, null);
}

public enum TwCategoryOperationalReadinessEvidenceDecision
{
    Accepted,
    Missing,
    SourceOnly,
    NotTargetBound,
    NotIndependentlySourced,
    WrongDeployment,
    Malformed,
    Expired
}

public sealed record TwCategoryOperationalReadinessEvidenceValidation(
    TwCategoryOperationalReadinessEvidenceDecision Decision,
    string DiagnosticCode)
{
    public bool Accepted => Decision == TwCategoryOperationalReadinessEvidenceDecision.Accepted;
}

public static class TwCategoryOperationalReadinessEvidencePolicy
{
    public static TwCategoryOperationalReadinessEvidenceValidation ValidateForAdministrativeExecution(
        TwCategoryOperationalReadinessEvidence? evidence,
        DateTime nowUtc)
    {
        if (evidence is null)
            return new(TwCategoryOperationalReadinessEvidenceDecision.Missing, "ReadinessEvidenceMissing");
        if (evidence.Provenance == TwCategoryOperationalReadinessEvidenceProvenance.SourceOnly)
            return new(TwCategoryOperationalReadinessEvidenceDecision.SourceOnly, "SourceOnlyReadinessEvidence");
        if (evidence.Provenance != TwCategoryOperationalReadinessEvidenceProvenance.ProductionDeployment)
            return new(TwCategoryOperationalReadinessEvidenceDecision.WrongDeployment, "ProductionReadinessEvidenceRequired");
        if (!evidence.TargetBound)
            return new(TwCategoryOperationalReadinessEvidenceDecision.NotTargetBound, "TargetBoundReadinessEvidenceRequired");
        if (!evidence.IndependentlySourced)
            return new(TwCategoryOperationalReadinessEvidenceDecision.NotIndependentlySourced, "IndependentReadinessEvidenceRequired");
        if (evidence.EvidenceId is null || evidence.EvidenceId == Guid.Empty
            || string.IsNullOrWhiteSpace(evidence.TargetBindingHash)
            || evidence.ObservedAtUtc?.Kind != DateTimeKind.Utc
            || evidence.ExpiresAtUtc?.Kind != DateTimeKind.Utc
            || evidence.ObservedAtUtc > evidence.ExpiresAtUtc
            || nowUtc.Kind != DateTimeKind.Utc)
            return new(TwCategoryOperationalReadinessEvidenceDecision.Malformed, "MalformedReadinessEvidence");
        if (nowUtc >= evidence.ExpiresAtUtc)
            return new(TwCategoryOperationalReadinessEvidenceDecision.Expired, "ReadinessEvidenceExpired");
        return new(TwCategoryOperationalReadinessEvidenceDecision.Accepted, "ProductionReadinessEvidenceAccepted");
    }
}

public sealed class RejectingTwCategoryOperationalReadinessEvidenceSource
    : ITwCategoryOperationalReadinessEvidenceSource
{
    public TwCategoryOperationalReadinessInput Get()
        => new(
            TwCategoryProviderActivationState.RejectingDefaults,
            TwCategoryOperationalEvidenceStatus.Missing,
            TwCategoryOperationalEvidenceStatus.Missing,
            TwCategoryOperationalEvidenceStatus.Missing,
            TwCategoryOperationalEvidenceStatus.Missing,
            TwCategoryOperationalEvidenceStatus.Unavailable,
            TwCategoryOperationalEvidenceStatus.Unavailable,
            TwCategoryOperationalEvidenceStatus.Missing,
            TwCategoryOperationalEvidenceStatus.Missing,
            TwCategoryOperationalEvidenceStatus.Missing,
            TwCategoryOperationalEvidenceStatus.Missing,
            TwCategoryOperationalEvidenceStatus.Missing,
            TwCategoryOperationalEvidenceStatus.Missing,
            TwCategoryOperationalEvidenceOrigin.SourceOnly,
            RecoveryIndeterminate: false,
            DatabaseContactedDuringEvaluation: false);
}

public sealed record TwCategoryAdministrativeReadinessResponse(
    TwCategoryAdministrativeApiDisposition Disposition,
    TwCategoryOperationalReadinessSnapshot? Readiness = null,
    string? DiagnosticCode = null);

public sealed record TwCategoryAdministrativePlanResponse(
    TwCategoryAdministrativeApiDisposition Disposition,
    TwCategoryRecoveryPlan? Plan = null,
    string? DiagnosticCode = null);

public sealed record TwCategoryAdministrativeExecutionResponse(
    TwCategoryAdministrativeApiDisposition Disposition,
    TwCategoryDurableRecoveryExecutionResult? Execution = null,
    string? DiagnosticCode = null);

#pragma warning restore CS1591
