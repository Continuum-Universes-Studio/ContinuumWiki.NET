using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using TightWiki.Controllers;
using TightWiki.Engine.MediaWiki;
using TightWiki.Plugin.Models;

namespace TightWiki.Tests.Unit;

public sealed class TwCategoryAdministrativeRecoveryApiTests
{
    private static readonly Guid ActorId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void MissingGateIsDisabledAndDoesNotReadEvidenceOrInvokeRecovery()
    {
        var evidence = new CountingEvidenceSource(FullyVerified());
        var recovery = new FakeRecoveryService();
        var api = CreateApi(enabled: false, evidence, new FakeAuthorization(true), recovery, new FakeDurableRecoveryService());

        var result = api.Readiness(Administrator());

        Assert.Equal(TwCategoryAdministrativeApiDisposition.Disabled, result.Disposition);
        Assert.Equal(0, evidence.Reads);
        Assert.Equal(0, recovery.PlanCalls);
    }

    [Fact]
    public void AnonymousAndOrdinaryAuthenticatedCallersAreRejectedWithoutReadinessDetails()
    {
        var evidence = new CountingEvidenceSource(FullyVerified());
        var api = CreateApi(enabled: true, evidence, new FakeAuthorization(true), new FakeRecoveryService(), new FakeDurableRecoveryService());

        var anonymous = api.Readiness(new(false, false, null));
        var ordinary = api.Readiness(new(true, false, ActorId));

        Assert.Equal(TwCategoryAdministrativeApiDisposition.Unauthenticated, anonymous.Disposition);
        Assert.Equal(TwCategoryAdministrativeApiDisposition.Forbidden, ordinary.Disposition);
        Assert.Null(anonymous.Readiness);
        Assert.Null(ordinary.Readiness);
        Assert.Equal(0, evidence.Reads);
    }

    [Fact]
    public async Task AdministratorAuthenticationAloneCannotExecute()
    {
        var durable = new FakeDurableRecoveryService();
        var authorization = new FakeAuthorization(false);
        var api = CreateApi(enabled: true, new CountingEvidenceSource(FullyVerified()), authorization, new FakeRecoveryService(), durable);

        var result = await api.ExecuteAsync(Administrator(), Request(), AllowPage);

        Assert.Equal(TwCategoryAdministrativeApiDisposition.Forbidden, result.Disposition);
        Assert.True(durable.Called);
        Assert.False(durable.MutationCalled);
        Assert.True(authorization.Calls > 0);
    }

    [Fact]
    public async Task DefaultRejectingEvidenceGatesExecutionBeforeAnyDurableWork()
    {
        var durable = new FakeDurableRecoveryService();
        var api = CreateApi(enabled: true, new RejectingTwCategoryOperationalReadinessEvidenceSource(), new FakeAuthorization(true), new FakeRecoveryService(), durable);

        var result = await api.ExecuteAsync(Administrator(), Request(), AllowPage);

        Assert.Equal(TwCategoryAdministrativeApiDisposition.MutationGated, result.Disposition);
        Assert.False(durable.Called);
    }

    [Fact]
    public async Task ForgedRequestCannotSupplyActorRevisionTargetOrAuthorizationVerdict()
    {
        var properties = typeof(TwCategoryAdministrativeRecoveryRequest).GetProperties().Select(property => property.Name).ToArray();

        Assert.DoesNotContain(nameof(TwCategoryRecoveryPlanEntry.CurrentRevision), properties);
        Assert.DoesNotContain(nameof(TwCategoryRecoveryPlanEntry.PageId), properties);
        Assert.DoesNotContain("ActorUserId", properties);
        Assert.DoesNotContain("DatabaseTarget", properties);
        Assert.DoesNotContain("AuthorizationApproved", properties);

        var authorization = new FakeAuthorization(true);
        var durable = new FakeDurableRecoveryService();
        var api = CreateApi(enabled: true, new CountingEvidenceSource(FullyVerified()), authorization, new FakeRecoveryService(), durable);

        var result = await api.ExecuteAsync(
            Administrator(),
            new(Guid.NewGuid(), [new TwCategoryRecoveryTarget(17, "Page:Trusted")]),
            AllowPage);

        Assert.Equal(TwCategoryAdministrativeApiDisposition.Success, result.Disposition);
        Assert.Equal(ActorId, durable.ActorUserId);
        Assert.Equal(ActorId, authorization.LastRequest?.ActorUserId);
    }

