using Microsoft.Extensions.Logging.Abstractions;
using TightWiki.Engine.MediaWiki;
using TightWiki.Plugin.Interfaces.Persistence;
using TightWiki.Plugin.Models;

namespace TightWiki.Tests.Unit;

public sealed class MediaWikiCategoryRecoveryDurabilityTests
{
    [Fact]
    public async Task RejectingDurabilityStoreFailsClosedBeforeReconciliation()
    {
        var recovery = new FakeRecoveryService();
        var service = new TwCategoryDurableRecoveryService(
            recovery,
            new RejectingTwCategoryRecoveryDurabilityStore(),
            NullLogger<TwCategoryDurableRecoveryService>.Instance);

        var result = await service.ExecuteAsync(Request(), AllowOperation, AllowPage);

        Assert.Equal(TwCategoryDurableRecoveryDisposition.Unavailable, result.Disposition);
        Assert.Equal(0, recovery.Calls);
    }

    [Fact]
    public async Task OperationCreationIsIdempotentButScopeIsImmutable()
    {
        var store = new FakeDurabilityStore();
        var first = Operation();
        var created = await store.CreateOrReplayOperationAsync(first);
        var replayed = await store.CreateOrReplayOperationAsync(first);
        var conflict = await store.CreateOrReplayOperationAsync(first with
        {
            Scope = Scope(18),
            Checkpoint = first.Checkpoint with { ScopeFingerprint = Scope(18).Fingerprint }
        });

        Assert.Equal(TwCategoryRecoveryDurabilityDisposition.Created, created.Disposition);
        Assert.Equal(TwCategoryRecoveryDurabilityDisposition.Replayed, replayed.Disposition);
        Assert.Equal(TwCategoryRecoveryDurabilityDisposition.ScopeConflict, conflict.Disposition);
    }

    [Fact]
    public async Task IllegalTerminalTransitionIsRejected()
    {
        var store = new FakeDurabilityStore();
        var operation = Operation();
        await store.CreateOrReplayOperationAsync(operation);

        var running = await store.TransitionOperationAsync(new(operation.OperationId, TwCategoryRecoveryOperationStatus.Planned, TwCategoryRecoveryOperationStatus.Running, 0));
        var completed = await store.TransitionOperationAsync(new(operation.OperationId, TwCategoryRecoveryOperationStatus.Running, TwCategoryRecoveryOperationStatus.Completed, 0));
        var illegal = await store.TransitionOperationAsync(new(operation.OperationId, TwCategoryRecoveryOperationStatus.Completed, TwCategoryRecoveryOperationStatus.Running, 0));

        Assert.Equal(TwCategoryRecoveryDurabilityDisposition.Finalized, running.Disposition);
        Assert.Equal(TwCategoryRecoveryDurabilityDisposition.Finalized, completed.Disposition);
        Assert.Equal(TwCategoryRecoveryDurabilityDisposition.InvalidTransition, illegal.Disposition);
    }

