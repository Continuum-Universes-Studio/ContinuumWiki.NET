using TightWiki.Plugin.Interfaces.Persistence;
using TightWiki.Plugin.Models;
using TightWiki.Engine.MediaWiki;

namespace TightWiki.Tests.Unit;

public sealed class TwCategoryAdministrativeGrantDurabilityTests
{
    private static readonly Guid ActorId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GrantId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid ApprovalId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid ApproverId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    [Fact]
    public async Task ConcurrentSingleUseConsumptionHasOneWinner()
    {
        var store = new ControlledGrantStore(SingleUseGrant());
        var request = AuthorizationRequest(TwCategoryAdministrativeOperation.Execute);
        var provider = new TwCategoryAdministrativeGrantProvider(store, store);

        var decisions = await Task.WhenAll(
            provider.EvaluateAsync(request),
            provider.EvaluateAsync(request));

        Assert.Single(decisions, decision => decision.Allowed);
        Assert.Single(decisions, decision => decision.Reason == TwCategoryAdministrativeAuthorizationReason.GrantReplayed);
        Assert.Equal(TwCategoryAdministrativeGrantState.Consumed, store.State.State);
        Assert.Equal(1L, store.State.Version);
    }

    [Fact]
    public async Task RevocationSerializedBeforeConsumptionWinsWithoutRetroactiveUndo()
    {
        var revokedFirst = new ControlledGrantStore(SingleUseGrant());
        var revoke = await revokedFirst.RevokeAsync(new(
            GrantId, 0, ApproverId, Guid.NewGuid(), DateTime.UtcNow, "operator cancellation"));
        var consumeAfterRevoke = await revokedFirst.TryConsumeAsync(ConsumeRequest(0));

        Assert.Equal(TwCategoryAdministrativeGrantDurabilityDisposition.Revoked, revoke.Disposition);
        Assert.Equal(TwCategoryAdministrativeGrantDurabilityDisposition.Revoked, consumeAfterRevoke.Disposition);

        var consumedFirst = new ControlledGrantStore(SingleUseGrant());
        var consume = await consumedFirst.TryConsumeAsync(ConsumeRequest(0));
        var revokeAfterConsume = await consumedFirst.RevokeAsync(new(
            GrantId, 0, ApproverId, Guid.NewGuid(), DateTime.UtcNow, "too late"));

        Assert.Equal(TwCategoryAdministrativeGrantDurabilityDisposition.Consumed, consume.Disposition);
        Assert.Equal(TwCategoryAdministrativeGrantDurabilityDisposition.ScopeConflict, revokeAfterConsume.Disposition);
        Assert.Equal(TwCategoryAdministrativeGrantState.Consumed, consumedFirst.State.State);
    }

    [Fact]
    public async Task ConsumedStateSurvivesAStoreReconstructionAndOutcomeIsIdempotent()
    {
        var state = new ControlledGrantStore(SingleUseGrant());
        Assert.Equal(TwCategoryAdministrativeGrantDurabilityDisposition.Consumed,
            (await state.TryConsumeAsync(ConsumeRequest(0))).Disposition);

        var reconstructed = new ControlledGrantStore(state.State.Grant, state.State);
        var replay = await reconstructed.TryConsumeAsync(ConsumeRequest(0));
        var outcome = new TwCategoryAdministrativeGrantExecutionOutcomeRequest(
            GrantId, ActorId, TwCategoryAdministrativeOperation.Execute, Scope(),
            CorrelationId, "Completed", DateTime.UtcNow);

        Assert.Equal(TwCategoryAdministrativeGrantDurabilityDisposition.AlreadyConsumed, replay.Disposition);
        Assert.Equal(TwCategoryAdministrativeGrantDurabilityDisposition.OutcomeRecorded,
            (await reconstructed.RecordExecutionOutcomeAsync(outcome)).Disposition);
        Assert.Equal(TwCategoryAdministrativeGrantDurabilityDisposition.DuplicateSame,
            (await reconstructed.RecordExecutionOutcomeAsync(outcome)).Disposition);
    }