    [Fact]
    public async Task PreviewIsReadOnlyAndUsesFreshOperationAuthorization()
    {
        var recovery = new FakeRecoveryService
        {
            Plan = new(TwCategoryRecoveryPlanDisposition.NoChange, "scope", [new(17, "Page:Trusted", 4, TwCategoryRecoveryPlanDisposition.NoChange, TwCategoryIndexConsistencyState.Current, true)]),
            InvokeOperationAuthorization = true
        };
        var durable = new FakeDurableRecoveryService();
        var authorization = new FakeAuthorization(true);
        var api = CreateApi(enabled: true, new CountingEvidenceSource(FullyVerified()), authorization, recovery, durable);

        var result = await api.PreviewAsync(Administrator(), Request(), AllowPage);

        Assert.Equal(TwCategoryAdministrativeApiDisposition.Success, result.Disposition);
        Assert.Equal(TwCategoryRecoveryPlanDisposition.NoChange, result.Plan?.Disposition);
        Assert.Equal(TwCategoryAdministrativeOperation.Preview, authorization.LastRequest?.Operation);
        Assert.Equal(1, recovery.PlanCalls);
        Assert.False(durable.Called);
    }

    [Fact]
    public async Task UnauthorizedPreviewDoesNotExposePlanEntries()
    {
        var recovery = new FakeRecoveryService
        {
            InvokeOperationAuthorization = true
        };
        var api = CreateApi(enabled: true, new CountingEvidenceSource(FullyVerified()), new FakeAuthorization(false), recovery, new FakeDurableRecoveryService());

        var result = await api.PreviewAsync(Administrator(), Request(), AllowPage);

        Assert.Equal(TwCategoryAdministrativeApiDisposition.Forbidden, result.Disposition);
        Assert.Empty(result.Plan?.Entries ?? []);
        Assert.Null(result.Plan?.Summary?.Outcomes);
    }

    [Theory]
    [InlineData(TwCategoryRecoveryPlanDisposition.RevisionChanged, TwCategoryAdministrativeApiDisposition.ConflictingRevision)]
    [InlineData(TwCategoryRecoveryPlanDisposition.IncompleteExtraction, TwCategoryAdministrativeApiDisposition.IncompleteExpansion)]
    [InlineData(TwCategoryRecoveryPlanDisposition.Inconsistent, TwCategoryAdministrativeApiDisposition.InconsistentIndex)]
    public async Task PreviewPreservesNonSuccessClassifications(
        TwCategoryRecoveryPlanDisposition planDisposition,
        TwCategoryAdministrativeApiDisposition apiDisposition)
    {
        var recovery = new FakeRecoveryService
        {
            Plan = new(planDisposition, "scope", [])
        };
        var api = CreateApi(enabled: true, new CountingEvidenceSource(FullyVerified()), new FakeAuthorization(true), recovery, new FakeDurableRecoveryService());

        var result = await api.PreviewAsync(Administrator(), Request(), AllowPage);

        Assert.Equal(apiDisposition, result.Disposition);
        Assert.Equal(planDisposition, result.Plan?.Disposition);
    }

    [Fact]
    public async Task BatchOverOneHundredIsRejectedBeforeAuthorizationOrProviderWork()
    {
        var authorization = new FakeAuthorization(true);
        var recovery = new FakeRecoveryService();
        var api = CreateApi(enabled: true, new CountingEvidenceSource(FullyVerified()), authorization, recovery, new FakeDurableRecoveryService());
        var targets = Enumerable.Range(1, 101).Select(id => new TwCategoryRecoveryTarget(id)).ToArray();

        var result = await api.PreviewAsync(Administrator(), new(Guid.NewGuid(), targets), AllowPage);

        Assert.Equal(TwCategoryAdministrativeApiDisposition.InvalidInput, result.Disposition);
        Assert.Equal("RecoveryBatchLimitExceeded", result.DiagnosticCode);
        Assert.Equal(0, authorization.Calls);
        Assert.Equal(0, recovery.PlanCalls);
    }

