using TightWiki.Plugin.Models;

namespace TightWiki.Engine.MediaWiki;

public interface ITwCategoryOperationalReadinessService
{
    TwCategoryOperationalReadinessSnapshot Evaluate(
        TwCategoryOperationalReadinessInput input,
        DateTime? evaluatedAtUtc = null);

    TwCategoryActivationReview EvaluateActivation(
        TwCategoryOperationalReadinessInput input);

    TwCategoryRollbackReview EvaluateRollback(
        TwCategoryOperationalReadinessSnapshot snapshot,
        bool explicitOperatorRequest);

    TwCategoryOperationalEvent CreateEvent(
        TwCategoryOperationalEventKind kind,
        TwCategoryOperationalEventOutcome outcome,
        TwCategoryOperationalReasonCode reasonCode,
        DateTime? occurredAtUtc = null);
}

/// <summary>
/// Pure, nonconnecting readiness and activation decision service. It evaluates
/// retained evidence only; it never resolves a provider, applies a schema, or
/// changes a registration.
/// </summary>
public sealed class TwCategoryOperationalReadinessService : ITwCategoryOperationalReadinessService
{
    public TwCategoryOperationalReadinessSnapshot Evaluate(
        TwCategoryOperationalReadinessInput input,
        DateTime? evaluatedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var reasons = CollectReasons(input);
        var state = DetermineState(input, reasons);
        var activation = EvaluateActivation(input);
        return new(
            NormalizeUtc(evaluatedAtUtc ?? DateTime.UtcNow),
            state,
            activation.Decision,
            input.EvidenceOrigin,
            reasons,
            !input.DatabaseContactedDuringEvaluation,
            activation.Decision == TwCategoryOperationalDecision.AllowedForReview && !activation.WouldMutate);
    }

    public TwCategoryActivationReview EvaluateActivation(TwCategoryOperationalReadinessInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var reasons = CollectReasons(input);
        if (!input.DatabaseContactedDuringEvaluation
            && input.EvidenceOrigin == TwCategoryOperationalEvidenceOrigin.SourceOnly
            && input.CategorySchema != TwCategoryOperationalEvidenceStatus.Verified)
        {
            // Source-only evaluation may recommend review only after the
            // actual provider/schema proofs have been retained separately.
            reasons = reasons.Append(TwCategoryOperationalReasonCode.CategorySchemaNotCertified).Distinct().ToArray();
        }

        var allowed = input.ActivationState == TwCategoryProviderActivationState.ExplicitActivationPending
            && input.Authorization == TwCategoryOperationalEvidenceStatus.Verified
            && input.TargetIsolation == TwCategoryOperationalEvidenceStatus.Verified
            && input.CategorySchema == TwCategoryOperationalEvidenceStatus.Verified
            && input.RecoverySchema == TwCategoryOperationalEvidenceStatus.Verified
            && input.CategoryProvider == TwCategoryOperationalEvidenceStatus.Verified
            && input.RecoveryProvider == TwCategoryOperationalEvidenceStatus.Verified
            && input.BrowseAuthorization == TwCategoryOperationalEvidenceStatus.Verified
            && input.RecoveryAuthorization == TwCategoryOperationalEvidenceStatus.Verified
            && input.ConcurrencyAndReplayCertification == TwCategoryOperationalEvidenceStatus.Verified
            && input.RollbackPlan == TwCategoryOperationalEvidenceStatus.Verified
            && input.ProductionApproval == TwCategoryOperationalEvidenceStatus.Verified
            && input.LeaseAssessment == TwCategoryOperationalEvidenceStatus.Verified
            && input.EvidenceOrigin != TwCategoryOperationalEvidenceOrigin.SourceOnly
            && !input.RecoveryIndeterminate;

        if (allowed)
            return new(TwCategoryOperationalDecision.AllowedForReview, [], false, true);
        if (input.ActivationState == TwCategoryProviderActivationState.ExplicitlyActivated
            && input.ProductionApproval != TwCategoryOperationalEvidenceStatus.Verified)
            reasons = reasons.Append(TwCategoryOperationalReasonCode.ProductionApprovalMissing).Distinct().ToArray();
        return new(
            input.DefaultsRejecting()
                ? TwCategoryOperationalDecision.RequiresMoreEvidence
                : TwCategoryOperationalDecision.Denied,
            reasons,
            false,
            true);
    }

