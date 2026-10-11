using TightWiki.Engine.MediaWiki;
using TightWiki.Plugin.Interfaces.Persistence;
using TightWiki.Plugin.Models;

namespace TightWiki.Tests.Unit;

public sealed class MediaWikiCategoryApplicationServiceTests
{
    [Fact]
    public async Task AuthorizedCommittedMutationExtractsThenReplacesInOrder()
    {
        var events = new List<string> { "page-published" };
        var source = new FakeSource(events, ResolvedPage());
        var extractor = new FakeExtractor(events, CompletePlan());
        var repository = new FakeMembershipRepository(events);
        var service = new TwCategoryMembershipApplicationService(source, extractor, repository);

        var result = await service.IndexAfterCommittedMutationAsync(
            Request(),
            (context, _) =>
            {
                events.Add("authorized");
                Assert.Equal(TwCategoryIndexMutationKind.PageMutation, context.MutationKind);
                Assert.Equal(4, context.ExpectedPageRevision);
                return Task.FromResult(true);
            });

        Assert.Equal(TwCategoryIndexingDisposition.Applied, result.Disposition);
        Assert.Equal(TwCategoryIndexConsistencyState.Unknown, result.ConsistencyState);
        Assert.Equal(TwCategoryRecoveryEligibility.EligibleAfterNewAuthorization, result.RecoveryEligibility);
        Assert.Equal(["page-published", "authorized", "source", "extracted", "read-memberships", "replaced"], events);
        Assert.NotNull(repository.LastReplacement);
        Assert.Equal(4, repository.LastReplacement!.ExpectedPageRevision);
    }

    [Fact]
    public async Task AuthorizationIsRequiredAndRejectedBeforeSourceAccess()
    {
        var source = new FakeSource([], ResolvedPage());
        var extractor = new FakeExtractor([], CompletePlan());
        var repository = new FakeMembershipRepository([]);
        var service = new TwCategoryMembershipApplicationService(source, extractor, repository);

        var result = await service.IndexAfterCommittedMutationAsync(Request(), null);

        Assert.Equal(TwCategoryIndexingDisposition.UnauthorizedMutation, result.Disposition);
        Assert.Equal(0, source.Calls);
        Assert.Equal(0, repository.ReadCalls);
    }