    [Fact]
    public async Task ExecuteRejectsMissingOrEmptyReplayIdentity()
    {
        var durable = new FakeDurableRecoveryService();
        var api = CreateApi(enabled: true, new CountingEvidenceSource(FullyVerified()), new FakeAuthorization(true), new FakeRecoveryService(), durable);

        var missing = await api.ExecuteAsync(Administrator(), new(null, [new TwCategoryRecoveryTarget(17)]), AllowPage);
        var empty = await api.ExecuteAsync(Administrator(), new(Guid.Empty, [new TwCategoryRecoveryTarget(17)]), AllowPage);

        Assert.Equal(TwCategoryAdministrativeApiDisposition.InvalidInput, missing.Disposition);
        Assert.Equal(TwCategoryAdministrativeApiDisposition.InvalidInput, empty.Disposition);
        Assert.False(durable.Called);
    }

    [Fact]
    public async Task ExecuteDelegatesToDurableServiceAndCarriesTrustedActorOnly()
    {
        var durable = new FakeDurableRecoveryService
        {
            Result = new(TwCategoryDurableRecoveryDisposition.Completed, Guid.Empty, [])
        };
        var authorization = new FakeAuthorization(true);
        var api = CreateApi(enabled: true, new CountingEvidenceSource(FullyVerified()), authorization, new FakeRecoveryService(), durable);

        var result = await api.ExecuteAsync(Administrator(), Request(), AllowPage);

        Assert.Equal(TwCategoryAdministrativeApiDisposition.Success, result.Disposition);
        Assert.True(durable.Called);
        Assert.Equal(TwCategoryAdministrativeOperation.Execute, authorization.LastRequest?.Operation);
        Assert.Equal(ActorId, durable.ActorUserId);
    }

    [Fact]
    public async Task PartialAndCancellationRemainExplicit()
    {
        var partial = new FakeDurableRecoveryService
        {
            Result = new(TwCategoryDurableRecoveryDisposition.PartiallyCompleted, Guid.Empty, [])
        };
        var partialApi = CreateApi(enabled: true, new CountingEvidenceSource(FullyVerified()), new FakeAuthorization(true), new FakeRecoveryService(), partial);
        var partialResult = await partialApi.ExecuteAsync(Administrator(), Request(), AllowPage);

        var cancelledRecovery = new FakeRecoveryService { ThrowCancellation = true };
        var cancelledApi = CreateApi(enabled: true, new CountingEvidenceSource(FullyVerified()), new FakeAuthorization(true), cancelledRecovery, new FakeDurableRecoveryService());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelledResult = await cancelledApi.PreviewAsync(Administrator(), Request(), AllowPage, cancellation.Token);

        Assert.Equal(TwCategoryAdministrativeApiDisposition.PartialResult, partialResult.Disposition);
        Assert.Equal(TwCategoryAdministrativeApiDisposition.Cancellation, cancelledResult.Disposition);
    }

