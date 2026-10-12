using TightWiki.Plugin.Models;

namespace TightWiki.Plugin.Interfaces.Persistence;

#pragma warning disable CS1591

/// <summary>
/// Durable-ready category recovery boundary. Implementations must provide
/// immutable operation identity/scope/actor provenance, idempotent event keys, optimistic
/// checkpoint advancement, and a read projection that does not disclose page
/// identities without a separately authorized projection.
/// </summary>
public interface ITwCategoryRecoveryDurabilityStore
{
    Task<TwCategoryRecoveryCreateOperationResult> CreateOrReplayOperationAsync(
        TwCategoryRecoveryOperationRecord requested,
        CancellationToken cancellationToken = default);

    Task<TwCategoryRecoveryLoadOperationResult> LoadOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken = default);

    Task<TwCategoryRecoveryAppendAuditResult> AppendAuditEventAsync(
        TwCategoryRecoveryAuditEvent auditEvent,
        CancellationToken cancellationToken = default);

    Task<TwCategoryRecoveryAdvanceCheckpointResult> AdvanceCheckpointAsync(
        TwCategoryRecoveryCheckpointAdvanceRequest request,
        CancellationToken cancellationToken = default);

    Task<TwCategoryRecoveryTransitionOperationResult> TransitionOperationAsync(
        TwCategoryRecoveryTransitionOperationRequest request,
        CancellationToken cancellationToken = default);

    Task<TwCategoryRecoveryProgressResult> ReadScopedProgressAsync(
        TwCategoryRecoveryProgressRequest request,
        CancellationToken cancellationToken = default);
}

#pragma warning restore CS1591