    [Fact]
    public async Task DurableProviderRequiresCorrelationAndDoesNotConsumePreview()
    {
        var previewStore = new ControlledGrantStore(SingleUseGrant() with { Operation = TwCategoryAdministrativeOperation.Preview });
        var provider = new TwCategoryAdministrativeGrantProvider(previewStore, previewStore);

        var preview = await provider.EvaluateAsync(AuthorizationRequest(TwCategoryAdministrativeOperation.Preview));
        var executeStore = new ControlledGrantStore(SingleUseGrant());
        var executeProvider = new TwCategoryAdministrativeGrantProvider(executeStore, executeStore);
        var missingCorrelation = await executeProvider.EvaluateAsync(new(
            TwCategoryAdministrativeOperation.Execute, ActorId, Scope()));

        Assert.True(preview.Allowed);
        Assert.Equal(0L, previewStore.State.Version);
        Assert.Equal(TwCategoryAdministrativeAuthorizationReason.ProviderUnavailable, missingCorrelation.Reason);
    }

    [Fact]
    public async Task RejectedAuthorizationIsRecordedWithoutChangingGrantState()
    {
        var store = new ControlledGrantStore(SingleUseGrant());
        await store.RevokeAsync(new(GrantId, 0, ApproverId, Guid.NewGuid(), DateTime.UtcNow, "revoked for test"));
        var provider = new TwCategoryAdministrativeGrantProvider(store, store);

        var decision = await provider.EvaluateAsync(AuthorizationRequest(TwCategoryAdministrativeOperation.Execute));

        Assert.False(decision.Allowed);
        Assert.Equal(TwCategoryAdministrativeAuthorizationReason.GrantRevoked, decision.Reason);
        Assert.Equal(TwCategoryAdministrativeGrantState.Revoked, store.State.State);
    }

    private static TwCategoryAdministrativeAuthorizationRequest AuthorizationRequest(
        TwCategoryAdministrativeOperation operation)
        => new(operation, ActorId, Scope(), CorrelationId);

    private static TwCategoryAdministrativeGrantConsumeRequest ConsumeRequest(long version)
        => new(GrantId, version, ActorId, TwCategoryAdministrativeOperation.Execute, Scope(), CorrelationId, DateTime.UtcNow);

    private static TwCategoryRecoveryScope Scope()
        => TwCategoryRecoveryScope.TryCreate([new TwCategoryRecoveryTarget(17, "Category:Example")], out var scope, out var error)
            ? scope
            : throw new InvalidOperationException(error);

    private static TwCategoryAdministrativeGrant SingleUseGrant()
    {
        var now = DateTime.UtcNow;
        return new(GrantId, ApprovalId, ApproverId, ActorId, TwCategoryAdministrativeOperation.Execute, Scope(),
            now.AddMinutes(-1), now.AddMinutes(-1), now.AddMinutes(5),
            TwCategoryAdministrativeGrantRevocationState.Active,
            TwCategoryAdministrativeGrantReplayMode.SingleUse,
            "operator-approved-evidence");
    }

    private static readonly Guid CorrelationId = Guid.Parse("77777777-7777-7777-7777-777777777777");

    private sealed class ControlledGrantStore
        : ITwCategoryAdministrativeGrantStore
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _outcomes = new(StringComparer.Ordinal);
        private readonly HashSet<string> _rejections = new(StringComparer.Ordinal);
        public ControlledGrantStore(TwCategoryAdministrativeGrant grant, TwCategoryAdministrativeGrantRecord? existing = null)
            => State = existing ?? new(grant, 0, TwCategoryAdministrativeGrantState.Issued, null, null, null, null, null, null, null, 0);

        public TwCategoryAdministrativeGrantRecord State { get; private set; }

        public Task<TwCategoryAdministrativeGrantEvidenceResult> GetAsync(
            TwCategoryAdministrativeAuthorizationRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new TwCategoryAdministrativeGrantEvidenceResult(
                State.Grant, State.State == TwCategoryAdministrativeGrantState.Consumed, true, State.Version));

        public Task<TwCategoryAdministrativeGrantCreateResult> CreateAsync(
            TwCategoryAdministrativeGrantCreateRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new TwCategoryAdministrativeGrantCreateResult(
                TwCategoryAdministrativeGrantDurabilityDisposition.Created, State));

        public Task<TwCategoryAdministrativeGrantLoadResult> LoadAsync(
            Guid grantId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new TwCategoryAdministrativeGrantLoadResult(
                TwCategoryAdministrativeGrantDurabilityDisposition.Loaded, State));

