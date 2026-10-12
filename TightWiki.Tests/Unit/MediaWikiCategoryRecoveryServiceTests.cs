using Microsoft.Extensions.Logging.Abstractions;
using TightWiki.Engine.MediaWiki;
using TightWiki.Plugin.Interfaces.Persistence;
using TightWiki.Plugin.Models;

namespace TightWiki.Tests.Unit;

public sealed class MediaWikiCategoryRecoveryServiceTests
{
    [Fact]
    public async Task PlanningRequiresOperationAndPageAuthorization()
    {
        var source = new FakePageSource(ResolvedPage(4));
        var service = Create(source, new FakeRepository(), new FakeApplicationService());

        var result = await service.PlanAsync(
            new([new TwCategoryRecoveryTarget(17)]),
            null,
            (_, _) => Task.FromResult(true));

        Assert.Equal(TwCategoryRecoveryPlanDisposition.Unauthorized, result.Disposition);
        Assert.Empty(result.Entries);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task PlanningRejectsUnboundedAndDuplicateTargets()
    {
        var service = Create(new FakePageSource(ResolvedPage(4)), new FakeRepository(), new FakeApplicationService());
        var tooMany = Enumerable.Range(1, TwCategoryRecoveryLimits.MaxBatchPages + 1)
            .Select(id => new TwCategoryRecoveryTarget(id)).ToArray();

        var limit = await service.PlanAsync(new(tooMany), AllowOperation, AllowPage);
        var duplicate = await service.PlanAsync(new([new TwCategoryRecoveryTarget(1), new TwCategoryRecoveryTarget(1)]), AllowOperation, AllowPage);

        Assert.Equal("RecoveryBatchLimitExceeded", limit.DiagnosticCode);
        Assert.Equal("DuplicateRecoveryTarget", duplicate.DiagnosticCode);
    }

    [Fact]
    public async Task PlanningDistinguishesNoChangeFromRequiredReindex()
    {
        var page = ResolvedPage(4);
        var repository = new FakeRepository(
            new TwCategoryIndexConsistencyResult(TwCategoryIndexConsistencyState.Current, 17, 4, 4),
            [Membership(17, 4)]);
        var service = Create(new FakePageSource(page), repository, new FakeApplicationService());

        var noChange = await service.PlanAsync(new([new TwCategoryRecoveryTarget(17)]), AllowOperation, AllowPage);
        repository.Existing = [];
        var needsReindex = await service.PlanAsync(new([new TwCategoryRecoveryTarget(17)]), AllowOperation, AllowPage);

        Assert.Equal(TwCategoryRecoveryPlanDisposition.NoChange, noChange.Entries.Single().Disposition);
        Assert.Equal(TwCategoryRecoveryPlanDisposition.NeedsReindex, needsReindex.Entries.Single().Disposition);
    }

    [Fact]
    public async Task PlanningDistinguishesVerifiedEmptyFromUnindexed()
    {
        var emptyPlan = new MediaWikiCategoryIndexPlan(MediaWikiCategoryIndexCompleteness.Complete, [], [], false, true);
        var repository = new FakeRepository(new(TwCategoryIndexConsistencyState.Current, 17, 4, 4));
        var service = Create(new FakePageSource(ResolvedPage(4)), repository, new FakeApplicationService(), new FakeExtractor(emptyPlan));
        var indexedEmpty = await service.PlanAsync(new([new TwCategoryRecoveryTarget(17)]), AllowOperation, AllowPage);

        repository.Consistency = new(TwCategoryIndexConsistencyState.Unindexed, 17, 4, null);
        var unindexed = await service.PlanAsync(new([new TwCategoryRecoveryTarget(17)]), AllowOperation, AllowPage);

        Assert.Equal(TwCategoryRecoveryPlanDisposition.IndexedEmpty, indexedEmpty.Entries.Single().Disposition);
        Assert.Equal(TwCategoryRecoveryPlanDisposition.Unindexed, unindexed.Entries.Single().Disposition);
    }

    [Fact]
    public async Task UnauthorizedPageIsRedactedAndNotExtracted()
    {
        var source = new FakePageSource(ResolvedPage(4));
        var extractor = new FakeExtractor(CompletePlan());
        var service = Create(source, new FakeRepository(), new FakeApplicationService(), extractor);

        var result = await service.PlanAsync(
            new([new TwCategoryRecoveryTarget(17)]),
            AllowOperation,
            (_, _) => Task.FromResult(false));

        var entry = Assert.Single(result.Entries);
        Assert.Null(entry.PageId);
        Assert.Null(entry.PageNavigation);
        Assert.Equal(TwCategoryRecoveryPlanDisposition.Unauthorized, entry.Disposition);
        Assert.Equal(0, extractor.Calls);
    }

    [Fact]
    public async Task DryRunDoesNotInvokeTheMutationService()
    {
        var application = new FakeApplicationService();
        var service = Create(new FakePageSource(ResolvedPage(4)), new FakeRepository(), application, new FakeExtractor(new(
            MediaWikiCategoryIndexCompleteness.Incomplete,
            [],
            ["MissingTemplate"],
            PreserveExistingMembership: true,
            HasCompleteSourceProvenance: true)));

        var plan = await service.PlanAsync(new([new TwCategoryRecoveryTarget(17)]), AllowOperation, AllowPage);
        Assert.Equal(TwCategoryRecoveryPlanDisposition.IncompleteExtraction, plan.Entries.Single().Disposition);
        Assert.Equal(0, application.Calls);
    }

    [Fact]
    public async Task ExecutionUsesFreshCurrentRevisionRatherThanPlanRevision()
    {
        var source = new FakePageSource(ResolvedPage(4));
        var application = new FakeApplicationService(new(TwCategoryReconciliationDisposition.Reconciled, 17, 4, 9, new(TwCategoryIndexingDisposition.Applied, 17, 9), TwCategoryRecoveryEligibility.NotEligible));
        var service = Create(source, new FakeRepository(), application);

        var plan = await service.PlanAsync(new([new TwCategoryRecoveryTarget(17)]), AllowOperation, AllowPage);
        Assert.Equal(4, plan.Entries.Single().CurrentRevision);
        source.Page = ResolvedPage(9);

        var result = await service.ExecuteAsync(new([new TwCategoryRecoveryTarget(17)]), AllowOperation, AllowPage);

        Assert.Equal(TwCategoryRecoveryExecutionDisposition.Reindexed, result.Disposition);
        Assert.Equal(9, application.LastRequest!.ExpectedPageRevision);
        Assert.Equal(9, result.Entries.Single().ResolvedRevision);
    }

    [Fact]
    public async Task RejectingRepositoryIsUnavailableAndNeverInvokesMutationService()
    {
        var repository = new FakeRepository { ThrowUnavailable = true };
        var application = new FakeApplicationService();
        var service = Create(new FakePageSource(ResolvedPage(4)), repository, application);

        var result = await service.PlanAsync(new([new TwCategoryRecoveryTarget(17)]), AllowOperation, AllowPage);

        Assert.Equal(TwCategoryRecoveryPlanDisposition.RepositoryUnavailable, result.Entries.Single().Disposition);
        Assert.Equal(0, application.Calls);
    }

    [Fact]
    public async Task MissingOrDeletedPageIsRedactedAsPageMissing()
    {
        var source = new FakePageSource(ResolvedPage(4)) { Missing = true };
        var service = Create(source, new FakeRepository(), new FakeApplicationService());

        var result = await service.PlanAsync(new([new TwCategoryRecoveryTarget(17)]), AllowOperation, AllowPage);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(TwCategoryRecoveryPlanDisposition.PageMissing, entry.Disposition);
        Assert.Null(entry.PageId);
        Assert.Null(entry.PageNavigation);
    }

    [Fact]
    public async Task CheckpointCannotResumeAChangedScope()
    {
        var service = Create(new FakePageSource(ResolvedPage(4)), new FakeRepository(), new FakeApplicationService());
        var result = await service.ExecuteAsync(
            new([new TwCategoryRecoveryTarget(17), new TwCategoryRecoveryTarget(18)], new("wrong", 1)),
            AllowOperation,
            AllowPage);

        Assert.Equal(TwCategoryRecoveryExecutionDisposition.InvalidRequest, result.Disposition);
        Assert.Equal("InvalidRecoveryCheckpoint", result.DiagnosticCode);
    }

    [Fact]
    public async Task BatchContinuesAfterPerPageFailureAndPreservesCheckpoint()
    {
        var source = new FakePageSource(ResolvedPage(4));
        var application = new FakeApplicationService(new(TwCategoryReconciliationDisposition.Failed, 17, 4, 4, new(TwCategoryIndexingDisposition.PersistenceFailure, 17, 4), TwCategoryRecoveryEligibility.EligibleAfterNewAuthorization));
        var service = Create(source, new FakeRepository(), application);

        var result = await service.ExecuteAsync(
            new([new TwCategoryRecoveryTarget(18), new TwCategoryRecoveryTarget(17)]),
            AllowOperation,
            AllowPage);

        Assert.Equal(TwCategoryRecoveryExecutionDisposition.PersistenceFailure, result.Disposition);
        Assert.Equal(2, result.Entries.Count);
        Assert.Equal(2, result.NextCheckpoint!.NextTargetIndex);
    }

    [Fact]
    public async Task CancellationIsReturnedWithoutClaimingAtomicBatch()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = Create(new FakePageSource(ResolvedPage(4)), new FakeRepository(), new FakeApplicationService());

        var result = await service.ExecuteAsync(new([new TwCategoryRecoveryTarget(17)]), AllowOperation, AllowPage, cancellation.Token);

        Assert.Equal(TwCategoryRecoveryExecutionDisposition.Cancelled, result.Disposition);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task DurableModeCannotSilentlyFallBackToVolatileExecution()
    {
        var source = new FakePageSource(ResolvedPage(4));
        var service = Create(source, new FakeRepository(), new FakeApplicationService());

        var result = await service.ExecuteAsync(
            new([new TwCategoryRecoveryTarget(17)], Mode: TwCategoryRecoveryExecutionMode.DurableExecutionRequested),
            AllowOperation,
            AllowPage);

        Assert.Equal(TwCategoryRecoveryExecutionDisposition.InvalidRequest, result.Disposition);
        Assert.Equal("DurableExecutionRequiresDurableService", result.DiagnosticCode);
        Assert.Equal(0, source.Calls);
    }

    private static TwCategoryRecoveryService Create(
        ITwCategoryRecoveryPageSource source,
        ITwCategoryMembershipRepository repository,
        ITwCategoryMembershipApplicationService application,
        ITwCategoryIndexExtractor? extractor = null)
        => new(source, extractor ?? new FakeExtractor(CompletePlan()), repository, application, NullLogger<TwCategoryRecoveryService>.Instance);

    private static Task<bool> AllowOperation(TwCategoryRecoveryOperationAuthorizationContext _, CancellationToken __) => Task.FromResult(true);
    private static Task<bool> AllowPage(TwCategoryRecoveryPageAuthorizationContext _, CancellationToken __) => Task.FromResult(true);

    private static TwCategoryIndexAuthoritativePage ResolvedPage(int revision)
        => new(17, "article::example", revision, revision, "[[Category:Divines]]", new(TwPageSourceFormat.ImportedMediaWiki, 9, "source-page", "source-revision", "Example", TargetPageId: 17, TargetRevision: revision));

    private static MediaWikiCategoryIndexPlan CompletePlan()
        => new(MediaWikiCategoryIndexCompleteness.Complete, [new("source-page", "source-revision", "Example", "Divines", "category::divines", null, null, "source-wiki", 0)], [], false, true);

    private static TwCategoryMembershipRecord Membership(int pageId, int revision)
        => new(pageId, TwCategoryIdentity.TryCreate("Divines", out var category) ? category : throw new InvalidOperationException(), null, revision, "source-page", "source-revision", "source-wiki", Guid.NewGuid(), DateTime.UtcNow);

    private sealed class FakePageSource(TwCategoryIndexAuthoritativePage page) : ITwCategoryRecoveryPageSource
    {
        public TwCategoryIndexAuthoritativePage Page { get; set; } = page;
        public bool Missing { get; set; }
        public int Calls { get; private set; }
        public Task<TwCategoryIndexPageSourceResult> ResolveCurrentAsync(TwCategoryRecoveryTarget target, TwCategoryIndexMutationKind mutationKind, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (Missing)
                return Task.FromResult(new TwCategoryIndexPageSourceResult(TwCategoryIndexPageSourceDisposition.SourceUnavailable, DiagnosticCode: "SourcePageUnavailable"));
            return Task.FromResult(new TwCategoryIndexPageSourceResult(TwCategoryIndexPageSourceDisposition.Resolved, Page with { PageId = target.PageId }));
        }
    }

    private sealed class FakeExtractor(MediaWikiCategoryIndexPlan plan) : ITwCategoryIndexExtractor
    {
        public int Calls { get; private set; }
        public Task<MediaWikiCategoryIndexPlan> ExtractAsync(TwCategoryIndexAuthoritativePage page, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(plan);
        }
    }

    private sealed class FakeRepository(
        TwCategoryIndexConsistencyResult? consistency = null,
        IReadOnlyList<TwCategoryMembershipRecord>? existing = null) : ITwCategoryMembershipRepository
    {
        public bool ThrowUnavailable { get; set; }
        public TwCategoryIndexConsistencyResult Consistency { get; set; } = consistency ?? new(TwCategoryIndexConsistencyState.Current, 17, 4, 4);
        public IReadOnlyList<TwCategoryMembershipRecord> Existing { get; set; } = existing ?? [];
        public Task<TwCategoryIndexConsistencyResult> GetIndexConsistencyAsync(int pageId, CancellationToken cancellationToken = default)
            => ThrowUnavailable ? Task.FromException<TwCategoryIndexConsistencyResult>(new NotSupportedException()) : Task.FromResult(Consistency with { PageId = pageId });
        public Task<TwCategoryIndexConsistencyResult> GetCategoryIndexConsistencyAsync(TwCategoryIdentity category, CancellationToken cancellationToken = default) => Task.FromResult(Consistency);
        public Task<IReadOnlyList<TwCategoryMembershipRecord>> GetMembershipsByPageIdAsync(int pageId, CancellationToken cancellationToken = default) => Task.FromResult(Existing);
        public Task<TwCategoryMembershipReplacementResult> ReplaceMembershipsAsync(TwCategoryMembershipReplacementRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new TwCategoryMembershipReplacementResult(TwCategoryMembershipReplacementDisposition.Applied, request.PageId));
        public Task<TwCategoryMemberPage> GetMembersAsync(TwCategoryMemberQuery query, CancellationToken cancellationToken = default) => Task.FromResult(new TwCategoryMemberPage([], false));
        public Task<TwCategoryMembershipReplacementResult> InvalidateMembershipsAsync(int pageId, int expectedPageRevision, CancellationToken cancellationToken = default) => Task.FromResult(new TwCategoryMembershipReplacementResult(TwCategoryMembershipReplacementDisposition.Applied, pageId, expectedPageRevision));
    }

    private sealed class FakeApplicationService(TwCategoryReconciliationResult? result = null) : ITwCategoryMembershipApplicationService
    {
        public int Calls { get; private set; }
        public TwCategoryReconciliationRequest? LastRequest { get; private set; }
        private readonly TwCategoryReconciliationResult _result = result ?? new(TwCategoryReconciliationDisposition.Reconciled, 17, 4, 4, new(TwCategoryIndexingDisposition.Applied, 17, 4), TwCategoryRecoveryEligibility.NotEligible);
        public Task<TwCategoryReconciliationResult> ReconcileAsync(TwCategoryReconciliationRequest request, Func<TwCategoryIndexAuthorizationContext, CancellationToken, Task<bool>>? authorize, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(_result with { PageId = request.PageId });
        }
        public Task<TwCategoryIndexingResult> IndexAfterCommittedMutationAsync(TwCategoryIndexingRequest request, Func<TwCategoryIndexAuthorizationContext, CancellationToken, Task<bool>>? authorize, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TwCategoryIndexingResult> InvalidateAfterCommittedDeletionAsync(TwCategoryIndexingRequest request, Func<TwCategoryIndexAuthorizationContext, CancellationToken, Task<bool>>? authorize, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
