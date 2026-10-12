namespace TightWiki.Plugin.Models;

#pragma warning disable CS1591

public enum TwCategoryOperationalReadinessState
{
    DisabledByDefault,
    AuthorizationMissingOrExpired,
    SchemaNotCertified,
    ProviderUnavailable,
    ReadOnlyReady,
    MutationGated,
    RecoveryIndeterminate,
    Degraded,
    ReadyForOperatorReview
}

public enum TwCategoryOperationalDecision
{
    Denied,
    RequiresMoreEvidence,
    AllowedForReview
}

public enum TwCategoryOperationalEvidenceStatus
{
    Missing,
    Verified,
    Unavailable,
    Expired,
    Unknown
}

public enum TwCategoryOperationalEvidenceOrigin
{
    SourceOnly,
    OperatorReadinessProof,
    DisposableCertification,
    ProductionApproval
}

public enum TwCategoryProviderActivationState
{
    RejectingDefaults,
    ExplicitActivationPending,
    ExplicitlyActivated
}

public enum TwCategoryOperationalReasonCode
{
    RejectingDefaultsRequired,
    ProviderNotActivated,
    AuthorizationMissing,
    AuthorizationExpired,
    TargetIsolationUnverified,
    CategorySchemaNotCertified,
    RecoverySchemaNotCertified,
    CategoryProviderUnavailable,
    RecoveryProviderUnavailable,
    BrowseAuthorizationUnverified,
    RecoveryAuthorizationUnverified,
    ConcurrencyReplayEvidenceMissing,
    RecoveryIndeterminate,
    RollbackPlanMissing,
    ProductionApprovalMissing,
    LeaseAssessmentMissing,
    RetainedCertificationEvidenceRequired,
    OperatorActionRequired
}

public enum TwCategoryOperationalEventKind
{
    ReadinessEvaluated,
    RecoveryPreviewed,
    RecoveryExecutionAttempted,
    RecoveryCommitted,
    RecoveryIndeterminate,
    RecoveryRejected,
    RollbackReviewed
}

public enum TwCategoryOperationalEventOutcome
{
    Attempted,
    ConfirmedCommit,
    Indeterminate,
    Rejected
}

/// <summary>
/// Non-secret evidence supplied to the pure readiness evaluator. It describes
/// retained proofs; constructing it never probes or mutates a provider.
/// </summary>
public sealed record TwCategoryOperationalReadinessInput(
    TwCategoryProviderActivationState ActivationState,
    TwCategoryOperationalEvidenceStatus Authorization,
    TwCategoryOperationalEvidenceStatus TargetIsolation,
    TwCategoryOperationalEvidenceStatus CategorySchema,
    TwCategoryOperationalEvidenceStatus RecoverySchema,
    TwCategoryOperationalEvidenceStatus CategoryProvider,
    TwCategoryOperationalEvidenceStatus RecoveryProvider,
    TwCategoryOperationalEvidenceStatus BrowseAuthorization,
    TwCategoryOperationalEvidenceStatus RecoveryAuthorization,
    TwCategoryOperationalEvidenceStatus ConcurrencyAndReplayCertification,
    TwCategoryOperationalEvidenceStatus RollbackPlan,
    TwCategoryOperationalEvidenceStatus ProductionApproval,
    TwCategoryOperationalEvidenceStatus LeaseAssessment,
    TwCategoryOperationalEvidenceOrigin EvidenceOrigin,
    bool RecoveryIndeterminate = false,
    bool DatabaseContactedDuringEvaluation = false);

public sealed record TwCategoryOperationalReadinessSnapshot(
    DateTime EvaluatedAtUtc,
    TwCategoryOperationalReadinessState State,
    TwCategoryOperationalDecision Decision,
    TwCategoryOperationalEvidenceOrigin EvidenceOrigin,
    IReadOnlyList<TwCategoryOperationalReasonCode> Reasons,
    bool DeterminedWithoutDatabaseConnection,
    bool EligibleForReviewOnly,
    bool PublicSafe = true);

public sealed record TwCategoryActivationReview(
    TwCategoryOperationalDecision Decision,
    IReadOnlyList<TwCategoryOperationalReasonCode> Reasons,
    bool WouldMutate = false,
    bool RequiresIndependentOperatorAction = true);

public enum TwCategoryRollbackAction
{
    KeepRejecting,
    RevertToRejectingAfterOperatorApproval,
    ManualReviewRequired
}

public sealed record TwCategoryRollbackReview(
    TwCategoryRollbackAction Action,
    IReadOnlyList<TwCategoryOperationalReasonCode> Reasons,
    bool WouldMutate = false);

/// <summary>
/// Bounded operational event data. It intentionally has no page, category,
/// revision, scope, credential, or connection-string fields.
/// </summary>
public sealed record TwCategoryOperationalEvent(
    DateTime OccurredAtUtc,
    TwCategoryOperationalEventKind Kind,
    TwCategoryOperationalEventOutcome Outcome,
    TwCategoryOperationalReasonCode ReasonCode);

public enum TwCategoryRecoveryAdministrationMode
{
    Preview,
    Execute
}

/// <summary>
/// Safe administrative contract only. No controller or route consumes this
/// type. Execute requests still require the existing per-operation and
/// per-page authorization callbacks at the recovery service boundary.
/// </summary>
public sealed record TwCategoryRecoveryAdministrativeRequest(
    Guid RequestId,
    IReadOnlyList<TwCategoryRecoveryTarget> Targets,
    Guid? ActorUserId,
    TwCategoryRecoveryAdministrationMode Mode)
{
    public bool IsPreview => Mode == TwCategoryRecoveryAdministrationMode.Preview;
    public bool RequiresFreshAuthorization => Mode == TwCategoryRecoveryAdministrationMode.Execute;
    public bool MutationRequested => Mode == TwCategoryRecoveryAdministrationMode.Execute;

    public TwCategoryRecoveryPlanRequest ToPlanRequest()
        => new(Targets, ActorUserId);

    public TwCategoryRecoveryDurableExecutionRequest ToDurableExecutionRequest()
    {
        if (Mode != TwCategoryRecoveryAdministrationMode.Execute)
            throw new InvalidOperationException("A preview request cannot be converted to durable execution.");
        return new(RequestId, Targets, ActorUserId);
    }
}

public static class TwCategoryRecoveryAdministrativePolicy
{
    public static string? Validate(TwCategoryRecoveryAdministrativeRequest? request)
    {
        if (request is null || request.RequestId == Guid.Empty)
            return "InvalidAdministrativeRecoveryRequest";
        if (request.Targets is null)
            return "InvalidAdministrativeRecoveryTargets";
        if (!TwCategoryRecoveryScope.TryCreate(request.Targets, out _, out var diagnosticCode))
            return string.IsNullOrWhiteSpace(diagnosticCode) ? "InvalidAdministrativeRecoveryTargets" : diagnosticCode;
        return null;
    }
}

#pragma warning restore CS1591