    [Fact]
    public async Task HistoricalReadCannotIndexEvenWhenCallerClaimsAuthorization()
    {
        var source = new FakeSource([], ResolvedPage());
        var service = new TwCategoryMembershipApplicationService(source, new FakeExtractor([], CompletePlan()), new FakeMembershipRepository([]));
        var request = Request() with { MutationKind = TwCategoryIndexMutationKind.HistoricalRead };

        var result = await service.IndexAfterCommittedMutationAsync(request, (_, _) => Task.FromResult(true));

        Assert.Equal(TwCategoryIndexingDisposition.InvalidRequest, result.Disposition);
        Assert.Equal("HistoricalReadCannotIndex", result.DiagnosticCode);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task RevisionConflictStopsBeforeExtraction()
    {
        var source = new FakeSource([], new TwCategoryIndexPageSourceResult(
            TwCategoryIndexPageSourceDisposition.RevisionConflict,
            CurrentRevision: 5,
            DiagnosticCode: "CurrentRevisionChanged"));
        var extractor = new FakeExtractor([], CompletePlan());
        var service = new TwCategoryMembershipApplicationService(source, extractor, new FakeMembershipRepository([]));

        var result = await service.IndexAfterCommittedMutationAsync(Request(), (_, _) => Task.FromResult(true));

        Assert.Equal(TwCategoryIndexingDisposition.RevisionConflict, result.Disposition);
        Assert.Equal(5, result.PageRevision);
        Assert.Equal(0, extractor.Calls);
    }

    [Fact]
    public async Task IncompleteExtractionNeverReplacesMemberships()
    {
        var source = new FakeSource([], ResolvedPage());
        var extractor = new FakeExtractor([], new(
            MediaWikiCategoryIndexCompleteness.Incomplete,
            [],
            ["MissingTemplate"],
            PreserveExistingMembership: true,
            HasCompleteSourceProvenance: true));
        var repository = new FakeMembershipRepository([]);
        var service = new TwCategoryMembershipApplicationService(source, extractor, repository);

        var result = await service.IndexAfterCommittedMutationAsync(Request(), (_, _) => Task.FromResult(true));

        Assert.Equal(TwCategoryIndexingDisposition.IncompleteExtraction, result.Disposition);
        Assert.Equal("MissingTemplate", result.DiagnosticCode);
        Assert.Equal(0, repository.ReadCalls);
        Assert.Null(repository.LastReplacement);
    }

    [Fact]
    public async Task UnchangedMembershipSetReturnsNoChangeWithoutReplacement()
    {
        var source = new FakeSource([], ResolvedPage());
        var repository = new FakeMembershipRepository([], [ExistingMembership()]);
        var service = new TwCategoryMembershipApplicationService(source, new FakeExtractor([], CompletePlan()), repository);

        var result = await service.IndexAfterCommittedMutationAsync(Request(), (_, _) => Task.FromResult(true));

        Assert.Equal(TwCategoryIndexingDisposition.NoChange, result.Disposition);
        Assert.Equal(1, repository.ReadCalls);
        Assert.Null(repository.LastReplacement);
    }

    [Fact]
    public async Task DurableNonCurrentStatePreventsMembershipReadFromFalsifyingCurrent()
    {
        var repository = new FakeMembershipRepository(
            [],
            [ExistingMembership()],
            consistencyResult: new TwCategoryIndexConsistencyResult(
                TwCategoryIndexConsistencyState.Incomplete,
                17,
                CurrentPageRevision: 4,
                IndexedPageRevision: 4,
                DiagnosticCode: "StoredIndexIsIncomplete"));
        var service = new TwCategoryMembershipApplicationService(new FakeSource([], ResolvedPage()), new FakeExtractor([], CompletePlan()), repository);

        var result = await service.IndexAfterCommittedMutationAsync(Request(), (_, _) => Task.FromResult(true));

        Assert.Equal(TwCategoryIndexingDisposition.Applied, result.Disposition);
        Assert.NotNull(repository.LastReplacement);
    }

    [Fact]
    public async Task UnavailableRepositoryIsNotReportedAsSuccess()
    {
        var repository = new FakeMembershipRepository([], throwNotSupported: true);
        var service = new TwCategoryMembershipApplicationService(new FakeSource([], ResolvedPage()), new FakeExtractor([], CompletePlan()), repository);

        var result = await service.IndexAfterCommittedMutationAsync(Request(), (_, _) => Task.FromResult(true));

        Assert.Equal(TwCategoryIndexingDisposition.RepositoryUnavailable, result.Disposition);
        Assert.Equal("CategoryRepositoryUnavailable", result.DiagnosticCode);
    }

    [Fact]
    public async Task CancellationIsReturnedAsTypedOutcomeAndNotPersisted()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var repository = new FakeMembershipRepository([]);
        var service = new TwCategoryMembershipApplicationService(new FakeSource([], ResolvedPage()), new FakeExtractor([], CompletePlan()), repository);

        var result = await service.IndexAfterCommittedMutationAsync(Request(), (_, _) => Task.FromResult(true), cancellation.Token);

        Assert.Equal(TwCategoryIndexingDisposition.Cancelled, result.Disposition);
        Assert.Equal(TwCategoryIndexConsistencyState.Unknown, result.ConsistencyState);
        Assert.Equal(TwCategoryRecoveryEligibility.EligibleAfterNewAuthorization, result.RecoveryEligibility);
        Assert.Equal(0, repository.ReadCalls);
        Assert.Null(repository.LastReplacement);
    }

    [Fact]
    public async Task CancellationTokenReachesTheAuthoritativeSourceBoundary()
    {
        using var cancellation = new CancellationTokenSource();
        var service = new TwCategoryMembershipApplicationService(
            new FakeSource([], ResolvedPage()),
            new FakeExtractor([], CompletePlan()),
            new FakeMembershipRepository([]));

        var result = await service.IndexAfterCommittedMutationAsync(
            Request(),
            (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult(true);
            },
            cancellation.Token);

        Assert.Equal(TwCategoryIndexingDisposition.Cancelled, result.Disposition);
    }

    [Fact]
    public async Task AuthorizedImportUsesTheSameExplicitBoundary()
    {
        var source = new FakeSource([], ResolvedPage());
        var service = new TwCategoryMembershipApplicationService(source, new FakeExtractor([], CompletePlan()), new FakeMembershipRepository([]));
        var request = Request() with { MutationKind = TwCategoryIndexMutationKind.AuthorizedImport, ActorUserId = null };

        var result = await service.IndexAfterCommittedMutationAsync(
            request,
            (context, _) => Task.FromResult(context.MutationKind == TwCategoryIndexMutationKind.AuthorizedImport));

        Assert.Equal(TwCategoryIndexingDisposition.Applied, result.Disposition);
    }

