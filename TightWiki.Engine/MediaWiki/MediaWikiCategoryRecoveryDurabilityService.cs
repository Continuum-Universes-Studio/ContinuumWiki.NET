using Microsoft.Extensions.Logging;
using TightWiki.Plugin.Interfaces.Persistence;
using TightWiki.Plugin.Models;

namespace TightWiki.Engine.MediaWiki;

public interface ITwCategoryDurableRecoveryService
{
    Task<TwCategoryDurableRecoveryExecutionResult> ExecuteAsync(
        TwCategoryRecoveryDurableExecutionRequest request,
        Func<TwCategoryRecoveryOperationAuthorizationContext, CancellationToken, Task<bool>>? authorizeOperation,
        Func<TwCategoryRecoveryPageAuthorizationContext, CancellationToken, Task<bool>>? authorizePage,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Test-only interruption points for the recoverable two-phase coordinator.
/// These boundaries simulate an application interruption; they do not kill an
/// operating-system process, container, or MariaDB server.
/// </summary>
public enum TwCategoryRecoveryFaultBoundary
{
    BeforeCategoryMutation,
    AfterCategoryMutationBeforeAudit,
    AfterAuditBeforeCheckpoint,
    AfterCheckpointCommitBeforeAcknowledgement
}

public interface ITwCategoryRecoveryFaultInjector
{
    Task InjectAsync(
        TwCategoryRecoveryFaultBoundary boundary,
        Guid operationId,
        int position,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Coordinates a recoverable two-phase recovery flow around the existing
/// category reconciliation service. The category mutation and durability
/// store are intentionally separate transactions; an uncertain page is never
/// advanced past in the checkpoint and is replayed through idempotent
/// reconciliation after resume.
/// </summary>
public sealed class TwCategoryDurableRecoveryService(
    ITwCategoryRecoveryService recoveryService,
    ITwCategoryRecoveryDurabilityStore durabilityStore,
    ILogger<TwCategoryDurableRecoveryService> logger,
    ITwCategoryRecoveryFaultInjector? faultInjector = null) : ITwCategoryDurableRecoveryService
{
    public async Task<TwCategoryDurableRecoveryExecutionResult> ExecuteAsync(
        TwCategoryRecoveryDurableExecutionRequest request,
        Func<TwCategoryRecoveryOperationAuthorizationContext, CancellationToken, Task<bool>>? authorizeOperation,
        Func<TwCategoryRecoveryPageAuthorizationContext, CancellationToken, Task<bool>>? authorizePage,
        CancellationToken cancellationToken = default)
    {
        if (request is null || request.OperationId == Guid.Empty)
            return Invalid(request?.OperationId ?? Guid.Empty, "InvalidDurableRecoveryRequest");
        if (!TwCategoryRecoveryScope.TryCreate(request.Targets, out var scope, out var validationCode))
            return Invalid(request.OperationId, validationCode.Length == 0 ? "InvalidDurableRecoveryRequest" : validationCode);
        if (authorizeOperation is null || authorizePage is null)
            return new(TwCategoryDurableRecoveryDisposition.Unauthorized, request.OperationId, [], DiagnosticCode: "RecoveryAuthorizationRequired");

        if (cancellationToken.IsCancellationRequested)
            return new(TwCategoryDurableRecoveryDisposition.Cancelled, request.OperationId, [], DiagnosticCode: "CancellationRequested");
        if (!await authorizeOperation(new(TwCategoryRecoveryOperation.Execute, request.ActorUserId), cancellationToken))
            return new(TwCategoryDurableRecoveryDisposition.Unauthorized, request.OperationId, [], DiagnosticCode: "RecoveryAuthorizationRejected");

        var now = DateTime.UtcNow;
        var requested = new TwCategoryRecoveryOperationRecord(
            request.OperationId,
            scope,
            request.ActorUserId,
            now,
            TwCategoryRecoveryDurabilityConstants.PolicyVersion,
            TwCategoryRecoveryOperationStatus.Planned,
            new(request.OperationId, scope.Fingerprint, 0, 0, 0));
        var opened = await durabilityStore.CreateOrReplayOperationAsync(requested, cancellationToken);
        if (opened.Disposition == TwCategoryRecoveryDurabilityDisposition.Unavailable)
            return new(TwCategoryDurableRecoveryDisposition.Unavailable, request.OperationId, [], DiagnosticCode: opened.DiagnosticCode ?? "RecoveryDurabilityUnavailable");
        if (opened.Disposition is TwCategoryRecoveryDurabilityDisposition.ScopeConflict or TwCategoryRecoveryDurabilityDisposition.DuplicateConflict)
            return new(TwCategoryDurableRecoveryDisposition.ScopeConflict, request.OperationId, [], DiagnosticCode: opened.DiagnosticCode ?? "RecoveryScopeConflict");
        if (opened.Disposition == TwCategoryRecoveryDurabilityDisposition.IncompatibleVersion)
            return new(TwCategoryDurableRecoveryDisposition.IncompatibleVersion, request.OperationId, [], DiagnosticCode: opened.DiagnosticCode ?? "RecoveryDurabilityVersionUnsupported");
        if (opened.Operation is null)
            return new(TwCategoryDurableRecoveryDisposition.Unavailable, request.OperationId, [], DiagnosticCode: opened.DiagnosticCode ?? "RecoveryOperationUnavailable");

        var operation = opened.Operation;
        if (opened.Disposition == TwCategoryRecoveryDurabilityDisposition.Replayed)
        {
            var loaded = await durabilityStore.LoadOperationAsync(request.OperationId, cancellationToken);
            if (loaded.Disposition != TwCategoryRecoveryDurabilityDisposition.Loaded || loaded.Operation is null)
                return new(TwCategoryDurableRecoveryDisposition.Unavailable, request.OperationId, [], DiagnosticCode: loaded.DiagnosticCode ?? "RecoveryOperationLoadFailed");
            operation = loaded.Operation;
        }
        if (operation.PolicyVersion != TwCategoryRecoveryDurabilityConstants.PolicyVersion
            || operation.Checkpoint.SchemaVersion != TwCategoryRecoveryDurabilityConstants.CurrentSchemaVersion)
            return new(TwCategoryDurableRecoveryDisposition.IncompatibleVersion, request.OperationId, [], operation.Checkpoint, operation.Status, "RecoveryDurabilityVersionUnsupported");
        if (!string.Equals(operation.Scope.Fingerprint, scope.Fingerprint, StringComparison.Ordinal))
            return new(TwCategoryDurableRecoveryDisposition.ScopeConflict, request.OperationId, [], operation.Checkpoint, operation.Status, "RecoveryScopeConflict");
        if (operation.ActorUserId != request.ActorUserId)
            return new(TwCategoryDurableRecoveryDisposition.Unauthorized, request.OperationId, [], operation.Checkpoint, operation.Status, "RecoveryActorConflict");
        if (request.ExpectedCheckpoint is not null && !EquivalentCheckpoint(request.ExpectedCheckpoint, operation.Checkpoint))
            return new(TwCategoryDurableRecoveryDisposition.GenerationConflict, request.OperationId, [], operation.Checkpoint, operation.Status, "ExpectedCheckpointConflict");

        if (operation.Status == TwCategoryRecoveryOperationStatus.Completed)
            return new(TwCategoryDurableRecoveryDisposition.AlreadyCompleted, request.OperationId, [], operation.Checkpoint, operation.Status, "RecoveryOperationAlreadyCompleted");
        if (operation.Status is TwCategoryRecoveryOperationStatus.Failed or TwCategoryRecoveryOperationStatus.Cancelled)
            return new(MapTerminal(operation.Status), request.OperationId, [], operation.Checkpoint, operation.Status, "RecoveryOperationTerminal");

        if (opened.Disposition == TwCategoryRecoveryDurabilityDisposition.Replayed)
        {
            var resumed = await AppendAsync(new(
                Guid.NewGuid(),
                $"{operation.OperationId:N}:resume:{operation.Checkpoint.Generation}:{operation.Checkpoint.CommittedPosition}",
                operation.OperationId,
                operation.Checkpoint.CommittedPosition,
                Guid.Empty,
                TwCategoryRecoveryAuditEventKind.Resumed,
                TwCategoryRecoveryAuditConfidence.Observation,
                null,
                null,
                null,
                request.ActorUserId,
                DateTime.UtcNow), cancellationToken);
            if (resumed.Disposition == TwCategoryRecoveryDurabilityDisposition.Unavailable)
                return new(TwCategoryDurableRecoveryDisposition.Unavailable, request.OperationId, [], operation.Checkpoint, operation.Status, "RecoveryResumeAuditUnavailable");
        }

        // A caller can disappear after the operation has durably entered
        // Running. A new coordinator instance must resume that state rather
        // than attempting the invalid Running -> Running transition.
        if (operation.Status != TwCategoryRecoveryOperationStatus.Running)
        {
            var transition = await TransitionAsync(operation, TwCategoryRecoveryOperationStatus.Running, null, cancellationToken);
            if (transition.Disposition != TwCategoryRecoveryDurabilityDisposition.Finalized || transition.Operation is null)
                return MapDurabilityFailure(request.OperationId, operation, transition);
            operation = transition.Operation;
        }

        var results = new List<TwCategoryRecoveryDurablePageResult>();
        for (var position = operation.Checkpoint.CommittedPosition; position < scope.Targets.Count; position++)
        {
            if (cancellationToken.IsCancellationRequested)
                return await InterruptAsync(operation, results, "CancellationRequested", cancellationToken);

            var target = scope.Targets[position];
            var work = new TwCategoryRecoveryPageWorkIdentity(operation.OperationId, position, target.PageId, target.ExpectedNavigation, 1, Guid.NewGuid());
            var intent = await AppendAsync(new(
                Guid.NewGuid(),
                $"{operation.OperationId:N}:intent:{position}:{work.AttemptId:N}",
                operation.OperationId,
                position,
                work.AttemptId,
                TwCategoryRecoveryAuditEventKind.IntentRequested,
                TwCategoryRecoveryAuditConfidence.Intent,
                target.PageId,
                null,
                null,
                request.ActorUserId,
                DateTime.UtcNow), cancellationToken);
            if (intent.Disposition is TwCategoryRecoveryDurabilityDisposition.Unavailable or TwCategoryRecoveryDurabilityDisposition.DuplicateConflict)
                return await RecoverableAsync(operation, results, "RecoveryIntentNotRecorded", cancellationToken);

            var auditUnavailable = false;
            async Task<bool> AuthorizePage(TwCategoryRecoveryPageAuthorizationContext context, CancellationToken token)
            {
                var authorized = await authorizePage(context, token);
                var resolved = await AppendAsync(new(
                    Guid.NewGuid(),
                    $"{operation.OperationId:N}:revision:{position}:{work.AttemptId:N}",
                    operation.OperationId,
                    position,
                    work.AttemptId,
                    TwCategoryRecoveryAuditEventKind.RevisionResolved,
                    TwCategoryRecoveryAuditConfidence.Observation,
                    context.PageId,
                    context.CurrentRevision,
                    null,
                    request.ActorUserId,
                    DateTime.UtcNow), token);
                var decision = await AppendAsync(new(
                    Guid.NewGuid(),
                    $"{operation.OperationId:N}:authorization:{position}:{work.AttemptId:N}",
                    operation.OperationId,
                    position,
                    work.AttemptId,
                    TwCategoryRecoveryAuditEventKind.AuthorizationDecision,
                    authorized ? TwCategoryRecoveryAuditConfidence.Observation : TwCategoryRecoveryAuditConfidence.Attempted,
                    context.PageId,
                    context.CurrentRevision,
                    authorized ? null : TwCategoryRecoveryExecutionDisposition.Unauthorized,
                    request.ActorUserId,
                    DateTime.UtcNow,
                    authorized ? null : "PageAuthorizationRejected"), token);
                if (resolved.Disposition == TwCategoryRecoveryDurabilityDisposition.Unavailable
                    || decision.Disposition == TwCategoryRecoveryDurabilityDisposition.Unavailable)
                    auditUnavailable = true;
                return authorized && !auditUnavailable;
            }

            var attempted = await AppendAsync(new(
                Guid.NewGuid(),
                $"{operation.OperationId:N}:attempt:{position}:{work.AttemptId:N}",
                operation.OperationId,
                position,
                work.AttemptId,
                TwCategoryRecoveryAuditEventKind.ReconciliationAttempted,
                TwCategoryRecoveryAuditConfidence.Attempted,
                target.PageId,
                null,
                null,
                request.ActorUserId,
                DateTime.UtcNow), cancellationToken);
            if (attempted.Disposition == TwCategoryRecoveryDurabilityDisposition.Unavailable)
                return await RecoverableAsync(operation, results, "RecoveryAttemptNotRecorded", cancellationToken);

            if (faultInjector is not null)
                await faultInjector.InjectAsync(TwCategoryRecoveryFaultBoundary.BeforeCategoryMutation, operation.OperationId, position, cancellationToken);

            TwCategoryRecoveryExecutionResult sourceResult;
            try
            {
                sourceResult = await recoveryService.ExecuteAsync(
                    new([target], ActorUserId: request.ActorUserId, Mode: TwCategoryRecoveryExecutionMode.SourceOnlyExecution),
                    (_, _) => Task.FromResult(true),
                    AuthorizePage,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return await InterruptAsync(operation, results, "CancellationRequested", cancellationToken);
            }

            if (auditUnavailable)
                return await RecoverableAsync(operation, results, "RecoveryAuditUnavailable", cancellationToken);

            if (faultInjector is not null)
                await faultInjector.InjectAsync(TwCategoryRecoveryFaultBoundary.AfterCategoryMutationBeforeAudit, operation.OperationId, position, cancellationToken);

            var sourceEntry = sourceResult.Entries.SingleOrDefault();
            var disposition = sourceEntry?.Disposition ?? sourceResult.Disposition;
            var confidence = disposition switch
            {
                TwCategoryRecoveryExecutionDisposition.Reindexed => TwCategoryRecoveryAuditConfidence.VerifiedCommit,
                TwCategoryRecoveryExecutionDisposition.NoChange => TwCategoryRecoveryAuditConfidence.Observation,
                _ => TwCategoryRecoveryAuditConfidence.Attempted
            };
            var outcomeEvent = await AppendAsync(new(
                Guid.NewGuid(),
                $"{operation.OperationId:N}:outcome:{position}:{work.AttemptId:N}",
                operation.OperationId,
                position,
                work.AttemptId,
                OutcomeKind(disposition),
                confidence,
                sourceEntry?.PageId ?? target.PageId,
                sourceEntry?.ResolvedRevision,
                disposition,
                request.ActorUserId,
                DateTime.UtcNow,
                sourceEntry?.DiagnosticCode ?? sourceResult.DiagnosticCode), cancellationToken);
            if (outcomeEvent.Disposition is TwCategoryRecoveryDurabilityDisposition.Unavailable or TwCategoryRecoveryDurabilityDisposition.DuplicateConflict || outcomeEvent.EventSequence is null)
                return await RecoverableAsync(operation, results, "RecoveryOutcomeNotRecorded", cancellationToken);

            if (faultInjector is not null)
                await faultInjector.InjectAsync(TwCategoryRecoveryFaultBoundary.AfterAuditBeforeCheckpoint, operation.OperationId, position, cancellationToken);

            var resultEntry = new TwCategoryRecoveryDurablePageResult(
                position,
                sourceEntry?.PageId,
                sourceEntry?.ResolvedRevision,
                disposition,
                confidence,
                sourceEntry?.DiagnosticCode ?? sourceResult.DiagnosticCode);
            results.Add(resultEntry);

            if (disposition == TwCategoryRecoveryExecutionDisposition.Cancelled || cancellationToken.IsCancellationRequested)
                return await InterruptAsync(operation, results, "CancellationRequested", cancellationToken);
            if (disposition is not TwCategoryRecoveryExecutionDisposition.Reindexed and not TwCategoryRecoveryExecutionDisposition.NoChange)
                return await RecoverableAsync(operation, results, "RecoveryPageRequiresReplay", cancellationToken);

            var advance = await durabilityStore.AdvanceCheckpointAsync(new(
                operation.OperationId,
                operation.Scope.Fingerprint,
                operation.Checkpoint.CommittedPosition,
                operation.Checkpoint.Generation,
                operation.Checkpoint.LastOutcomeSequence,
                position + 1,
                outcomeEvent.EventSequence.Value,
                $"{operation.OperationId:N}:outcome:{position}:{work.AttemptId:N}",
                scope.Targets.Count), cancellationToken);
            if (advance.Disposition != TwCategoryRecoveryDurabilityDisposition.Advanced || advance.Checkpoint is null)
                return await RecoverableAsync(operation, results, advance.DiagnosticCode ?? "RecoveryCheckpointAdvanceFailed", cancellationToken);

            if (faultInjector is not null)
                await faultInjector.InjectAsync(TwCategoryRecoveryFaultBoundary.AfterCheckpointCommitBeforeAcknowledgement, operation.OperationId, position, cancellationToken);
            operation = operation with { Checkpoint = advance.Checkpoint };
        }

        var completed = await TransitionAsync(operation, TwCategoryRecoveryOperationStatus.Completed, "AllTargetsCommittedOrNoChange", cancellationToken);
        if (completed.Operation is null)
            return MapDurabilityFailure(request.OperationId, operation, completed);
        logger.LogInformation("Durable category recovery operation completed with status {Status}.", completed.Operation.Status);
        return new(TwCategoryDurableRecoveryDisposition.Completed, request.OperationId, results, completed.Operation.Checkpoint, completed.Operation.Status);
    }

    private async Task<TwCategoryDurableRecoveryExecutionResult> RecoverableAsync(
        TwCategoryRecoveryOperationRecord operation,
        IReadOnlyList<TwCategoryRecoveryDurablePageResult> results,
        string reason,
        CancellationToken cancellationToken)
    {
        var nextStatus = results.Count == 0
            ? TwCategoryRecoveryOperationStatus.Recoverable
            : TwCategoryRecoveryOperationStatus.PartiallyCompleted;
        var transition = await TransitionAsync(operation, nextStatus, reason, cancellationToken);
        if (transition.Operation is null)
            return new(transition.Disposition == TwCategoryRecoveryDurabilityDisposition.GenerationConflict
                ? TwCategoryDurableRecoveryDisposition.GenerationConflict
                : TwCategoryDurableRecoveryDisposition.Unavailable, operation.OperationId, results, operation.Checkpoint, operation.Status, transition.DiagnosticCode ?? reason);
        return new(results.Count == 0 ? TwCategoryDurableRecoveryDisposition.Recoverable : TwCategoryDurableRecoveryDisposition.PartiallyCompleted, operation.OperationId, results, transition.Operation.Checkpoint, transition.Operation.Status, reason);
    }

    private async Task<TwCategoryDurableRecoveryExecutionResult> InterruptAsync(
        TwCategoryRecoveryOperationRecord operation,
        IReadOnlyList<TwCategoryRecoveryDurablePageResult> results,
        string reason,
        CancellationToken cancellationToken)
    {
        // Persist the interrupted marker at the cancellation boundary; the
        // caller token is no longer used for this bounded state transition.
        var transition = await TransitionAsync(operation, TwCategoryRecoveryOperationStatus.Interrupted, reason, CancellationToken.None);
        return new(TwCategoryDurableRecoveryDisposition.Interrupted, operation.OperationId, results, transition.Operation?.Checkpoint ?? operation.Checkpoint, transition.Operation?.Status ?? operation.Status, reason);
    }

    private Task<TwCategoryRecoveryTransitionOperationResult> TransitionAsync(
        TwCategoryRecoveryOperationRecord operation,
        TwCategoryRecoveryOperationStatus status,
        string? reason,
        CancellationToken cancellationToken)
        => durabilityStore.TransitionOperationAsync(new(operation.OperationId, operation.Status, status, operation.Checkpoint.Generation, reason), cancellationToken);

    private Task<TwCategoryRecoveryAppendAuditResult> AppendAsync(TwCategoryRecoveryAuditEvent auditEvent, CancellationToken cancellationToken)
        => durabilityStore.AppendAuditEventAsync(auditEvent, cancellationToken);

    private static TwCategoryRecoveryAuditEventKind OutcomeKind(TwCategoryRecoveryExecutionDisposition disposition)
        => disposition switch
        {
            TwCategoryRecoveryExecutionDisposition.Reindexed => TwCategoryRecoveryAuditEventKind.ReconciliationCommitted,
            TwCategoryRecoveryExecutionDisposition.NoChange => TwCategoryRecoveryAuditEventKind.NoChangeObserved,
            TwCategoryRecoveryExecutionDisposition.RevisionConflict => TwCategoryRecoveryAuditEventKind.ConflictObserved,
            TwCategoryRecoveryExecutionDisposition.IncompleteExtraction => TwCategoryRecoveryAuditEventKind.IncompleteExpansion,
            TwCategoryRecoveryExecutionDisposition.RepositoryUnavailable or TwCategoryRecoveryExecutionDisposition.PageMissing => TwCategoryRecoveryAuditEventKind.Unavailable,
            TwCategoryRecoveryExecutionDisposition.Cancelled => TwCategoryRecoveryAuditEventKind.Cancellation,
            _ => TwCategoryRecoveryAuditEventKind.Failure
        };

    private static bool EquivalentCheckpoint(TwCategoryRecoveryDurableCheckpoint expected, TwCategoryRecoveryDurableCheckpoint actual)
        => expected.OperationId == actual.OperationId
            && string.Equals(expected.ScopeFingerprint, actual.ScopeFingerprint, StringComparison.Ordinal)
            && expected.CommittedPosition == actual.CommittedPosition
            && expected.Generation == actual.Generation
            && expected.LastOutcomeSequence == actual.LastOutcomeSequence
            && expected.SchemaVersion == actual.SchemaVersion;

    private static TwCategoryDurableRecoveryExecutionResult MapDurabilityFailure(
        Guid operationId,
        TwCategoryRecoveryOperationRecord operation,
        TwCategoryRecoveryTransitionOperationResult result)
        => new(
            result.Disposition == TwCategoryRecoveryDurabilityDisposition.GenerationConflict
                ? TwCategoryDurableRecoveryDisposition.GenerationConflict
                : TwCategoryDurableRecoveryDisposition.Unavailable,
            operationId,
            [],
            operation.Checkpoint,
            operation.Status,
            result.DiagnosticCode ?? "RecoveryDurabilityUnavailable");

    private static TwCategoryDurableRecoveryExecutionResult Invalid(Guid operationId, string code)
        => new(TwCategoryDurableRecoveryDisposition.InvalidRequest, operationId, [], DiagnosticCode: code);

    private static TwCategoryDurableRecoveryDisposition MapTerminal(TwCategoryRecoveryOperationStatus status)
        => status == TwCategoryRecoveryOperationStatus.Cancelled
            ? TwCategoryDurableRecoveryDisposition.Cancelled
            : TwCategoryDurableRecoveryDisposition.Failed;
}

/// <summary>Fail-closed adapter used until durable recovery storage is authorized.</summary>
public sealed class RejectingTwCategoryRecoveryDurabilityStore : ITwCategoryRecoveryDurabilityStore
{
    private static readonly string Code = "RecoveryDurabilityUnavailable";

    public Task<TwCategoryRecoveryCreateOperationResult> CreateOrReplayOperationAsync(TwCategoryRecoveryOperationRecord requested, CancellationToken cancellationToken = default)
        => Task.FromResult(new TwCategoryRecoveryCreateOperationResult(TwCategoryRecoveryDurabilityDisposition.Unavailable, DiagnosticCode: Code));

    public Task<TwCategoryRecoveryLoadOperationResult> LoadOperationAsync(Guid operationId, CancellationToken cancellationToken = default)
        => Task.FromResult(new TwCategoryRecoveryLoadOperationResult(TwCategoryRecoveryDurabilityDisposition.Unavailable, DiagnosticCode: Code));

    public Task<TwCategoryRecoveryAppendAuditResult> AppendAuditEventAsync(TwCategoryRecoveryAuditEvent auditEvent, CancellationToken cancellationToken = default)
        => Task.FromResult(new TwCategoryRecoveryAppendAuditResult(TwCategoryRecoveryDurabilityDisposition.Unavailable, DiagnosticCode: Code));

    public Task<TwCategoryRecoveryAdvanceCheckpointResult> AdvanceCheckpointAsync(TwCategoryRecoveryCheckpointAdvanceRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new TwCategoryRecoveryAdvanceCheckpointResult(TwCategoryRecoveryDurabilityDisposition.Unavailable, DiagnosticCode: Code));

    public Task<TwCategoryRecoveryTransitionOperationResult> TransitionOperationAsync(TwCategoryRecoveryTransitionOperationRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new TwCategoryRecoveryTransitionOperationResult(TwCategoryRecoveryDurabilityDisposition.Unavailable, DiagnosticCode: Code));

    public Task<TwCategoryRecoveryProgressResult> ReadScopedProgressAsync(TwCategoryRecoveryProgressRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new TwCategoryRecoveryProgressResult(TwCategoryRecoveryDurabilityDisposition.Unavailable, DiagnosticCode: Code));
}
