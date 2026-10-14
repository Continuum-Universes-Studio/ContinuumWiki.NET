using TightWiki.Plugin.Models;
using TightWiki.Plugin.Interfaces.Persistence;

namespace TightWiki.Engine.MediaWiki;

/// <summary>
/// Verifies grants obtained from an independently controlled evidence source.
/// It does not accept grants from request JSON and does not pretend that an
/// in-memory lookup provides atomic single-use or revocation semantics.
/// </summary>
public sealed class TwCategoryAdministrativeGrantProvider(
    ITwCategoryAdministrativeGrantEvidenceSource evidenceSource,
    ITwCategoryAdministrativeGrantStore? grantStore = null)
    : ITwCategoryAdministrativeGrantProvider, ITwCategoryAdministrativeGrantOutcomeRecorder
{
    public async Task<TwCategoryAdministrativeAuthorizationDecision> EvaluateAsync(
        TwCategoryAdministrativeAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return new(false, TwCategoryAdministrativeAuthorizationReason.CancellationRequested);

        try
        {
            var evidence = await evidenceSource.GetAsync(request, cancellationToken);
            if (!string.IsNullOrWhiteSpace(evidence.DiagnosticCode))
                return new(false, TwCategoryAdministrativeAuthorizationReason.ProviderUnavailable);
            if (evidence.Grant is null)
                return TwCategoryAdministrativeGrantVerifier.Evaluate(
                    null,
                    request,
                    TimeProvider.System.GetUtcNow().UtcDateTime,
                    evidence.ReplayAlreadyConsumed,
                    evidence.DurableReplayProtectionAvailable);
            var grant = evidence.Grant;

            var decision = TwCategoryAdministrativeGrantVerifier.Evaluate(
                grant,
                request,
                TimeProvider.System.GetUtcNow().UtcDateTime,
                evidence.ReplayAlreadyConsumed,
                evidence.DurableReplayProtectionAvailable);
            if (!decision.Allowed && grantStore is not null
                && request.CorrelationId is Guid rejectionCorrelationId && rejectionCorrelationId != Guid.Empty)
            {
                var rejection = await RecordAuthorizationRejectionAsync(new(
                    grant.GrantId,
                    request.ActorUserId,
                    request.Operation,
                    rejectionCorrelationId,
                    decision.Reason.ToString(),
                    DateTime.UtcNow), cancellationToken);
                if (rejection.Disposition is not TwCategoryAdministrativeGrantDurabilityDisposition.OutcomeRecorded
                    and not TwCategoryAdministrativeGrantDurabilityDisposition.DuplicateSame)
                    return new(false, TwCategoryAdministrativeAuthorizationReason.ProviderUnavailable, decision.GrantId, decision.DurableReplayProtection);
            }
            if (!decision.Allowed)
                return decision;
            if (request.Operation != TwCategoryAdministrativeOperation.Execute
                || grant.ReplayMode != TwCategoryAdministrativeGrantReplayMode.SingleUse)
                return decision;
            if (grantStore is null || evidence.Version is null || request.CorrelationId is null || request.CorrelationId == Guid.Empty)
                return new(false, TwCategoryAdministrativeAuthorizationReason.ProviderUnavailable, decision.GrantId);

            var consumed = await grantStore.TryConsumeAsync(new(
                grant.GrantId,
                evidence.Version.Value,
                request.ActorUserId,
                request.Operation,
                request.Scope,
                request.CorrelationId.Value,
                DateTime.UtcNow), cancellationToken);
            return consumed.Disposition switch
            {
                TwCategoryAdministrativeGrantDurabilityDisposition.Consumed
                    => decision with { DurableReplayProtection = true },
                TwCategoryAdministrativeGrantDurabilityDisposition.AlreadyConsumed
                    => new(false, TwCategoryAdministrativeAuthorizationReason.GrantReplayed, decision.GrantId, true),
                TwCategoryAdministrativeGrantDurabilityDisposition.Revoked
                    => new(false, TwCategoryAdministrativeAuthorizationReason.GrantRevoked, decision.GrantId, true),
                TwCategoryAdministrativeGrantDurabilityDisposition.InvalidRequest when consumed.DiagnosticCode == "GrantExpired"
                    => new(false, TwCategoryAdministrativeAuthorizationReason.GrantExpired, decision.GrantId, true),
                _ => new(false, TwCategoryAdministrativeAuthorizationReason.ProviderUnavailable, decision.GrantId)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(false, TwCategoryAdministrativeAuthorizationReason.CancellationRequested);
        }
        catch
        {
            return new(false, TwCategoryAdministrativeAuthorizationReason.ProviderUnavailable);
        }
    }

    public Task<TwCategoryAdministrativeGrantExecutionOutcomeResult> RecordExecutionOutcomeAsync(
        TwCategoryAdministrativeGrantExecutionOutcomeRequest request,
        CancellationToken cancellationToken = default)
        => grantStore is null
            ? Task.FromResult(new TwCategoryAdministrativeGrantExecutionOutcomeResult(
                TwCategoryAdministrativeGrantDurabilityDisposition.Unavailable,
                "AdministrativeGrantOutcomeRecorderUnavailable"))
            : grantStore.RecordExecutionOutcomeAsync(request, cancellationToken);

    public Task<TwCategoryAdministrativeGrantAuthorizationRejectionResult> RecordAuthorizationRejectionAsync(
        TwCategoryAdministrativeGrantAuthorizationRejectionRequest request,
        CancellationToken cancellationToken = default)
        => grantStore is null
            ? Task.FromResult(new TwCategoryAdministrativeGrantAuthorizationRejectionResult(
                TwCategoryAdministrativeGrantDurabilityDisposition.Unavailable,
                "AdministrativeGrantRejectionRecorderUnavailable"))
            : grantStore.RecordAuthorizationRejectionAsync(request, cancellationToken);
}