    [Fact]
    public async Task RepositoryFailureIsTypedAndCannotBecomeSuccess()
    {
        var repository = new FakeMembershipRepository([], throwPersistenceFailure: true);
        var service = new TwCategoryMembershipApplicationService(new FakeSource([], ResolvedPage()), new FakeExtractor([], CompletePlan()), repository);

        var result = await service.IndexAfterCommittedMutationAsync(Request(), (_, _) => Task.FromResult(true));

        Assert.Equal(TwCategoryIndexingDisposition.PersistenceFailure, result.Disposition);
        Assert.Equal("CategoryIndexingFailed", result.DiagnosticCode);
    }

    [Fact]
    public async Task RepositoryCompareAndSwapConflictIsSurfaced()
    {
        var repository = new FakeMembershipRepository(
            [],
            replacementResult: new TwCategoryMembershipReplacementResult(
                TwCategoryMembershipReplacementDisposition.RevisionConflict,
                17,
                5,
                "ConcurrentRevisionChange"));
        var service = new TwCategoryMembershipApplicationService(new FakeSource([], ResolvedPage()), new FakeExtractor([], CompletePlan()), repository);

        var result = await service.IndexAfterCommittedMutationAsync(Request(), (_, _) => Task.FromResult(true));

        Assert.Equal(TwCategoryIndexingDisposition.RevisionConflict, result.Disposition);
        Assert.Equal("ConcurrentRevisionChange", result.DiagnosticCode);
    }

    [Fact]
    public async Task ReconciliationTargetsTheCurrentRevisionAndReportsNoChangeAsCurrent()
    {
        var source = new FakeSource([], ResolvedPage());
        var repository = new FakeMembershipRepository([], [ExistingMembership()]);
        var service = new TwCategoryMembershipApplicationService(source, new FakeExtractor([], CompletePlan()), repository);

        var result = await service.ReconcileAsync(
            new TwCategoryReconciliationRequest(17, "article::example", null, TwCategoryIndexMutationKind.AuthorizedMaintenance),
            (_, _) => Task.FromResult(true));

        Assert.Equal(TwCategoryReconciliationDisposition.NoChange, result.Disposition);
        Assert.Equal(4, result.ResolvedAuthoritativeRevision);
        Assert.Equal(TwCategoryIndexConsistencyState.Current, result.IndexingResult.ConsistencyState);
        Assert.Equal(TwCategoryRecoveryEligibility.NotEligible, result.RecoveryEligibility);
    }

    [Fact]
    public async Task ReconciliationSelectsNewerCurrentRevisionWhenNoExpectedRevisionIsSupplied()
    {
        var current = new TwCategoryIndexPageSourceResult(TwCategoryIndexPageSourceDisposition.Resolved, ResolvedPage(5));
        var repository = new FakeMembershipRepository([]);
        var service = new TwCategoryMembershipApplicationService(
            new FakeSource([], current, currentResult: current),
            new FakeExtractor([], CompletePlan()),
            repository);

        var result = await service.ReconcileAsync(
            new TwCategoryReconciliationRequest(17, "article::example", null, TwCategoryIndexMutationKind.AuthorizedMaintenance),
            (_, _) => Task.FromResult(true));

        Assert.Equal(TwCategoryReconciliationDisposition.Reconciled, result.Disposition);
        Assert.Equal(5, result.ResolvedAuthoritativeRevision);
        Assert.NotNull(repository.LastReplacement);
        Assert.Equal(5, repository.LastReplacement!.ExpectedPageRevision);
    }

    [Fact]
    public async Task RepeatedReconciliationRemainsNoChangeAtTheContractLevel()
    {
        var current = new TwCategoryIndexPageSourceResult(TwCategoryIndexPageSourceDisposition.Resolved, ResolvedPage(4));
        var repository = new FakeMembershipRepository([], [ExistingMembership()]);
        var service = new TwCategoryMembershipApplicationService(
            new FakeSource([], current, currentResult: current),
            new FakeExtractor([], CompletePlan()),
            repository);
        var request = new TwCategoryReconciliationRequest(17, "article::example", 4, TwCategoryIndexMutationKind.AuthorizedMaintenance);

        var first = await service.ReconcileAsync(request, (_, _) => Task.FromResult(true));
        var second = await service.ReconcileAsync(request, (_, _) => Task.FromResult(true));

        Assert.Equal(TwCategoryReconciliationDisposition.NoChange, first.Disposition);
        Assert.Equal(TwCategoryReconciliationDisposition.NoChange, second.Disposition);
        Assert.Null(repository.LastReplacement);
    }

