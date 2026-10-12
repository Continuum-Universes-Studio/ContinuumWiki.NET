using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using TightWiki.Engine.MediaWiki;
using TightWiki.Library.Database;
using TightWiki.Plugin.Interfaces.Persistence;
using TightWiki.Plugin.Models;
using TightWiki.Repository.Helpers;

namespace TightWiki.Tests.Integration;

/// <summary>
/// Bounded live certification for independent MariaDB connections. Test rows
/// use unique identifiers and are retained because the current v2 approval
/// does not authorize database cleanup.
/// </summary>
[Collection("CW-M5-019 serial")]
public sealed class MediaWikiCategoryConcurrencyCrashReplayAuthorizationTests
{
    [M5CategoryRecoveryFact]
    public async Task IndependentConnectionsSerializeCategoryReplacementWithoutTornRows()
    {
        var connectionString = M5_019AuthorizationGate.RequireApprovedConnectionString();
        var suffix = Guid.NewGuid().ToString("N");
        var factoryA = CreateUnpooledFactory(connectionString);
        var factoryB = CreateUnpooledFactory(connectionString);
        var pageRepository = new MySqlPageRepository(factoryA);
        var page = new TwPage
        {
            Name = $"Tests::m5-020-concurrent-{suffix}",
            Description = "CW-M5-020 retained concurrency fixture",
            Body = "bounded concurrency fixture",
            CreatedByUserId = Guid.NewGuid(),
            ModifiedByUserId = Guid.NewGuid()
        };
        var pageId = await pageRepository.CreatePageAsync(page);
        var current = await pageRepository.GetPageByIdAsync(pageId);
        Assert.NotNull(current);
        var revision = current!.Revision;
        var first = Replacement(pageId, revision, $"m5-020-a-{suffix}");
        var second = Replacement(pageId, revision, $"m5-020-b-{suffix}");
        using var barrier = new Barrier(2);
        var firstRepository = new MySqlCategoryMembershipRepository(factoryA);
        var secondRepository = new MySqlCategoryMembershipRepository(factoryB);

        var results = await Task.WhenAll(
            Task.Run(async () =>
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(10));
                return await firstRepository.ReplaceMembershipsAsync(first);
            }),
            Task.Run(async () =>
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(10));
                return await secondRepository.ReplaceMembershipsAsync(second);
            }));

        Assert.All(results, result => Assert.Equal(TwCategoryMembershipReplacementDisposition.Applied, result.Disposition));
        var memberships = await firstRepository.GetMembershipsByPageIdAsync(pageId);
        Assert.Single(memberships);
        Assert.Contains(memberships[0].Category.NormalizedTitle, new[] { first.DesiredMemberships[0].Category.NormalizedTitle, second.DesiredMemberships[0].Category.NormalizedTitle });
        var state = await firstRepository.GetIndexConsistencyAsync(pageId);
        Assert.Equal(TwCategoryIndexConsistencyState.Current, state.State);
        Assert.Equal(revision, state.IndexedPageRevision);
        Assert.Equal(memberships[0].IndexGeneration, state.Provenance!.IndexGeneration);
    }

    [M5CategoryRecoveryFact]
    public async Task StaleRevisionIsRejectedAfterIndependentAuthoritativeRead()
    {
        var connectionString = M5_019AuthorizationGate.RequireApprovedConnectionString();
        var factory = CreateUnpooledFactory(connectionString);
        var pageRepository = new MySqlPageRepository(factory);
        var suffix = Guid.NewGuid().ToString("N");
        var page = new TwPage
        {
            Name = $"Tests::m5-020-stale-{suffix}",
            Description = "CW-M5-020 retained stale revision fixture",
            Body = "initial",
            CreatedByUserId = Guid.NewGuid(),
            ModifiedByUserId = Guid.NewGuid()
        };
        var pageId = await pageRepository.CreatePageAsync(page);
        var observed = await pageRepository.GetPageByIdAsync(pageId);
        Assert.NotNull(observed);
        var competing = new TwPage
        {
            Id = pageId,
            Revision = observed!.Revision,
            Name = observed.Name,
            Description = observed.Description,
            Body = "authoritative update",
            CreatedByUserId = observed.CreatedByUserId,
            ModifiedByUserId = Guid.NewGuid(),
            ChangeSummary = "CW-M5-020 stale revision fixture"
        };
        await pageRepository.UpdatePageAsync(competing, observed.Revision);

        var result = await new MySqlCategoryMembershipRepository(factory)
            .ReplaceMembershipsAsync(Replacement(pageId, observed.Revision, $"m5-020-stale-category-{suffix}"));

        Assert.Equal(TwCategoryMembershipReplacementDisposition.RevisionConflict, result.Disposition);
        Assert.Empty(await new MySqlCategoryMembershipRepository(factory).GetMembershipsByPageIdAsync(pageId));
    }

    [M5CategoryRecoveryFact]
    public async Task ConcurrentCheckpointCasHasOneWinnerAndDurableReadback()
    {
        var connectionString = M5_019AuthorizationGate.RequireApprovedConnectionString();
        var storeA = new MySqlCategoryRecoveryDurabilityStore(CreateUnpooledFactory(connectionString));
        var storeB = new MySqlCategoryRecoveryDurabilityStore(CreateUnpooledFactory(connectionString));
        var operation = NewOperation(17);
        Assert.Equal(TwCategoryRecoveryDurabilityDisposition.Created,
            (await storeA.CreateOrReplayOperationAsync(operation)).Disposition);
        var audit = Outcome(operation, "m5-020-cas-outcome");
        var appended = await storeA.AppendAuditEventAsync(audit);
        Assert.Equal(TwCategoryRecoveryDurabilityDisposition.Appended, appended.Disposition);

        var request = new TwCategoryRecoveryCheckpointAdvanceRequest(
            operation.OperationId,
            operation.Scope.Fingerprint,
            0,
            0,
            0,
            1,
            appended.EventSequence!.Value,
            audit.EventKey,
            1);
        var results = await Task.WhenAll(
            Task.Run(() => storeA.AdvanceCheckpointAsync(request)),
            Task.Run(() => storeB.AdvanceCheckpointAsync(request)));

        Assert.Single(results, result => result.Disposition == TwCategoryRecoveryDurabilityDisposition.Advanced);
        Assert.Single(results, result => result.Disposition == TwCategoryRecoveryDurabilityDisposition.GenerationConflict);
        var progress = await storeA.ReadScopedProgressAsync(new(operation.OperationId, operation.Scope.Fingerprint, null));
        Assert.Equal(TwCategoryRecoveryDurabilityDisposition.ProgressRead, progress.Disposition);
        Assert.Equal(1, progress.Progress!.Checkpoint.CommittedPosition);
    }

    [M5CategoryRecoveryFact]
    public async Task ConcurrentAuditReplayIsIdempotentAndConflictingPayloadFailsClosed()
    {
        var connectionString = M5_019AuthorizationGate.RequireApprovedConnectionString();
        var storeA = new MySqlCategoryRecoveryDurabilityStore(CreateUnpooledFactory(connectionString));
        var storeB = new MySqlCategoryRecoveryDurabilityStore(CreateUnpooledFactory(connectionString));
        var operation = NewOperation(18);
        Assert.Equal(TwCategoryRecoveryDurabilityDisposition.Created,
            (await storeA.CreateOrReplayOperationAsync(operation)).Disposition);
        var audit = Outcome(operation, "m5-020-audit-replay");

        var results = await Task.WhenAll(
            Task.Run(() => storeA.AppendAuditEventAsync(audit)),
            Task.Run(() => storeB.AppendAuditEventAsync(audit)));
        Assert.Single(results, result => result.Disposition == TwCategoryRecoveryDurabilityDisposition.Appended);
        Assert.Single(results, result => result.Disposition == TwCategoryRecoveryDurabilityDisposition.DuplicateSame);

        var conflict = await storeA.AppendAuditEventAsync(audit with { DiagnosticCode = "different-safe-code" });
        Assert.Equal(TwCategoryRecoveryDurabilityDisposition.DuplicateConflict, conflict.Disposition);
        await using var session = await CreateUnpooledFactory(connectionString).OpenSessionAsync();
        var count = await session.Connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM TwCategoryRecoveryAuditEvent WHERE OperationId = @OperationId AND EventKey = @EventKey;",
            new { OperationId = operation.OperationId.ToString("D"), audit.EventKey });
        Assert.Equal(1, count);
    }

    [M5CategoryRecoveryFact]
    public async Task ApplicationFaultAfterCheckpointUsesRealDurabilityStoreOnResume()
    {
        var connectionString = M5_019AuthorizationGate.RequireApprovedConnectionString();
        var store = new MySqlCategoryRecoveryDurabilityStore(CreateUnpooledFactory(connectionString));
        var operation = NewOperation(19);
        var recovery = new DeterministicRecoveryService();
        var first = new TwCategoryDurableRecoveryService(
            recovery,
            store,
            NullLogger<TwCategoryDurableRecoveryService>.Instance,
            new ThrowOnceFaultInjector(TwCategoryRecoveryFaultBoundary.AfterCheckpointCommitBeforeAcknowledgement));

        await Assert.ThrowsAsync<InjectedApplicationInterruption>(() => first.ExecuteAsync(
            new(operation.OperationId, [new TwCategoryRecoveryTarget(19)], ActorUserId: null), AllowOperation, AllowPage));

        var resumed = await new TwCategoryDurableRecoveryService(
            recovery,
            store,
            NullLogger<TwCategoryDurableRecoveryService>.Instance)
            .ExecuteAsync(new(operation.OperationId, [new TwCategoryRecoveryTarget(19)], ActorUserId: null), AllowOperation, AllowPage);

        Assert.Equal(TwCategoryDurableRecoveryDisposition.Completed, resumed.Disposition);
        Assert.Equal(1, resumed.Checkpoint!.CommittedPosition);
        Assert.Equal(1, recovery.Calls);
    }

    private static MySqlDatabaseConnectionFactory CreateUnpooledFactory(string connectionString)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString) { Pooling = false };
        return new(builder.ConnectionString);
    }

    private static TwCategoryMembershipReplacementRequest Replacement(int pageId, int revision, string categoryTitle)
    {
        Assert.True(TwCategoryIdentity.TryCreate(categoryTitle, out var category));
        var generation = Guid.NewGuid();
        var indexedAt = DateTime.UtcNow;
        return new(pageId, revision,
            [new TwCategoryMembershipRecord(pageId, category, null, revision, "m5-020-page", "m5-020-revision", "continuumwiki-test", generation, indexedAt)],
            true, generation, indexedAt,
            "m5-020-page", "m5-020-revision", "continuumwiki-test");
    }

    private static TwCategoryRecoveryOperationRecord NewOperation(int pageId)
    {
        var scope = TwCategoryRecoveryScope.TryCreate([new TwCategoryRecoveryTarget(pageId)], out var created, out var error)
            ? created
            : throw new InvalidOperationException(error);
        var operationId = Guid.NewGuid();
        return new(operationId, scope, null, DateTime.UtcNow, TwCategoryRecoveryDurabilityConstants.PolicyVersion,
            TwCategoryRecoveryOperationStatus.Planned,
            new(operationId, scope.Fingerprint, 0, 0, 0));
    }

    private static TwCategoryRecoveryAuditEvent Outcome(TwCategoryRecoveryOperationRecord operation, string key)
        => new(Guid.NewGuid(), key, operation.OperationId, 0, Guid.NewGuid(),
            TwCategoryRecoveryAuditEventKind.NoChangeObserved, TwCategoryRecoveryAuditConfidence.Observation,
            operation.Scope.Targets[0].PageId, 1, TwCategoryRecoveryExecutionDisposition.NoChange, null, DateTime.UtcNow);

    private static Task<bool> AllowOperation(TwCategoryRecoveryOperationAuthorizationContext _, CancellationToken __) => Task.FromResult(true);
    private static Task<bool> AllowPage(TwCategoryRecoveryPageAuthorizationContext _, CancellationToken __) => Task.FromResult(true);

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

    private sealed class DeterministicRecoveryService : ITwCategoryRecoveryService
    {
        public int Calls { get; private set; }

        public Task<TwCategoryRecoveryPlan> PlanAsync(TwCategoryRecoveryPlanRequest request, Func<TwCategoryRecoveryOperationAuthorizationContext, CancellationToken, Task<bool>>? authorizeOperation, Func<TwCategoryRecoveryPageAuthorizationContext, CancellationToken, Task<bool>>? authorizePage, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async Task<TwCategoryRecoveryExecutionResult> ExecuteAsync(TwCategoryRecoveryExecutionRequest request, Func<TwCategoryRecoveryOperationAuthorizationContext, CancellationToken, Task<bool>>? authorizeOperation, Func<TwCategoryRecoveryPageAuthorizationContext, CancellationToken, Task<bool>>? authorizePage, CancellationToken cancellationToken = default)
        {
            Calls++;
            var target = request.Targets.Single();
            if (authorizePage is not null && !await authorizePage(new(TwCategoryRecoveryOperation.Execute, target.PageId, target.ExpectedNavigation ?? $"article::m5-020-{target.PageId}", 1, request.ActorUserId), cancellationToken))
                return new(TwCategoryRecoveryExecutionDisposition.Unauthorized, []);
            return new(TwCategoryRecoveryExecutionDisposition.NoChange,
                [new TwCategoryRecoveryExecutionEntry(target.PageId, target.ExpectedNavigation, 1, 1, TwCategoryRecoveryExecutionDisposition.NoChange)]);
        }
    }
}