        public Task<TwCategoryAdministrativeGrantConsumeResult> TryConsumeAsync(
            TwCategoryAdministrativeGrantConsumeRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (State.State == TwCategoryAdministrativeGrantState.Revoked)
                    return Task.FromResult(new TwCategoryAdministrativeGrantConsumeResult(TwCategoryAdministrativeGrantDurabilityDisposition.Revoked, State));
                if (State.State == TwCategoryAdministrativeGrantState.Consumed)
                    return Task.FromResult(new TwCategoryAdministrativeGrantConsumeResult(TwCategoryAdministrativeGrantDurabilityDisposition.AlreadyConsumed, State));
                if (request.ExpectedVersion != State.Version)
                    return Task.FromResult(new TwCategoryAdministrativeGrantConsumeResult(TwCategoryAdministrativeGrantDurabilityDisposition.GenerationConflict, State));
                State = State with
                {
                    Version = State.Version + 1,
                    State = TwCategoryAdministrativeGrantState.Consumed,
                    ConsumedAtUtc = request.NowUtc,
                    ConsumedByActorUserId = request.ActorUserId,
                    ConsumedOperationId = request.CorrelationId,
                    ConsumedCorrelationId = request.CorrelationId
                };
                return Task.FromResult(new TwCategoryAdministrativeGrantConsumeResult(TwCategoryAdministrativeGrantDurabilityDisposition.Consumed, State));
            }
        }

        public Task<TwCategoryAdministrativeGrantRevokeResult> RevokeAsync(
            TwCategoryAdministrativeGrantRevokeRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (State.State == TwCategoryAdministrativeGrantState.Consumed)
                    return Task.FromResult(new TwCategoryAdministrativeGrantRevokeResult(TwCategoryAdministrativeGrantDurabilityDisposition.ScopeConflict, State, "GrantAlreadyConsumed"));
                if (State.State == TwCategoryAdministrativeGrantState.Revoked)
                    return Task.FromResult(new TwCategoryAdministrativeGrantRevokeResult(TwCategoryAdministrativeGrantDurabilityDisposition.AlreadyRevoked, State));
                if (request.ExpectedVersion != State.Version)
                    return Task.FromResult(new TwCategoryAdministrativeGrantRevokeResult(TwCategoryAdministrativeGrantDurabilityDisposition.GenerationConflict, State));
                State = State with
                {
                    Version = State.Version + 1,
                    State = TwCategoryAdministrativeGrantState.Revoked,
                    Grant = State.Grant with { RevocationState = TwCategoryAdministrativeGrantRevocationState.Revoked },
                    RevokedAtUtc = request.RevokedAtUtc,
                    RevokedByActorUserId = request.RevokingActorUserId,
                    RevocationReason = request.Reason
                };
                return Task.FromResult(new TwCategoryAdministrativeGrantRevokeResult(TwCategoryAdministrativeGrantDurabilityDisposition.Revoked, State));
            }
        }

        public Task<TwCategoryAdministrativeGrantExecutionOutcomeResult> RecordExecutionOutcomeAsync(
            TwCategoryAdministrativeGrantExecutionOutcomeRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var key = $"{request.GrantId:D}:{request.CorrelationId:D}";
                if (!_outcomes.Add(key))
                    return Task.FromResult(new TwCategoryAdministrativeGrantExecutionOutcomeResult(TwCategoryAdministrativeGrantDurabilityDisposition.DuplicateSame));
                return Task.FromResult(new TwCategoryAdministrativeGrantExecutionOutcomeResult(TwCategoryAdministrativeGrantDurabilityDisposition.OutcomeRecorded));
            }
        }

        public Task<TwCategoryAdministrativeGrantAuthorizationRejectionResult> RecordAuthorizationRejectionAsync(
            TwCategoryAdministrativeGrantAuthorizationRejectionRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var key = $"{request.GrantId:D}:{request.CorrelationId:D}:{request.Reason}";
                if (!_rejections.Add(key))
                    return Task.FromResult(new TwCategoryAdministrativeGrantAuthorizationRejectionResult(TwCategoryAdministrativeGrantDurabilityDisposition.DuplicateSame));
                return Task.FromResult(new TwCategoryAdministrativeGrantAuthorizationRejectionResult(TwCategoryAdministrativeGrantDurabilityDisposition.OutcomeRecorded));
            }
        }
    }
}