    public TwCategoryRollbackReview EvaluateRollback(
        TwCategoryOperationalReadinessSnapshot snapshot,
        bool explicitOperatorRequest)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!explicitOperatorRequest)
            return new(TwCategoryRollbackAction.ManualReviewRequired, [TwCategoryOperationalReasonCode.OperatorActionRequired]);
        if (snapshot.Decision == TwCategoryOperationalDecision.AllowedForReview)
            return new(TwCategoryRollbackAction.RevertToRejectingAfterOperatorApproval, [TwCategoryOperationalReasonCode.OperatorActionRequired]);
        return new(TwCategoryRollbackAction.KeepRejecting, []);
    }

    public TwCategoryOperationalEvent CreateEvent(
        TwCategoryOperationalEventKind kind,
        TwCategoryOperationalEventOutcome outcome,
        TwCategoryOperationalReasonCode reasonCode,
        DateTime? occurredAtUtc = null)
        => new(NormalizeUtc(occurredAtUtc ?? DateTime.UtcNow), kind, outcome, reasonCode);

    private static IReadOnlyList<TwCategoryOperationalReasonCode> CollectReasons(TwCategoryOperationalReadinessInput input)
    {
        var reasons = new List<TwCategoryOperationalReasonCode>();
        if (input.EvidenceOrigin == TwCategoryOperationalEvidenceOrigin.SourceOnly)
            reasons.Add(TwCategoryOperationalReasonCode.RetainedCertificationEvidenceRequired);
        if (!input.DefaultsRejecting())
            reasons.Add(TwCategoryOperationalReasonCode.RejectingDefaultsRequired);
        if (input.ActivationState == TwCategoryProviderActivationState.RejectingDefaults)
            reasons.Add(TwCategoryOperationalReasonCode.ProviderNotActivated);
        AddStatusReason(input.Authorization, TwCategoryOperationalReasonCode.AuthorizationMissing, TwCategoryOperationalReasonCode.AuthorizationExpired, reasons);
        AddMissing(input.TargetIsolation, TwCategoryOperationalReasonCode.TargetIsolationUnverified, reasons);
        AddMissing(input.CategorySchema, TwCategoryOperationalReasonCode.CategorySchemaNotCertified, reasons);
        AddMissing(input.RecoverySchema, TwCategoryOperationalReasonCode.RecoverySchemaNotCertified, reasons);
        AddMissing(input.CategoryProvider, TwCategoryOperationalReasonCode.CategoryProviderUnavailable, reasons);
        AddMissing(input.RecoveryProvider, TwCategoryOperationalReasonCode.RecoveryProviderUnavailable, reasons);
        AddMissing(input.BrowseAuthorization, TwCategoryOperationalReasonCode.BrowseAuthorizationUnverified, reasons);
        AddMissing(input.RecoveryAuthorization, TwCategoryOperationalReasonCode.RecoveryAuthorizationUnverified, reasons);
        AddMissing(input.ConcurrencyAndReplayCertification, TwCategoryOperationalReasonCode.ConcurrencyReplayEvidenceMissing, reasons);
        AddMissing(input.RollbackPlan, TwCategoryOperationalReasonCode.RollbackPlanMissing, reasons);
        AddMissing(input.ProductionApproval, TwCategoryOperationalReasonCode.ProductionApprovalMissing, reasons);
        AddMissing(input.LeaseAssessment, TwCategoryOperationalReasonCode.LeaseAssessmentMissing, reasons);
        if (input.RecoveryIndeterminate)
            reasons.Add(TwCategoryOperationalReasonCode.RecoveryIndeterminate);
        return reasons.Distinct().ToArray();
    }

    private static TwCategoryOperationalReadinessState DetermineState(
        TwCategoryOperationalReadinessInput input,
        IReadOnlyList<TwCategoryOperationalReasonCode> reasons)
    {
        if (input.ActivationState == TwCategoryProviderActivationState.RejectingDefaults)
            return TwCategoryOperationalReadinessState.DisabledByDefault;
        if (input.Authorization is TwCategoryOperationalEvidenceStatus.Missing or TwCategoryOperationalEvidenceStatus.Expired)
            return TwCategoryOperationalReadinessState.AuthorizationMissingOrExpired;
        if (input.TargetIsolation != TwCategoryOperationalEvidenceStatus.Verified
            || input.CategorySchema != TwCategoryOperationalEvidenceStatus.Verified
            || input.RecoverySchema != TwCategoryOperationalEvidenceStatus.Verified
            || input.EvidenceOrigin == TwCategoryOperationalEvidenceOrigin.SourceOnly)
            return TwCategoryOperationalReadinessState.SchemaNotCertified;
        if (input.CategoryProvider is TwCategoryOperationalEvidenceStatus.Unavailable or TwCategoryOperationalEvidenceStatus.Unknown
            || input.RecoveryProvider is TwCategoryOperationalEvidenceStatus.Unavailable or TwCategoryOperationalEvidenceStatus.Unknown)
            return TwCategoryOperationalReadinessState.ProviderUnavailable;
        if (input.RecoveryIndeterminate)
            return TwCategoryOperationalReadinessState.RecoveryIndeterminate;

        var readOnlyReady = input.CategoryProvider == TwCategoryOperationalEvidenceStatus.Verified
            && input.BrowseAuthorization == TwCategoryOperationalEvidenceStatus.Verified;
        var recoveryReady = input.RecoveryProvider == TwCategoryOperationalEvidenceStatus.Verified
            && input.RecoveryAuthorization == TwCategoryOperationalEvidenceStatus.Verified
            && input.ConcurrencyAndReplayCertification == TwCategoryOperationalEvidenceStatus.Verified;
        if (readOnlyReady && !recoveryReady)
            return TwCategoryOperationalReadinessState.Degraded;
        if (readOnlyReady && reasons.Count > 0)
            return TwCategoryOperationalReadinessState.MutationGated;
        if (readOnlyReady)
            return TwCategoryOperationalReadinessState.ReadOnlyReady;
        return TwCategoryOperationalReadinessState.ReadyForOperatorReview;
    }

    private static void AddStatusReason(
        TwCategoryOperationalEvidenceStatus status,
        TwCategoryOperationalReasonCode missing,
        TwCategoryOperationalReasonCode expired,
        ICollection<TwCategoryOperationalReasonCode> reasons)
    {
        if (status == TwCategoryOperationalEvidenceStatus.Missing)
            reasons.Add(missing);
        else if (status == TwCategoryOperationalEvidenceStatus.Expired)
            reasons.Add(expired);
        else if (status != TwCategoryOperationalEvidenceStatus.Verified)
            reasons.Add(missing);
    }

    private static void AddMissing(
        TwCategoryOperationalEvidenceStatus status,
        TwCategoryOperationalReasonCode reason,
        ICollection<TwCategoryOperationalReasonCode> reasons)
    {
        if (status != TwCategoryOperationalEvidenceStatus.Verified)
            reasons.Add(reason);
    }

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}

file static class TwCategoryOperationalReadinessInputExtensions
{
    public static bool DefaultsRejecting(this TwCategoryOperationalReadinessInput input)
        => input.ActivationState != TwCategoryProviderActivationState.ExplicitlyActivated;
}