    [Fact]
    public async Task CheckpointCannotAdvanceWithoutRecordedOutcomeOrBackwards()
    {
        var store = new FakeDurabilityStore();
        var operation = Operation();
        await store.CreateOrReplayOperationAsync(operation);
        var request = new TwCategoryRecoveryCheckpointAdvanceRequest(
            operation.OperationId,
            operation.Scope.Fingerprint,
            0,
            0,
            0,
            1,
            1,
            "outcome",
            1);

        var missing = await store.AdvanceCheckpointAsync(request);
        var outcome = new TwCategoryRecoveryAuditEvent(Guid.NewGuid(), "outcome-1", operation.OperationId, 0, Guid.NewGuid(), TwCategoryRecoveryAuditEventKind.ReconciliationCommitted, TwCategoryRecoveryAuditConfidence.VerifiedCommit, 17, 4, TwCategoryRecoveryExecutionDisposition.Reindexed, null, DateTime.UtcNow);
        var recorded = await store.AppendAuditEventAsync(outcome);
        var advanced = await store.AdvanceCheckpointAsync(request with { RecordedOutcomeSequence = recorded.EventSequence!.Value, RecordedOutcomeEventKey = "outcome-1" });
        var next = new TwCategoryRecoveryAuditEvent(Guid.NewGuid(), "outcome-2", operation.OperationId, 0, Guid.NewGuid(), TwCategoryRecoveryAuditEventKind.ReconciliationCommitted, TwCategoryRecoveryAuditConfidence.VerifiedCommit, 17, 4, TwCategoryRecoveryExecutionDisposition.Reindexed, null, DateTime.UtcNow);
        var nextRecorded = await store.AppendAuditEventAsync(next);
        var backwards = await store.AdvanceCheckpointAsync(new(operation.OperationId, operation.Scope.Fingerprint, 1, advanced.Checkpoint!.Generation, advanced.Checkpoint.LastOutcomeSequence, 0, nextRecorded.EventSequence!.Value, "outcome-2", 1));

        Assert.Equal(TwCategoryRecoveryDurabilityDisposition.OutcomeNotRecorded, missing.Disposition);
        Assert.Equal(TwCategoryRecoveryDurabilityDisposition.Advanced, advanced.Disposition);
        Assert.Equal(TwCategoryRecoveryDurabilityDisposition.BackwardsCheckpoint, backwards.Disposition);
        var staleGeneration = await store.AdvanceCheckpointAsync(request with { RecordedOutcomeSequence = recorded.EventSequence!.Value, RecordedOutcomeEventKey = "outcome-1" });
        Assert.Equal(TwCategoryRecoveryDurabilityDisposition.GenerationConflict, staleGeneration.Disposition);
    }

    [Fact]
    public async Task DuplicateEventKeysAreIdempotentButConflictingPayloadsAreRejected()
    {
        var store = new FakeDurabilityStore();
        var operation = Operation();
        await store.CreateOrReplayOperationAsync(operation);
        var audit = new TwCategoryRecoveryAuditEvent(Guid.NewGuid(), "stable-event", operation.OperationId, 0, Guid.NewGuid(), TwCategoryRecoveryAuditEventKind.IntentRequested, TwCategoryRecoveryAuditConfidence.Intent, 17, null, null, null, DateTime.UtcNow);

        var first = await store.AppendAuditEventAsync(audit);
        var duplicate = await store.AppendAuditEventAsync(audit);
        var conflict = await store.AppendAuditEventAsync(audit with { DiagnosticCode = "different" });

        Assert.Equal(TwCategoryRecoveryDurabilityDisposition.Appended, first.Disposition);
        Assert.Equal(TwCategoryRecoveryDurabilityDisposition.DuplicateSame, duplicate.Disposition);
        Assert.Equal(TwCategoryRecoveryDurabilityDisposition.DuplicateConflict, conflict.Disposition);
    }

    [Fact]
    public void FutureCheckpointVersionsAreRejectedWithoutGuessing()
    {
        var operation = Operation();
        var request = new TwCategoryRecoveryCheckpointAdvanceRequest(
            operation.OperationId,
            operation.Scope.Fingerprint,
            0,
            0,
            0,
            1,
            1,
            "outcome",
            1,
            SchemaVersion: TwCategoryRecoveryDurabilityConstants.CurrentSchemaVersion + 1);

        var diagnostic = TwCategoryRecoveryDurabilityPolicy.ValidateCheckpointAdvance(operation.Checkpoint, request, true);

        Assert.Equal("IncompatibleCheckpointVersion", diagnostic);
    }

    [Fact]
    public async Task SuccessfulPageRecordsOutcomeBeforeCheckpointAndCompletes()
    {
        var store = new FakeDurabilityStore();
        var recovery = new FakeRecoveryService(TwCategoryRecoveryExecutionDisposition.Reindexed);
        var service = new TwCategoryDurableRecoveryService(recovery, store, NullLogger<TwCategoryDurableRecoveryService>.Instance);

        var result = await service.ExecuteAsync(Request(), AllowOperation, AllowPage);

        Assert.Equal(TwCategoryDurableRecoveryDisposition.Completed, result.Disposition);
        Assert.Equal(TwCategoryRecoveryOperationStatus.Completed, result.OperationStatus);
        Assert.Equal(1, result.Checkpoint!.CommittedPosition);
        Assert.Contains(store.Events, value => value.Kind == TwCategoryRecoveryAuditEventKind.ReconciliationCommitted && value.Confidence == TwCategoryRecoveryAuditConfidence.VerifiedCommit);
        Assert.True(store.Events.FindIndex(value => value.Kind == TwCategoryRecoveryAuditEventKind.ReconciliationCommitted)
            < store.CheckpointAdvanceEventIndex);
    }