    [Fact]
    public void PreviewAndExecuteRequireAntiForgeryAndStableRoutes()
    {
        var controller = typeof(CategoryRecoveryAdminController);
        Assert.NotNull(controller.GetCustomAttributes(true).OfType<AuthorizeAttribute>().SingleOrDefault());
        Assert.Equal("Admin/CategoryRecovery", controller.GetCustomAttributes(true).OfType<RouteAttribute>().Single().Template);
        Assert.NotNull(controller.GetMethod(nameof(CategoryRecoveryAdminController.Preview))!.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true).SingleOrDefault());
        Assert.NotNull(controller.GetMethod(nameof(CategoryRecoveryAdminController.Execute))!.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true).SingleOrDefault());
        Assert.NotNull(controller.GetMethod(nameof(CategoryRecoveryAdminController.Readiness))!.GetCustomAttributes(typeof(HttpGetAttribute), true).SingleOrDefault());
        Assert.NotNull(controller.GetMethod(nameof(CategoryRecoveryAdminController.Preview))!.GetCustomAttributes(typeof(HttpPostAttribute), true).SingleOrDefault());
        Assert.NotNull(controller.GetMethod(nameof(CategoryRecoveryAdminController.Execute))!.GetCustomAttributes(typeof(HttpPostAttribute), true).SingleOrDefault());
    }

    [Fact]
    public void GrantVerifierBindsActorActionScopeAndValidity()
    {
        var now = DateTime.UtcNow;
        var request = new TwCategoryAdministrativeAuthorizationRequest(
            TwCategoryAdministrativeOperation.Execute,
            ActorId,
            Scope());
        var grant = ValidGrant(request, now);

        Assert.True(TwCategoryAdministrativeGrantVerifier.Evaluate(grant, request, now).Allowed);
        Assert.Equal(TwCategoryAdministrativeAuthorizationReason.ActorMismatch,
            TwCategoryAdministrativeGrantVerifier.Evaluate(grant with { ActorUserId = Guid.NewGuid() }, request, now).Reason);
        Assert.Equal(TwCategoryAdministrativeAuthorizationReason.ActionMismatch,
            TwCategoryAdministrativeGrantVerifier.Evaluate(grant with { Operation = TwCategoryAdministrativeOperation.Preview }, request, now).Reason);
        Assert.Equal(TwCategoryAdministrativeAuthorizationReason.ScopeMismatch,
            TwCategoryAdministrativeGrantVerifier.Evaluate(grant with { Scope = Scope(18) }, request, now).Reason);
        Assert.Equal(TwCategoryAdministrativeAuthorizationReason.GrantExpired,
            TwCategoryAdministrativeGrantVerifier.Evaluate(grant, request, grant.ExpiresAtUtc).Reason);
        Assert.Equal(TwCategoryAdministrativeAuthorizationReason.GrantNotYetValid,
            TwCategoryAdministrativeGrantVerifier.Evaluate(grant, request, grant.NotBeforeUtc.AddSeconds(-1)).Reason);
        Assert.Equal(TwCategoryAdministrativeAuthorizationReason.GrantRevoked,
            TwCategoryAdministrativeGrantVerifier.Evaluate(grant with { RevocationState = TwCategoryAdministrativeGrantRevocationState.Revoked }, request, now).Reason);
        Assert.Equal(TwCategoryAdministrativeAuthorizationReason.MalformedGrant,
            TwCategoryAdministrativeGrantVerifier.Evaluate(grant with { GrantId = Guid.Empty }, request, now).Reason);
    }

    [Fact]
    public void SingleUseGrantIsBlockedWithoutDurableAtomicReplayState()
    {
        var now = DateTime.UtcNow;
        var request = new TwCategoryAdministrativeAuthorizationRequest(TwCategoryAdministrativeOperation.Execute, ActorId, Scope());
        var grant = ValidGrant(request, now) with { ReplayMode = TwCategoryAdministrativeGrantReplayMode.SingleUse };

        var unavailable = TwCategoryAdministrativeGrantVerifier.Evaluate(grant, request, now);
        var replayed = TwCategoryAdministrativeGrantVerifier.Evaluate(grant, request, now, replayAlreadyConsumed: true, durableReplayProtectionAvailable: true);

        Assert.Equal(TwCategoryAdministrativeAuthorizationReason.ReplayProtectionUnavailable, unavailable.Reason);
        Assert.Equal(TwCategoryAdministrativeAuthorizationReason.GrantReplayed, replayed.Reason);
        Assert.False(unavailable.DurableReplayProtection);
        Assert.True(replayed.DurableReplayProtection);
    }

    [Fact]
    public async Task DefaultGrantProviderRejectsWithoutGrantEvidence()
    {
        var provider = new TwCategoryAdministrativeGrantProvider(
            new RejectingTwCategoryAdministrativeGrantEvidenceSource());
        var request = new TwCategoryAdministrativeAuthorizationRequest(
            TwCategoryAdministrativeOperation.Execute,
            ActorId,
            Scope());

        var decision = await provider.EvaluateAsync(request);

        Assert.False(decision.Allowed);
        Assert.Equal(TwCategoryAdministrativeAuthorizationReason.MissingGrant, decision.Reason);
    }

    [Fact]
    public async Task GrantProviderUsesIndependentEvidenceAndVerifier()
    {
        var now = DateTime.UtcNow;
        var request = new TwCategoryAdministrativeAuthorizationRequest(
            TwCategoryAdministrativeOperation.Preview,
            ActorId,
            Scope());
        var provider = new TwCategoryAdministrativeGrantProvider(
            new FixedGrantEvidenceSource(new(
                ValidGrant(request, now),
                DurableReplayProtectionAvailable: false)));

        var decision = await provider.EvaluateAsync(request);

        Assert.True(decision.Allowed);
        Assert.Equal(TwCategoryAdministrativeAuthorizationReason.Granted, decision.Reason);
    }

    [Fact]
    public async Task DisposableOrSourceOnlyReadinessEvidenceCannotAuthorizeProductionExecution()
    {
        var disposableSource = new FixedEvidenceSource(new(
            FullyVerified(),
            TwCategoryOperationalReadinessEvidenceProvenance.DisposableCertification,
            Guid.NewGuid(),
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(5),
            true,
            true,
            "operator4-binding"));
        var durable = new FakeDurableRecoveryService();
        var api = CreateApi(enabled: true, disposableSource, new FakeAuthorization(true), new FakeRecoveryService(), durable);

        var result = await api.ExecuteAsync(Administrator(), Request(), AllowPage);

        Assert.Equal(TwCategoryAdministrativeApiDisposition.MutationGated, result.Disposition);
        Assert.Equal("ProductionReadinessEvidenceRequired", result.DiagnosticCode);
        Assert.False(durable.Called);
    }

    [Fact]
    public void ExpiredProductionReadinessEvidenceIsRejected()
    {
        var now = DateTime.UtcNow;
        var evidence = new TwCategoryOperationalReadinessEvidence(
            FullyVerified(),
            TwCategoryOperationalReadinessEvidenceProvenance.ProductionDeployment,
            Guid.NewGuid(),
            now.AddMinutes(-10),
            now.AddMinutes(-1),
            true,
            true,
            "expired-production-binding");

        var result = TwCategoryOperationalReadinessEvidencePolicy.ValidateForAdministrativeExecution(evidence, now);

        Assert.False(result.Accepted);
        Assert.Equal(TwCategoryOperationalReadinessEvidenceDecision.Expired, result.Decision);
    }

    [Fact]
    public async Task PreviewGrantCannotEscalateIntoExecute()
    {
        var authorization = new FakeAuthorization(false)
        {
            DecisionByOperation = operation => operation == TwCategoryAdministrativeOperation.Preview
        };
        var recovery = new FakeRecoveryService { InvokeOperationAuthorization = true };
        var durable = new FakeDurableRecoveryService();
        var api = CreateApi(enabled: true, new CountingEvidenceSource(FullyVerified()), authorization, recovery, durable);

        var preview = await api.PreviewAsync(Administrator(), Request(), AllowPage);
        var execute = await api.ExecuteAsync(Administrator(), Request(), AllowPage);

        Assert.Equal(TwCategoryAdministrativeApiDisposition.Success, preview.Disposition);
        Assert.Equal(TwCategoryAdministrativeApiDisposition.Forbidden, execute.Disposition);
        Assert.False(durable.MutationCalled);
    }

    [Fact]
    public async Task PagePermissionRevocationBetweenPreviewAndExecuteStopsBeforeMutation()
    {
        var durable = new FakeDurableRecoveryService { InvokePageAuthorization = true };
        var api = CreateApi(enabled: true, new CountingEvidenceSource(FullyVerified()), new FakeAuthorization(true), new FakeRecoveryService(), durable);

        var result = await api.ExecuteAsync(
            Administrator(),
            Request(),
            (_, _) => Task.FromResult(false));

        Assert.Equal(TwCategoryAdministrativeApiDisposition.Forbidden, result.Disposition);
        Assert.True(durable.Called);
        Assert.False(durable.MutationCalled);
    }

    private static TwCategoryAdministrativeCaller Administrator()
        => new(true, true, ActorId);

    private static TwCategoryAdministrativeRecoveryRequest Request()
        => new(Guid.Parse("22222222-2222-2222-2222-222222222222"), [new TwCategoryRecoveryTarget(17, "Page:Trusted")]);

    private static TwCategoryRecoveryScope Scope(int pageId = 17)
    {
        Assert.True(TwCategoryRecoveryScope.TryCreate([new TwCategoryRecoveryTarget(pageId, "Page:Trusted")], out var scope, out var diagnostic), diagnostic);
        return scope;
    }

    private static TwCategoryAdministrativeGrant ValidGrant(
        TwCategoryAdministrativeAuthorizationRequest request,
        DateTime now)
        => new(
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            Guid.Parse("55555555-5555-5555-5555-555555555555"),
            Guid.Parse("66666666-6666-6666-6666-666666666666"),
            request.ActorUserId,
            request.Operation,
            request.Scope,
            now.AddMinutes(-1),
            now.AddMinutes(-1),
            now.AddMinutes(5),
            TwCategoryAdministrativeGrantRevocationState.Active,
            TwCategoryAdministrativeGrantReplayMode.ReusableWithinValidityWindow,
            "test-only-grant-evidence");

    private static Task<bool> AllowPage(TwCategoryRecoveryPageAuthorizationContext context, CancellationToken cancellationToken)
        => Task.FromResult(true);

    private static TwCategoryAdministrativeRecoveryApi CreateApi(
        bool enabled,
        ITwCategoryOperationalReadinessEvidenceSource evidence,
        ITwCategoryAdministrativeAuthorization authorization,
        FakeRecoveryService recovery,
        FakeDurableRecoveryService durable)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [TwCategoryAdministrativeRecoveryApi.EnabledConfigurationKey] = enabled.ToString()
            })
            .Build();
        return new(configuration, new TwCategoryOperationalReadinessService(), evidence, authorization, recovery, durable);
    }

    private static TwCategoryOperationalReadinessInput FullyVerified()
        => new(
            TwCategoryProviderActivationState.ExplicitActivationPending,
            TwCategoryOperationalEvidenceStatus.Verified,
            TwCategoryOperationalEvidenceStatus.Verified,
            TwCategoryOperationalEvidenceStatus.Verified,
            TwCategoryOperationalEvidenceStatus.Verified,
            TwCategoryOperationalEvidenceStatus.Verified,
            TwCategoryOperationalEvidenceStatus.Verified,
            TwCategoryOperationalEvidenceStatus.Verified,
            TwCategoryOperationalEvidenceStatus.Verified,
            TwCategoryOperationalEvidenceStatus.Verified,
            TwCategoryOperationalEvidenceStatus.Verified,
            TwCategoryOperationalEvidenceStatus.Verified,
            TwCategoryOperationalEvidenceStatus.Verified,
            TwCategoryOperationalEvidenceOrigin.DisposableCertification,
            RecoveryIndeterminate: false,
            DatabaseContactedDuringEvaluation: false);

    private sealed class CountingEvidenceSource(TwCategoryOperationalReadinessInput input)
        : ITwCategoryOperationalReadinessEvidenceSource
    {
        public int Reads { get; private set; }

        public TwCategoryOperationalReadinessInput Get()
        {
            Reads++;
            return input;
        }

        public TwCategoryOperationalReadinessEvidence GetEvidence()
        {
            Reads++;
            var now = DateTime.UtcNow;
            return new(
                input,
                TwCategoryOperationalReadinessEvidenceProvenance.ProductionDeployment,
                Guid.Parse("33333333-3333-3333-3333-333333333333"),
                now.AddMinutes(-1),
                now.AddMinutes(5),
                TargetBound: true,
                IndependentlySourced: true,
                TargetBindingHash: "test-production-binding");
        }
    }

    private sealed class FixedEvidenceSource(TwCategoryOperationalReadinessEvidence evidence)
        : ITwCategoryOperationalReadinessEvidenceSource
    {
        public TwCategoryOperationalReadinessInput Get() => evidence.Input;
        public TwCategoryOperationalReadinessEvidence GetEvidence() => evidence;
    }

    private sealed class FixedGrantEvidenceSource(TwCategoryAdministrativeGrantEvidenceResult result)
        : ITwCategoryAdministrativeGrantEvidenceSource
    {
        public Task<TwCategoryAdministrativeGrantEvidenceResult> GetAsync(
            TwCategoryAdministrativeAuthorizationRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }

    private sealed class FakeAuthorization(bool allowed) : ITwCategoryAdministrativeAuthorization
    {
        public int Calls { get; private set; }
        public TwCategoryAdministrativeAuthorizationRequest? LastRequest { get; private set; }
        public Func<TwCategoryAdministrativeOperation, bool>? DecisionByOperation { get; init; }

        public Task<TwCategoryAdministrativeAuthorizationDecision> EvaluateAsync(TwCategoryAdministrativeAuthorizationRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = request;
            var isAllowed = DecisionByOperation?.Invoke(request.Operation) ?? allowed;
            return Task.FromResult(new TwCategoryAdministrativeAuthorizationDecision(
                isAllowed,
                isAllowed
                    ? TwCategoryAdministrativeAuthorizationReason.Granted
                    : TwCategoryAdministrativeAuthorizationReason.MissingGrant));
        }
    }

    private sealed class FakeRecoveryService : ITwCategoryRecoveryService
    {
        public TwCategoryRecoveryPlan Plan { get; init; } = new(TwCategoryRecoveryPlanDisposition.NoChange, "scope", []);
        public int PlanCalls { get; private set; }
        public bool InvokeOperationAuthorization { get; init; }
        public bool ThrowCancellation { get; init; }

        public async Task<TwCategoryRecoveryPlan> PlanAsync(
            TwCategoryRecoveryPlanRequest request,
            Func<TwCategoryRecoveryOperationAuthorizationContext, CancellationToken, Task<bool>>? authorizeOperation,
            Func<TwCategoryRecoveryPageAuthorizationContext, CancellationToken, Task<bool>>? authorizePage,
            CancellationToken cancellationToken = default)
        {
            PlanCalls++;
            if (ThrowCancellation)
                throw new OperationCanceledException(cancellationToken);
            if (InvokeOperationAuthorization && authorizeOperation is not null
                && !await authorizeOperation(new(TwCategoryRecoveryOperation.Preview, request.ActorUserId), cancellationToken))
                return new(TwCategoryRecoveryPlanDisposition.Unauthorized, "scope", []);
            return Plan;
        }

        public Task<TwCategoryRecoveryExecutionResult> ExecuteAsync(
            TwCategoryRecoveryExecutionRequest request,
            Func<TwCategoryRecoveryOperationAuthorizationContext, CancellationToken, Task<bool>>? authorizeOperation,
            Func<TwCategoryRecoveryPageAuthorizationContext, CancellationToken, Task<bool>>? authorizePage,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeDurableRecoveryService : ITwCategoryDurableRecoveryService
    {
        public bool Called { get; private set; }
        public bool MutationCalled { get; private set; }
        public Guid? ActorUserId { get; private set; }
        public bool InvokePageAuthorization { get; init; }
        public TwCategoryDurableRecoveryExecutionResult Result { get; init; } = new(TwCategoryDurableRecoveryDisposition.Completed, Guid.Empty, []);

        public async Task<TwCategoryDurableRecoveryExecutionResult> ExecuteAsync(
            TwCategoryRecoveryDurableExecutionRequest request,
            Func<TwCategoryRecoveryOperationAuthorizationContext, CancellationToken, Task<bool>>? authorizeOperation,
            Func<TwCategoryRecoveryPageAuthorizationContext, CancellationToken, Task<bool>>? authorizePage,
            CancellationToken cancellationToken = default)
        {
            Called = true;
            ActorUserId = request.ActorUserId;
            if (authorizeOperation is not null && !await authorizeOperation(new(TwCategoryRecoveryOperation.Execute, request.ActorUserId), cancellationToken))
                return new(TwCategoryDurableRecoveryDisposition.Unauthorized, request.OperationId, []);
            if (InvokePageAuthorization && authorizePage is not null && request.Targets.Count > 0)
            {
                var target = request.Targets[0];
                var pageAllowed = await authorizePage(
                    new(TwCategoryRecoveryOperation.Execute, target.PageId, target.ExpectedNavigation ?? "Page:Trusted", 4, request.ActorUserId),
                    cancellationToken);
                if (!pageAllowed)
                    return new(TwCategoryDurableRecoveryDisposition.Unauthorized, request.OperationId, []);
            }
            MutationCalled = true;
            return Result with { OperationId = request.OperationId };
        }
    }
}