    [Fact]
    public async Task UnauthorizedReconciliationDoesNotReadOrExtract()
    {
        var source = new FakeSource([], ResolvedPage());
        var extractor = new FakeExtractor([], CompletePlan());
        var service = new TwCategoryMembershipApplicationService(source, extractor, new FakeMembershipRepository([]));

        var result = await service.ReconcileAsync(
            new TwCategoryReconciliationRequest(17, "article::example", null, TwCategoryIndexMutationKind.AuthorizedMaintenance),
            (_, _) => Task.FromResult(false));

        Assert.Equal(TwCategoryReconciliationDisposition.Unauthorized, result.Disposition);
        Assert.Equal(0, source.Calls);
        Assert.Equal(0, extractor.Calls);
    }

    [Fact]
    public async Task HistoricalReconciliationIsRejected()
    {
        var source = new FakeSource([], ResolvedPage());
        var service = new TwCategoryMembershipApplicationService(source, new FakeExtractor([], CompletePlan()), new FakeMembershipRepository([]));

        var result = await service.ReconcileAsync(
            new TwCategoryReconciliationRequest(17, "article::example", 4, TwCategoryIndexMutationKind.HistoricalRead),
            (_, _) => Task.FromResult(true));

        Assert.Equal(TwCategoryReconciliationDisposition.Invalid, result.Disposition);
        Assert.Equal("HistoricalReadCannotReconcile", result.DiagnosticCode);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task SupersededRevisionIsRejectedBeforeExtraction()
    {
        var source = new FakeSource(
            [],
            new TwCategoryIndexPageSourceResult(TwCategoryIndexPageSourceDisposition.Resolved, ResolvedPage(5)),
            currentResult: new TwCategoryIndexPageSourceResult(TwCategoryIndexPageSourceDisposition.Resolved, ResolvedPage(5)));
        var extractor = new FakeExtractor([], CompletePlan());
        var service = new TwCategoryMembershipApplicationService(source, extractor, new FakeMembershipRepository([]));

        var result = await service.ReconcileAsync(
            new TwCategoryReconciliationRequest(17, "article::example", 4, TwCategoryIndexMutationKind.AuthorizedMaintenance),
            (_, _) => Task.FromResult(true));

        Assert.Equal(TwCategoryReconciliationDisposition.Superseded, result.Disposition);
        Assert.Equal(5, result.ResolvedAuthoritativeRevision);
        Assert.Equal(TwCategoryRecoveryEligibility.RequiresCurrentRevision, result.RecoveryEligibility);
        Assert.Equal(0, extractor.Calls);
    }

    [Fact]
    public async Task RevisionChangeBeforeReplacementCannotBeReportedAsReconciled()
    {
        var current = new TwCategoryIndexPageSourceResult(TwCategoryIndexPageSourceDisposition.Resolved, ResolvedPage(4));
        var source = new FakeSource(
            [],
            current,
            currentResult: current,
            resolveResults: [new TwCategoryIndexPageSourceResult(TwCategoryIndexPageSourceDisposition.RevisionConflict, CurrentRevision: 5, DiagnosticCode: "CurrentRevisionChanged")]);
        var repository = new FakeMembershipRepository([]);
        var service = new TwCategoryMembershipApplicationService(source, new FakeExtractor([], CompletePlan()), repository);

        var result = await service.ReconcileAsync(
            new TwCategoryReconciliationRequest(17, "article::example", null, TwCategoryIndexMutationKind.AuthorizedMaintenance),
            (_, _) => Task.FromResult(true));

        Assert.Equal(TwCategoryReconciliationDisposition.Superseded, result.Disposition);
        Assert.Equal(TwCategoryIndexConsistencyState.Stale, result.IndexingResult.ConsistencyState);
        Assert.Null(repository.LastReplacement);
    }

    private static TwCategoryIndexingRequest Request()
        => new(17, "article::example", 4, TwCategoryIndexMutationKind.PageMutation, Guid.NewGuid());

    private static TwCategoryIndexAuthoritativePage ResolvedPage()
        => ResolvedPage(4);

    private static TwCategoryIndexAuthoritativePage ResolvedPage(int revision)
        => new(
            17,
            "article::example",
            revision,
            revision,
            "[[Category:Divines]]",
            new(TwPageSourceFormat.ImportedMediaWiki, 9, "source-page", "source-revision", "Example", TargetPageId: 17, TargetRevision: revision));

    private static MediaWikiCategoryIndexPlan CompletePlan()
        => new(
            MediaWikiCategoryIndexCompleteness.Complete,
            [new("source-page", "source-revision", "Example", "Divines", "category::divines", null, null, "source-wiki", 0)],
            [],
            PreserveExistingMembership: false,
            HasCompleteSourceProvenance: true);

    private static TwCategoryMembershipRecord ExistingMembership()
        => new(17, TwCategoryIdentity.TryCreate("Divines", out var category) ? category : throw new InvalidOperationException(), null, 4, "source-page", "source-revision", "source-wiki", Guid.NewGuid(), DateTime.UtcNow);

    private sealed class FakeSource(
        List<string> events,
        TwCategoryIndexPageSourceResult result,
        TwCategoryIndexPageSourceResult? currentResult = null,
        IReadOnlyList<TwCategoryIndexPageSourceResult>? resolveResults = null) : ITwCategoryIndexPageSource
    {
        public FakeSource(List<string> events, TwCategoryIndexAuthoritativePage page)
            : this(events, new TwCategoryIndexPageSourceResult(TwCategoryIndexPageSourceDisposition.Resolved, page))
        {
        }

        private readonly Queue<TwCategoryIndexPageSourceResult> _resolveResults = new(resolveResults ?? []);

        public int Calls { get; private set; }

        public Task<TwCategoryIndexPageSourceResult> ResolveAsync(TwCategoryIndexingRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            events.Add("source");
            return Task.FromResult(_resolveResults.Count == 0 ? result : _resolveResults.Dequeue());
        }

        public Task<TwCategoryIndexPageSourceResult> ResolveCurrentAsync(TwCategoryReconciliationRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            events.Add("source-current");
            return Task.FromResult(currentResult ?? result);
        }
    }

    private sealed class FakeExtractor(List<string> events, MediaWikiCategoryIndexPlan plan) : ITwCategoryIndexExtractor
    {
        public int Calls { get; private set; }

        public Task<MediaWikiCategoryIndexPlan> ExtractAsync(TwCategoryIndexAuthoritativePage page, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            events.Add("extracted");
            return Task.FromResult(plan);
        }
    }

    private sealed class FakeMembershipRepository(
        List<string> events,
        IReadOnlyList<TwCategoryMembershipRecord>? existing = null,
        bool throwNotSupported = false,
        bool throwPersistenceFailure = false,
        TwCategoryMembershipReplacementResult? replacementResult = null,
        TwCategoryIndexConsistencyResult? consistencyResult = null) : ITwCategoryMembershipRepository
    {
        private readonly IReadOnlyList<TwCategoryMembershipRecord> _existing = existing ?? [];
        private readonly TwCategoryMembershipReplacementResult? _replacementResult = replacementResult;
        private readonly TwCategoryIndexConsistencyResult? _consistencyResult = consistencyResult;
        public int ReadCalls { get; private set; }
        public TwCategoryMembershipReplacementRequest? LastReplacement { get; private set; }

        public Task<IReadOnlyList<TwCategoryMembershipRecord>> GetMembershipsByPageIdAsync(int pageId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (throwNotSupported) throw new NotSupportedException();
            if (throwPersistenceFailure) throw new InvalidOperationException("synthetic repository failure");
            ReadCalls++;
            events.Add("read-memberships");
            return Task.FromResult(_existing);
        }

        public Task<TwCategoryIndexConsistencyResult> GetIndexConsistencyAsync(int pageId, CancellationToken cancellationToken = default)
            => _consistencyResult is null
                ? Task.FromException<TwCategoryIndexConsistencyResult>(new NotSupportedException())
                : Task.FromResult(_consistencyResult);

        public Task<TwCategoryMembershipReplacementResult> ReplaceMembershipsAsync(TwCategoryMembershipReplacementRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (throwNotSupported) throw new NotSupportedException();
            if (throwPersistenceFailure) throw new InvalidOperationException("synthetic repository failure");
            LastReplacement = request;
            events.Add("replaced");
            return Task.FromResult(_replacementResult ?? new TwCategoryMembershipReplacementResult(TwCategoryMembershipReplacementDisposition.Applied, request.PageId, request.ExpectedPageRevision));
        }

        public Task<TwCategoryMemberPage> GetMembersAsync(TwCategoryMemberQuery query, CancellationToken cancellationToken = default)
            => Task.FromException<TwCategoryMemberPage>(new NotSupportedException());

        public Task<TwCategoryMembershipReplacementResult> InvalidateMembershipsAsync(int pageId, int expectedPageRevision, CancellationToken cancellationToken = default)
            => Task.FromException<TwCategoryMembershipReplacementResult>(new NotSupportedException());
    }
}