    [Fact]
    public async Task CrashBoundaryBeforeCheckpointLeavesRecoverableAndResumeReplays()
    {
        var store = new FakeDurabilityStore { FailNextCheckpointAdvance = true };
        var recovery = new FakeRecoveryService(TwCategoryRecoveryExecutionDisposition.Reindexed);
        var service = new TwCategoryDurableRecoveryService(recovery, store, NullLogger<TwCategoryDurableRecoveryService>.Instance);

        var first = await service.ExecuteAsync(Request(), AllowOperation, AllowPage);
        var second = await service.ExecuteAsync(Request(), AllowOperation, AllowPage);

        Assert.Equal(TwCategoryDurableRecoveryDisposition.PartiallyCompleted, first.Disposition);
        Assert.Equal(0, first.Checkpoint!.CommittedPosition);
        Assert.Equal(TwCategoryDurableRecoveryDisposition.Completed, second.Disposition);
        Assert.Equal(2, recovery.Calls);
    }

    [Theory]
    [InlineData(TwCategoryRecoveryFaultBoundary.BeforeCategoryMutation)]
    [InlineData(TwCategoryRecoveryFaultBoundary.AfterCategoryMutationBeforeAudit)]
    [InlineData(TwCategoryRecoveryFaultBoundary.AfterAuditBeforeCheckpoint)]
    public async Task ApplicationInterruptionBeforeAcknowledgedCheckpointResumesFromDurableEvidence(
        TwCategoryRecoveryFaultBoundary boundary)
    {
        var store = new FakeDurabilityStore();
        var recovery = new FakeRecoveryService(TwCategoryRecoveryExecutionDisposition.Reindexed);
        var injector = new ThrowOnceFaultInjector(boundary);
        var service = new TwCategoryDurableRecoveryService(
            recovery,
            store,
            NullLogger<TwCategoryDurableRecoveryService>.Instance,
            injector);

        await Assert.ThrowsAsync<InjectedApplicationInterruption>(() => service.ExecuteAsync(Request(), AllowOperation, AllowPage));

        // Resume uses the same durable operation but a fresh coordinator; no
        // checkpoint is inferred from the missing caller acknowledgement.
        var resumed = await new TwCategoryDurableRecoveryService(
            recovery,
            store,
            NullLogger<TwCategoryDurableRecoveryService>.Instance)
            .ExecuteAsync(Request(), AllowOperation, AllowPage);

        Assert.Equal(TwCategoryDurableRecoveryDisposition.Completed, resumed.Disposition);
        Assert.Equal(1, resumed.Checkpoint!.CommittedPosition);
        Assert.Equal(1, store.Operation!.Checkpoint.CommittedPosition);
    }

    [Fact]
    public async Task CheckpointCommitInterruptionResumesWithoutIllegalRunningTransition()
    {
        var store = new FakeDurabilityStore();
        var recovery = new FakeRecoveryService(TwCategoryRecoveryExecutionDisposition.Reindexed);
        var injector = new ThrowOnceFaultInjector(TwCategoryRecoveryFaultBoundary.AfterCheckpointCommitBeforeAcknowledgement);
        var firstService = new TwCategoryDurableRecoveryService(
            recovery,
            store,
            NullLogger<TwCategoryDurableRecoveryService>.Instance,
            injector);

        await Assert.ThrowsAsync<InjectedApplicationInterruption>(() => firstService.ExecuteAsync(Request(), AllowOperation, AllowPage));
        Assert.Equal(1, store.Operation!.Checkpoint.CommittedPosition);
        Assert.Equal(TwCategoryRecoveryOperationStatus.Running, store.Operation.Status);

        var resumed = await new TwCategoryDurableRecoveryService(
            recovery,
            store,
            NullLogger<TwCategoryDurableRecoveryService>.Instance)
            .ExecuteAsync(Request(), AllowOperation, AllowPage);

        Assert.Equal(TwCategoryDurableRecoveryDisposition.Completed, resumed.Disposition);
        Assert.Equal(TwCategoryRecoveryOperationStatus.Completed, resumed.OperationStatus);
        Assert.Equal(1, recovery.Calls);
    }

    [Fact]
    public async Task AuthorizationRevocationDoesNotAdvanceCheckpointOrExposePage()
    {
        var store = new FakeDurabilityStore();
        var recovery = new FakeRecoveryService(TwCategoryRecoveryExecutionDisposition.Unauthorized, redacted: true);
        var service = new TwCategoryDurableRecoveryService(recovery, store, NullLogger<TwCategoryDurableRecoveryService>.Instance);

        var result = await service.ExecuteAsync(Request(), AllowOperation, (_, _) => Task.FromResult(false));

        Assert.Equal(TwCategoryDurableRecoveryDisposition.PartiallyCompleted, result.Disposition);
        Assert.Null(result.Entries.Single().PageId);
        Assert.Equal(0, result.Checkpoint!.CommittedPosition);
    }

    [Fact]
    public async Task PreCancelledOperationReturnsTypedCancellationWithoutCreatingIntent()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var store = new FakeDurabilityStore();
        var service = new TwCategoryDurableRecoveryService(new FakeRecoveryService(), store, NullLogger<TwCategoryDurableRecoveryService>.Instance);

        var result = await service.ExecuteAsync(Request(), AllowOperation, AllowPage, cancellation.Token);

        Assert.Equal(TwCategoryDurableRecoveryDisposition.Cancelled, result.Disposition);
        Assert.Null(store.Operation);
    }

    [Fact]
    public async Task TerminalOperationCannotBeReplayedAsAnotherMutation()
    {
        var store = new FakeDurabilityStore();
        var operation = Operation();
        await store.CreateOrReplayOperationAsync(operation);
        await store.TransitionOperationAsync(new(operation.OperationId, TwCategoryRecoveryOperationStatus.Planned, TwCategoryRecoveryOperationStatus.Running, 0));
        await store.TransitionOperationAsync(new(operation.OperationId, TwCategoryRecoveryOperationStatus.Running, TwCategoryRecoveryOperationStatus.Completed, 0));
        var recovery = new FakeRecoveryService();
        var service = new TwCategoryDurableRecoveryService(recovery, store, NullLogger<TwCategoryDurableRecoveryService>.Instance);

        var result = await service.ExecuteAsync(Request(), AllowOperation, AllowPage);

        Assert.Equal(TwCategoryDurableRecoveryDisposition.AlreadyCompleted, result.Disposition);
        Assert.Equal(0, recovery.Calls);
    }

    [Fact]
    public async Task ProgressProjectionIsRedacted()
    {
        var store = new FakeDurabilityStore();
        var operation = Operation();
        await store.CreateOrReplayOperationAsync(operation);

        var progress = await store.ReadScopedProgressAsync(new(operation.OperationId, operation.Scope.Fingerprint, null));

        Assert.Equal(TwCategoryRecoveryDurabilityDisposition.ProgressRead, progress.Disposition);
        Assert.True(progress.Progress!.Redacted);
    }

    private static TwCategoryRecoveryDurableExecutionRequest Request()
        => new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), [new TwCategoryRecoveryTarget(17)]);

    private static TwCategoryRecoveryOperationRecord Operation()
    {
        var scope = Scope(17);
        return new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), scope, null, DateTime.UtcNow, TwCategoryRecoveryDurabilityConstants.PolicyVersion, TwCategoryRecoveryOperationStatus.Planned, new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), scope.Fingerprint, 0, 0, 0));
    }

    private static TwCategoryRecoveryScope Scope(int pageId)
        => TwCategoryRecoveryScope.TryCreate([new TwCategoryRecoveryTarget(pageId)], out var scope, out var error)
            ? scope
            : throw new InvalidOperationException(error);

    private static Task<bool> AllowOperation(TwCategoryRecoveryOperationAuthorizationContext _, CancellationToken __) => Task.FromResult(true);
    private static Task<bool> AllowPage(TwCategoryRecoveryPageAuthorizationContext _, CancellationToken __) => Task.FromResult(true);

    private sealed class FakeRecoveryService(
        TwCategoryRecoveryExecutionDisposition disposition = TwCategoryRecoveryExecutionDisposition.Reindexed,
        bool redacted = false) : ITwCategoryRecoveryService
    {
        public int Calls { get; private set; }

        public Task<TwCategoryRecoveryPlan> PlanAsync(TwCategoryRecoveryPlanRequest request, Func<TwCategoryRecoveryOperationAuthorizationContext, CancellationToken, Task<bool>>? authorizeOperation, Func<TwCategoryRecoveryPageAuthorizationContext, CancellationToken, Task<bool>>? authorizePage, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async Task<TwCategoryRecoveryExecutionResult> ExecuteAsync(TwCategoryRecoveryExecutionRequest request, Func<TwCategoryRecoveryOperationAuthorizationContext, CancellationToken, Task<bool>>? authorizeOperation, Func<TwCategoryRecoveryPageAuthorizationContext, CancellationToken, Task<bool>>? authorizePage, CancellationToken cancellationToken = default)
        {
            Calls++;
            var allowed = authorizePage is null || await authorizePage(new(TwCategoryRecoveryOperation.Execute, 17, "article::example", 4, request.ActorUserId), cancellationToken);
            if (!allowed)
                disposition = TwCategoryRecoveryExecutionDisposition.Unauthorized;
            var entry = new TwCategoryRecoveryExecutionEntry(redacted || !allowed ? null : 17, redacted || !allowed ? null : "article::example", 4, 4, disposition, disposition == TwCategoryRecoveryExecutionDisposition.Reindexed ? null : "SyntheticOutcome");
            return new(disposition, [entry]);
        }
    }

    private sealed class FakeDurabilityStore : ITwCategoryRecoveryDurabilityStore
    {
        public TwCategoryRecoveryOperationRecord? Operation { get; private set; }
        public List<TwCategoryRecoveryAuditEvent> Events { get; } = [];
        public bool FailNextCheckpointAdvance { get; set; }
        public int CheckpointAdvanceEventIndex { get; private set; } = -1;
        private long _nextSequence;

        public Task<TwCategoryRecoveryCreateOperationResult> CreateOrReplayOperationAsync(TwCategoryRecoveryOperationRecord requested, CancellationToken cancellationToken = default)
        {
            if (Operation is null)
            {
                Operation = requested;
                return Task.FromResult(new TwCategoryRecoveryCreateOperationResult(TwCategoryRecoveryDurabilityDisposition.Created, Operation));
            }
            return Task.FromResult(Operation.Scope.Fingerprint == requested.Scope.Fingerprint && Operation.ActorUserId == requested.ActorUserId
                ? new TwCategoryRecoveryCreateOperationResult(TwCategoryRecoveryDurabilityDisposition.Replayed, Operation)
                : new TwCategoryRecoveryCreateOperationResult(TwCategoryRecoveryDurabilityDisposition.ScopeConflict, DiagnosticCode: "RecoveryScopeConflict"));
        }

        public Task<TwCategoryRecoveryLoadOperationResult> LoadOperationAsync(Guid operationId, CancellationToken cancellationToken = default)
            => Task.FromResult(Operation?.OperationId == operationId
                ? new TwCategoryRecoveryLoadOperationResult(TwCategoryRecoveryDurabilityDisposition.Loaded, Operation)
                : new TwCategoryRecoveryLoadOperationResult(TwCategoryRecoveryDurabilityDisposition.InvalidRequest, DiagnosticCode: "OperationMissing"));

        public Task<TwCategoryRecoveryAppendAuditResult> AppendAuditEventAsync(TwCategoryRecoveryAuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            var existing = Events.FirstOrDefault(value => value.EventKey == auditEvent.EventKey);
            if (existing is not null)
                return Task.FromResult(new TwCategoryRecoveryAppendAuditResult(
                    existing with { Sequence = null } == auditEvent with { Sequence = null }
                        ? TwCategoryRecoveryDurabilityDisposition.DuplicateSame
                        : TwCategoryRecoveryDurabilityDisposition.DuplicateConflict,
                    existing.Sequence ?? 0));
            var sequence = ++_nextSequence;
            Events.Add(auditEvent with { Sequence = sequence });
            return Task.FromResult(new TwCategoryRecoveryAppendAuditResult(TwCategoryRecoveryDurabilityDisposition.Appended, sequence));
        }

        public Task<TwCategoryRecoveryAdvanceCheckpointResult> AdvanceCheckpointAsync(TwCategoryRecoveryCheckpointAdvanceRequest request, CancellationToken cancellationToken = default)
        {
            if (FailNextCheckpointAdvance)
            {
                FailNextCheckpointAdvance = false;
                return Task.FromResult(new TwCategoryRecoveryAdvanceCheckpointResult(TwCategoryRecoveryDurabilityDisposition.Unavailable, DiagnosticCode: "InjectedCheckpointFailure"));
            }
            var operation = Operation!;
            var recorded = Events.FirstOrDefault(value => value.EventKey == request.RecordedOutcomeEventKey && value.Sequence == request.RecordedOutcomeSequence);
            var validation = TwCategoryRecoveryDurabilityPolicy.ValidateCheckpointAdvance(operation.Checkpoint, request, recorded is not null);
            if (validation is not null)
            {
                var disposition = validation switch
                {
                    "CheckpointCannotMoveBackwards" => TwCategoryRecoveryDurabilityDisposition.BackwardsCheckpoint,
                    "OutcomeNotRecorded" => TwCategoryRecoveryDurabilityDisposition.OutcomeNotRecorded,
                    "CheckpointGenerationConflict" => TwCategoryRecoveryDurabilityDisposition.GenerationConflict,
                    _ => TwCategoryRecoveryDurabilityDisposition.InvalidRequest
                };
                return Task.FromResult(new TwCategoryRecoveryAdvanceCheckpointResult(disposition, DiagnosticCode: validation));
            }
            var checkpoint = operation.Checkpoint with { CommittedPosition = request.NextCommittedPosition, Generation = operation.Checkpoint.Generation + 1, LastOutcomeSequence = request.RecordedOutcomeSequence };
            Operation = operation with { Checkpoint = checkpoint };
            CheckpointAdvanceEventIndex = Events.Count;
            return Task.FromResult(new TwCategoryRecoveryAdvanceCheckpointResult(TwCategoryRecoveryDurabilityDisposition.Advanced, checkpoint));
        }

        public Task<TwCategoryRecoveryTransitionOperationResult> TransitionOperationAsync(TwCategoryRecoveryTransitionOperationRequest request, CancellationToken cancellationToken = default)
        {
            var operation = Operation!;
            if (operation.Status != request.ExpectedStatus || operation.Checkpoint.Generation != request.ExpectedGeneration)
                return Task.FromResult(new TwCategoryRecoveryTransitionOperationResult(TwCategoryRecoveryDurabilityDisposition.GenerationConflict, DiagnosticCode: "OperationGenerationConflict"));
            if (!TwCategoryRecoveryDurabilityPolicy.CanTransition(operation.Status, request.NewStatus))
                return Task.FromResult(new TwCategoryRecoveryTransitionOperationResult(TwCategoryRecoveryDurabilityDisposition.InvalidTransition, DiagnosticCode: "InvalidOperationTransition"));
            Operation = operation with { Status = request.NewStatus, TerminalReason = request.TerminalReason };
            return Task.FromResult(new TwCategoryRecoveryTransitionOperationResult(TwCategoryRecoveryDurabilityDisposition.Finalized, Operation));
        }

        public Task<TwCategoryRecoveryProgressResult> ReadScopedProgressAsync(TwCategoryRecoveryProgressRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new TwCategoryRecoveryProgressResult(TwCategoryRecoveryDurabilityDisposition.ProgressRead, Operation is null ? null : new(Operation.OperationId, Operation.Scope.Fingerprint, Operation.Status, Operation.Checkpoint, [], true)));
    }

    private sealed class ThrowOnceFaultInjector(TwCategoryRecoveryFaultBoundary boundary) : ITwCategoryRecoveryFaultInjector
    {
        private bool _thrown;

        public Task InjectAsync(TwCategoryRecoveryFaultBoundary requested, Guid operationId, int position, CancellationToken cancellationToken = default)
        {
            if (!_thrown && requested == boundary)
            {
                _thrown = true;
                throw new InjectedApplicationInterruption(boundary);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class InjectedApplicationInterruption(TwCategoryRecoveryFaultBoundary boundary)
        : Exception($"Injected application interruption at {boundary}.");

}
