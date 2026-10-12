using TightWiki.Plugin.Interfaces.Persistence;
using TightWiki.Plugin.Models;
using TightWiki.Plugin;

namespace TightWiki.Engine.MediaWiki;

public enum TwCategoryIndexPageSourceDisposition
{
    Resolved,
    RevisionConflict,
    SourceUnavailable,
    InvalidRequest
}

public sealed record TwCategoryIndexAuthoritativePage(
    int PageId,
    string Navigation,
    int Revision,
    int MostCurrentRevision,
    string Body,
    TwMediaWikiRenderSource Provenance);

public sealed record TwCategoryIndexPageSourceResult(
    TwCategoryIndexPageSourceDisposition Disposition,
    TwCategoryIndexAuthoritativePage? Page = null,
    int? CurrentRevision = null,
    string? DiagnosticCode = null);

/// <summary>
/// Reads the committed current page revision for category indexing. This is a
/// source boundary, not a persistence boundary, and never writes membership.
/// </summary>
public interface ITwCategoryIndexPageSource
{
    Task<TwCategoryIndexPageSourceResult> ResolveAsync(
        TwCategoryIndexingRequest request,
        CancellationToken cancellationToken = default);

    Task<TwCategoryIndexPageSourceResult> ResolveCurrentAsync(
        TwCategoryReconciliationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Extracts a bounded category plan through the existing renderer.</summary>
public interface ITwCategoryIndexExtractor
{
    Task<MediaWikiCategoryIndexPlan> ExtractAsync(
        TwCategoryIndexAuthoritativePage page,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Application boundary for post-commit category indexing. The authorization
/// callback is mandatory at the call site so a read or direct indexing attempt
/// cannot acquire implicit mutation authority from dependency injection.
/// </summary>
public interface ITwCategoryMembershipApplicationService
{
    Task<TwCategoryIndexingResult> IndexAfterCommittedMutationAsync(
        TwCategoryIndexingRequest request,
        Func<TwCategoryIndexAuthorizationContext, CancellationToken, Task<bool>>? authorize,
        CancellationToken cancellationToken = default);

    Task<TwCategoryReconciliationResult> ReconcileAsync(
        TwCategoryReconciliationRequest request,
        Func<TwCategoryIndexAuthorizationContext, CancellationToken, Task<bool>>? authorize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidates a deleted page's category rows only after the page mutation
    /// has committed. The repository must validate the deleted state and exact
    /// authoritative revision inside its own transaction.
    /// </summary>
    Task<TwCategoryIndexingResult> InvalidateAfterCommittedDeletionAsync(
        TwCategoryIndexingRequest request,
        Func<TwCategoryIndexAuthorizationContext, CancellationToken, Task<bool>>? authorize,
        CancellationToken cancellationToken = default);
}

/// <summary>Read-only category-page orchestration over the typed repository.</summary>
public interface ITwCategoryBrowseService
{
    Task<TwCategoryBrowseResult> BrowseAsync(
        string? categoryTitle,
        Guid? viewerUserId,
        int pageNumber = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Implements category browse without treating repository failures or
/// unindexed state as an empty category.
/// </summary>
public sealed class TwCategoryBrowseService(ITwCategoryMembershipRepository membershipRepository) : ITwCategoryBrowseService
{
    public async Task<TwCategoryBrowseResult> BrowseAsync(
        string? categoryTitle,
        Guid? viewerUserId,
        int pageNumber = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        if (pageNumber <= 0)
            return new(TwCategoryBrowseDisposition.InvalidRequest, DiagnosticCode: "InvalidPageNumber");
        if (pageSize <= 0 || pageSize > TwCategoryMembershipLimits.MaxCategoryMemberPageSize)
            return new(TwCategoryBrowseDisposition.InvalidRequest, DiagnosticCode: "PageSizeLimitExceeded");

        if (!TwCategoryIdentity.TryCreateFromNavigation(categoryTitle, out var category))
            return new(TwCategoryBrowseDisposition.InvalidCategory, DiagnosticCode: "InvalidCategoryTitle");

        try
        {
            var consistency = await membershipRepository.GetCategoryIndexConsistencyAsync(category, cancellationToken);
            return await ReadMembersAsync(category, consistency, viewerUserId, pageNumber, pageSize, cancellationToken);
        }
        catch (NotSupportedException)
        {
            return new(TwCategoryBrowseDisposition.RepositoryUnavailable, category, DiagnosticCode: "CategoryRepositoryUnavailable");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(TwCategoryBrowseDisposition.RepositoryUnavailable, category, DiagnosticCode: "CategoryRepositoryUnavailable");
        }
    }

    private async Task<TwCategoryBrowseResult> ReadMembersAsync(
        TwCategoryIdentity category,
        TwCategoryIndexConsistencyResult consistency,
        Guid? viewerUserId,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken)
    {
        if (consistency.State == TwCategoryIndexConsistencyState.Unavailable)
            return new(TwCategoryBrowseDisposition.RepositoryUnavailable, category, Consistency: consistency, DiagnosticCode: consistency.DiagnosticCode ?? "CategoryRepositoryUnavailable");
        if (consistency.State == TwCategoryIndexConsistencyState.Unindexed)
            return new(TwCategoryBrowseDisposition.Unindexed, category, Consistency: consistency, DiagnosticCode: consistency.DiagnosticCode ?? "CategoryIndexUnindexed");
        if (consistency.State != TwCategoryIndexConsistencyState.Current)
            return new(TwCategoryBrowseDisposition.Inconsistent, category, Consistency: consistency, DiagnosticCode: consistency.DiagnosticCode ?? "CategoryIndexInconsistent");

        try
        {
            var members = await membershipRepository.GetMembersAsync(
                new TwCategoryMemberQuery(category, viewerUserId, pageNumber, pageSize),
                cancellationToken);
            return new(
                members.Members.Count == 0 ? TwCategoryBrowseDisposition.IndexedEmpty : TwCategoryBrowseDisposition.Populated,
                category,
                members.Members,
                members.HasMore,
                consistency,
                members.Members.Count == 0 ? "NoVisibleCategoryMembers" : "VisibleCategoryMembersReturned");
        }
        catch (NotSupportedException)
        {
            return new(TwCategoryBrowseDisposition.RepositoryUnavailable, category, Consistency: consistency, DiagnosticCode: "CategoryRepositoryUnavailable");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(TwCategoryBrowseDisposition.RepositoryUnavailable, category, Consistency: consistency, DiagnosticCode: "CategoryRepositoryUnavailable");
        }
    }

}

/// <summary>Adapts the existing page and MediaWiki import repositories.</summary>
public sealed class ApplicationTwCategoryIndexPageSource(
    ITwApplicationPageRepository pageRepository,
    ITwMediaWikiImportRepository importRepository) : ITwCategoryIndexPageSource
{
    public async Task<TwCategoryIndexPageSourceResult> ResolveAsync(
        TwCategoryIndexingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var currentRevision = await pageRepository.GetCurrentPageRevisionAsync(request.PageId, cancellationToken);
        if (currentRevision != request.ExpectedPageRevision)
            return new(TwCategoryIndexPageSourceDisposition.RevisionConflict, CurrentRevision: currentRevision, DiagnosticCode: "CurrentRevisionChanged");

        var page = await pageRepository.GetPageByIdAsync(request.PageId, request.ExpectedPageRevision, cancellationToken: cancellationToken);
        if (page is null)
            return new(TwCategoryIndexPageSourceDisposition.SourceUnavailable, CurrentRevision: currentRevision, DiagnosticCode: "SourcePageUnavailable");

        if (page.Revision != request.ExpectedPageRevision || page.MostCurrentRevision != request.ExpectedPageRevision)
            return new(TwCategoryIndexPageSourceDisposition.RevisionConflict, Page: null, CurrentRevision: page.MostCurrentRevision, DiagnosticCode: "AuthoritativeRevisionMismatch");

        var navigation = new TwNamespaceNavigation(page.Navigation).Canonical;
        if (!string.Equals(navigation, request.PageNavigation, StringComparison.OrdinalIgnoreCase))
            return new(TwCategoryIndexPageSourceDisposition.InvalidRequest, CurrentRevision: currentRevision, DiagnosticCode: "PageNavigationMismatch");

        var provenance = await importRepository.GetRenderSourceAsync(page.Id, page.Revision, cancellationToken);
        return new(
            TwCategoryIndexPageSourceDisposition.Resolved,
            new(page.Id, navigation, page.Revision, page.MostCurrentRevision, page.Body, provenance),
            currentRevision);
    }

    public async Task<TwCategoryIndexPageSourceResult> ResolveCurrentAsync(
        TwCategoryReconciliationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var currentRevision = await pageRepository.GetCurrentPageRevisionAsync(request.PageId, cancellationToken);
        if (currentRevision <= 0)
            return new(TwCategoryIndexPageSourceDisposition.SourceUnavailable, CurrentRevision: currentRevision, DiagnosticCode: "SourcePageUnavailable");

        return await ResolveAsync(
            new TwCategoryIndexingRequest(
                request.PageId,
                request.PageNavigation,
                currentRevision,
                request.MutationKind,
                request.ActorUserId),
            cancellationToken);
    }
}

/// <summary>Uses the bounded MediaWiki renderer and existing category planner.</summary>
public sealed class MediaWikiCategoryIndexExtractor(MediaWikiWikitextRenderer renderer) : ITwCategoryIndexExtractor
{
    public async Task<MediaWikiCategoryIndexPlan> ExtractAsync(
        TwCategoryIndexAuthoritativePage page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (page.Provenance.Format != TwPageSourceFormat.ImportedMediaWiki)
        {
            return new(
                MediaWikiCategoryIndexCompleteness.Incomplete,
                [],
                ["UnsupportedSourceFormat"],
                PreserveExistingMembership: true,
                HasCompleteSourceProvenance: false);
        }

        var renderResult = await renderer.RenderAsync(page.Body, page.Provenance, cancellationToken);
        return MediaWikiCategoryIndexPlanner.Build(page.Provenance, renderResult);
    }
}

/// <summary>
/// Coordinates authorization, authoritative revision validation, bounded
/// extraction, idempotency, and the future atomic repository replacement.
/// </summary>
public sealed class TwCategoryMembershipApplicationService(
    ITwCategoryIndexPageSource pageSource,
    ITwCategoryIndexExtractor extractor,
    ITwCategoryMembershipRepository membershipRepository) : ITwCategoryMembershipApplicationService
{
    public async Task<TwCategoryIndexingResult> IndexAfterCommittedMutationAsync(
        TwCategoryIndexingRequest request,
        Func<TwCategoryIndexAuthorizationContext, CancellationToken, Task<bool>>? authorize,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidate(request, out var validationCode))
            return Invalid(request?.PageId ?? 0, validationCode);

        if (authorize is null || request.MutationKind == TwCategoryIndexMutationKind.HistoricalRead)
            return Unauthorized(request.PageId, request.ExpectedPageRevision, "MutationAuthorizationRequired");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var authorization = new TwCategoryIndexAuthorizationContext(
                request.PageId,
                request.PageNavigation,
                request.ExpectedPageRevision,
                request.MutationKind,
                request.ActorUserId);
            if (!await authorize(authorization, cancellationToken))
                return Unauthorized(request.PageId, request.ExpectedPageRevision, "MutationAuthorizationRejected");

            return await IndexCoreAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(TwCategoryIndexingDisposition.Cancelled, request.PageId, request.ExpectedPageRevision, "CancellationRequested", TwCategoryIndexConsistencyState.Unknown, TwCategoryRecoveryEligibility.EligibleAfterNewAuthorization);
        }
        catch (NotSupportedException)
        {
            return new(TwCategoryIndexingDisposition.RepositoryUnavailable, request.PageId, request.ExpectedPageRevision, "CategoryRepositoryUnavailable", TwCategoryIndexConsistencyState.Unavailable, TwCategoryRecoveryEligibility.EligibleAfterNewAuthorization);
        }
        catch
        {
            return new(TwCategoryIndexingDisposition.PersistenceFailure, request.PageId, request.ExpectedPageRevision, "CategoryIndexingFailed", TwCategoryIndexConsistencyState.Failed, TwCategoryRecoveryEligibility.EligibleAfterNewAuthorization);
        }
    }

    public async Task<TwCategoryReconciliationResult> ReconcileAsync(
        TwCategoryReconciliationRequest request,
        Func<TwCategoryIndexAuthorizationContext, CancellationToken, Task<bool>>? authorize,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidate(request, out var validationCode))
            return ReconciliationInvalid(request?.PageId ?? 0, request?.ExpectedPageRevision, validationCode);

        if (authorize is null || request.MutationKind == TwCategoryIndexMutationKind.HistoricalRead)
            return ReconciliationUnauthorized(request, "MutationAuthorizationRequired");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var authorization = new TwCategoryIndexAuthorizationContext(
                request.PageId,
                request.PageNavigation,
                request.ExpectedPageRevision,
                request.MutationKind,
                request.ActorUserId);
            if (!await authorize(authorization, cancellationToken))
                return ReconciliationUnauthorized(request, "MutationAuthorizationRejected");

            var source = await pageSource.ResolveCurrentAsync(request, cancellationToken);
            if (source.Disposition == TwCategoryIndexPageSourceDisposition.InvalidRequest)
                return ReconciliationInvalid(request.PageId, request.ExpectedPageRevision, source.DiagnosticCode ?? "InvalidSourceRequest");
            if (source.Page is null)
            {
                var unavailable = new TwCategoryIndexingResult(
                    TwCategoryIndexingDisposition.IncompleteExtraction,
                    request.PageId,
                    source.CurrentRevision,
                    source.DiagnosticCode ?? "SourceUnavailable",
                    TwCategoryIndexConsistencyState.Unknown,
                    TwCategoryRecoveryEligibility.EligibleAfterDependencyRecovery);
                return new(TwCategoryReconciliationDisposition.Incomplete, request.PageId, request.ExpectedPageRevision, source.CurrentRevision, unavailable, unavailable.RecoveryEligibility, unavailable.DiagnosticCode);
            }

            if (request.ExpectedPageRevision is int expected && expected != source.Page.Revision)
            {
                var superseded = new TwCategoryIndexingResult(
                    TwCategoryIndexingDisposition.RevisionConflict,
                    request.PageId,
                    source.Page.Revision,
                    "RequestedRevisionSuperseded",
                    TwCategoryIndexConsistencyState.Stale,
                    TwCategoryRecoveryEligibility.RequiresCurrentRevision,
                    "Current authoritative revision is newer than the requested reconciliation revision.");
                return new(TwCategoryReconciliationDisposition.Superseded, request.PageId, expected, source.Page.Revision, superseded, superseded.RecoveryEligibility, superseded.DiagnosticCode);
            }

            var currentRequest = new TwCategoryIndexingRequest(
                source.Page.PageId,
                source.Page.Navigation,
                source.Page.Revision,
                request.MutationKind,
                request.ActorUserId);
            var result = await IndexCoreAsync(currentRequest, cancellationToken);
            return ToReconciliationResult(request, source.Page.Revision, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cancelled = new TwCategoryIndexingResult(
                TwCategoryIndexingDisposition.Cancelled,
                request.PageId,
                request.ExpectedPageRevision,
                "CancellationRequested",
                TwCategoryIndexConsistencyState.Unknown,
                TwCategoryRecoveryEligibility.EligibleAfterNewAuthorization);
            return new(TwCategoryReconciliationDisposition.Cancelled, request.PageId, request.ExpectedPageRevision, null, cancelled, cancelled.RecoveryEligibility, cancelled.DiagnosticCode);
        }
        catch (NotSupportedException)
        {
            var unavailable = new TwCategoryIndexingResult(
                TwCategoryIndexingDisposition.RepositoryUnavailable,
                request.PageId,
                request.ExpectedPageRevision,
                "CategoryRepositoryUnavailable",
                TwCategoryIndexConsistencyState.Unavailable,
                TwCategoryRecoveryEligibility.EligibleAfterNewAuthorization);
            return new(TwCategoryReconciliationDisposition.Unavailable, request.PageId, request.ExpectedPageRevision, null, unavailable, unavailable.RecoveryEligibility, unavailable.DiagnosticCode);
        }
        catch
        {
            var failed = new TwCategoryIndexingResult(
                TwCategoryIndexingDisposition.PersistenceFailure,
                request.PageId,
                request.ExpectedPageRevision,
                "CategoryIndexingFailed",
                TwCategoryIndexConsistencyState.Failed,
                TwCategoryRecoveryEligibility.EligibleAfterNewAuthorization);
            return new(TwCategoryReconciliationDisposition.Failed, request.PageId, request.ExpectedPageRevision, null, failed, failed.RecoveryEligibility, failed.DiagnosticCode);
        }
    }

    public async Task<TwCategoryIndexingResult> InvalidateAfterCommittedDeletionAsync(
        TwCategoryIndexingRequest request,
        Func<TwCategoryIndexAuthorizationContext, CancellationToken, Task<bool>>? authorize,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidate(request, out var validationCode))
            return Invalid(request?.PageId ?? 0, validationCode);

        if (authorize is null || request.MutationKind == TwCategoryIndexMutationKind.HistoricalRead)
            return Unauthorized(request.PageId, request.ExpectedPageRevision, "MutationAuthorizationRequired");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var authorization = new TwCategoryIndexAuthorizationContext(
                request.PageId,
                request.PageNavigation,
                request.ExpectedPageRevision,
                request.MutationKind,
                request.ActorUserId);
            if (!await authorize(authorization, cancellationToken))
                return Unauthorized(request.PageId, request.ExpectedPageRevision, "MutationAuthorizationRejected");

            var result = await membershipRepository.InvalidateMembershipsAsync(
                request.PageId,
                request.ExpectedPageRevision,
                cancellationToken);
            return result.Disposition switch
            {
                TwCategoryMembershipReplacementDisposition.Applied => new(
                    TwCategoryIndexingDisposition.Invalidated,
                    request.PageId,
                    request.ExpectedPageRevision,
                    result.DiagnosticCode,
                    TwCategoryIndexConsistencyState.Unknown,
                    TwCategoryRecoveryEligibility.EligibleAfterNewAuthorization,
                    "Deleted-page membership invalidation committed."),
                TwCategoryMembershipReplacementDisposition.NoChange => new(
                    TwCategoryIndexingDisposition.NoChange,
                    request.PageId,
                    request.ExpectedPageRevision,
                    result.DiagnosticCode,
                    TwCategoryIndexConsistencyState.Unknown,
                    TwCategoryRecoveryEligibility.EligibleAfterNewAuthorization),
                TwCategoryMembershipReplacementDisposition.RevisionConflict => new(
                    TwCategoryIndexingDisposition.RevisionConflict,
                    request.PageId,
                    result.AppliedPageRevision ?? request.ExpectedPageRevision,
                    result.DiagnosticCode,
                    TwCategoryIndexConsistencyState.Stale,
                    TwCategoryRecoveryEligibility.RequiresCurrentRevision),
                TwCategoryMembershipReplacementDisposition.PersistenceFailure => new(
                    TwCategoryIndexingDisposition.PersistenceFailure,
                    request.PageId,
                    request.ExpectedPageRevision,
                    result.DiagnosticCode ?? "CategoryIndexInvalidationFailed",
                    TwCategoryIndexConsistencyState.Failed,
                    TwCategoryRecoveryEligibility.EligibleAfterNewAuthorization),
                _ => Invalid(request.PageId, result.DiagnosticCode ?? "InvalidInvalidationResult")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(TwCategoryIndexingDisposition.Cancelled, request.PageId, request.ExpectedPageRevision, "CancellationRequested", TwCategoryIndexConsistencyState.Unknown, TwCategoryRecoveryEligibility.EligibleAfterNewAuthorization);
        }
        catch (NotSupportedException)
        {
            return new(TwCategoryIndexingDisposition.RepositoryUnavailable, request.PageId, request.ExpectedPageRevision, "CategoryRepositoryUnavailable", TwCategoryIndexConsistencyState.Unavailable, TwCategoryRecoveryEligibility.EligibleAfterNewAuthorization);
        }
        catch
        {
            return new(TwCategoryIndexingDisposition.PersistenceFailure, request.PageId, request.ExpectedPageRevision, "CategoryIndexInvalidationFailed", TwCategoryIndexConsistencyState.Failed, TwCategoryRecoveryEligibility.EligibleAfterNewAuthorization);
        }
    }

    private async Task<TwCategoryIndexingResult> IndexCoreAsync(
        TwCategoryIndexingRequest request,
        CancellationToken cancellationToken)
    {
        var source = await pageSource.ResolveAsync(request, cancellationToken);
        if (source.Disposition == TwCategoryIndexPageSourceDisposition.RevisionConflict)
        {
            var state = source.CurrentRevision is > 0 and var current && current > request.ExpectedPageRevision
                ? TwCategoryIndexConsistencyState.Stale
                : TwCategoryIndexConsistencyState.Unknown;
            var eligibility = state == TwCategoryIndexConsistencyState.Stale
                ? TwCategoryRecoveryEligibility.RequiresCurrentRevision
                : TwCategoryRecoveryEligibility.EligibleAfterNewAuthorization;
            return new(TwCategoryIndexingDisposition.RevisionConflict, request.PageId, source.CurrentRevision, source.DiagnosticCode, state, eligibility, "Authoritative revision changed before extraction.");
        }
        if (source.Disposition == TwCategoryIndexPageSourceDisposition.InvalidRequest)
            return Invalid(request.PageId, source.DiagnosticCode ?? "InvalidSourceRequest");
        if (source.Page is null)
            return new(TwCategoryIndexingDisposition.IncompleteExtraction, request.PageId, source.CurrentRevision, source.DiagnosticCode ?? "SourceUnavailable", TwCategoryIndexConsistencyState.Unknown, TwCategoryRecoveryEligibility.EligibleAfterDependencyRecovery);

        var plan = await extractor.ExtractAsync(source.Page, cancellationToken);
        if (!plan.CanReplaceExistingMembership)
            return new(TwCategoryIndexingDisposition.IncompleteExtraction, request.PageId, source.Page.Revision, plan.BlockingDiagnostics.FirstOrDefault() ?? "IncompleteExtraction", TwCategoryIndexConsistencyState.Incomplete, TwCategoryRecoveryEligibility.EligibleAfterDependencyRecovery);

        var indexedAtUtc = DateTime.UtcNow;
        var indexGeneration = Guid.NewGuid();
        if (!MediaWikiCategoryIndexPlanner.TryCreateReplacementRequest(
                source.Page.Provenance,
                plan,
                indexGeneration,
                indexedAtUtc,
                out var replacement))
            return Invalid(request.PageId, "ReplacementRequestInvalid");

        TwCategoryIndexConsistencyResult? durableConsistency = null;
        try
        {
            durableConsistency = await membershipRepository.GetIndexConsistencyAsync(request.PageId, cancellationToken);
        }
        catch (NotSupportedException)
        {
            // Preserve the source-only contract for the rejecting adapter and
            // synthetic repositories. A durable provider must implement the
            // read-back boundary before it can report index currency.
        }

        if (durableConsistency is null || durableConsistency.State == TwCategoryIndexConsistencyState.Current)
        {
            var existing = await membershipRepository.GetMembershipsByPageIdAsync(request.PageId, cancellationToken);
            var disposition = TwCategoryMembershipReplacementPolicy.Classify(replacement, source.Page.Revision, existing);
            if (disposition == TwCategoryMembershipReplacementDisposition.NoChange)
                return new(TwCategoryIndexingDisposition.NoChange, request.PageId, source.Page.Revision, null, TwCategoryIndexConsistencyState.Current, TwCategoryRecoveryEligibility.NotEligible, "Repository membership read matched the authoritative revision and desired set.");
            if (disposition == TwCategoryMembershipReplacementDisposition.RevisionConflict)
                return new(TwCategoryIndexingDisposition.RevisionConflict, request.PageId, source.Page.Revision, "ReplacementRevisionConflict", TwCategoryIndexConsistencyState.Stale, TwCategoryRecoveryEligibility.RequiresCurrentRevision, "Replacement precondition rejected the source revision.");
            if (disposition != TwCategoryMembershipReplacementDisposition.Applied)
                return Invalid(request.PageId, disposition.ToString());
        }

        var replacementResult = await membershipRepository.ReplaceMembershipsAsync(replacement, cancellationToken);
        var mapped = MapRepositoryResult(replacementResult, request.PageId, source.Page.Revision);
        if (mapped.Disposition != TwCategoryIndexingDisposition.Applied)
            return mapped;

        try
        {
            var readBack = await membershipRepository.GetIndexConsistencyAsync(request.PageId, cancellationToken);
            return mapped with
            {
                ConsistencyState = readBack.State,
                RecoveryEligibility = RecoveryFor(readBack.State),
                ConsistencyEvidence = readBack.DiagnosticCode ?? "DurableIndexReadBackCompleted"
            };
        }
        catch (NotSupportedException)
        {
            return mapped;
        }
    }

    private static bool TryValidate(TwCategoryIndexingRequest? request, out string code)
    {
        code = string.Empty;
        if (request is null) { code = "MissingRequest"; return false; }
        if (request.PageId <= 0) { code = "InvalidPageId"; return false; }
        if (request.ExpectedPageRevision <= 0) { code = "InvalidExpectedPageRevision"; return false; }
        if (string.IsNullOrWhiteSpace(request.PageNavigation)) { code = "MissingPageNavigation"; return false; }
        if (request.MutationKind == TwCategoryIndexMutationKind.HistoricalRead) { code = "HistoricalReadCannotIndex"; return false; }
        try
        {
            if (!string.Equals(new TwNamespaceNavigation(request.PageNavigation).Canonical, request.PageNavigation, StringComparison.OrdinalIgnoreCase))
            {
                code = "PageNavigationNotCanonical";
                return false;
            }
        }
        catch
        {
            code = "InvalidPageNavigation";
            return false;
        }
        return true;
    }

    private static TwCategoryIndexingResult Invalid(int pageId, string? code)
        => new(TwCategoryIndexingDisposition.InvalidRequest, pageId, DiagnosticCode: code, RecoveryEligibility: TwCategoryRecoveryEligibility.NotEligible);

    private static TwCategoryIndexingResult Unauthorized(int pageId, int revision, string code)
        => new(TwCategoryIndexingDisposition.UnauthorizedMutation, pageId, revision, code, TwCategoryIndexConsistencyState.Unknown, TwCategoryRecoveryEligibility.RequiresAuthorization);

    private static TwCategoryIndexingResult MapRepositoryResult(
        TwCategoryMembershipReplacementResult result,
        int pageId,
        int revision)
        => result.Disposition switch
        {
            TwCategoryMembershipReplacementDisposition.Applied => new(TwCategoryIndexingDisposition.Applied, pageId, revision, result.DiagnosticCode, TwCategoryIndexConsistencyState.Unknown, TwCategoryRecoveryEligibility.EligibleAfterNewAuthorization, "Repository reported replacement; durable index currency was not independently read back."),
            TwCategoryMembershipReplacementDisposition.NoChange => new(TwCategoryIndexingDisposition.NoChange, pageId, revision, result.DiagnosticCode, TwCategoryIndexConsistencyState.Current, TwCategoryRecoveryEligibility.NotEligible, "Repository reported no change after matching membership read."),
            TwCategoryMembershipReplacementDisposition.IncompleteExtraction => new(TwCategoryIndexingDisposition.IncompleteExtraction, pageId, revision, result.DiagnosticCode, TwCategoryIndexConsistencyState.Incomplete, TwCategoryRecoveryEligibility.EligibleAfterDependencyRecovery),
            TwCategoryMembershipReplacementDisposition.RevisionConflict => new(TwCategoryIndexingDisposition.RevisionConflict, pageId, revision, result.DiagnosticCode, TwCategoryIndexConsistencyState.Stale, TwCategoryRecoveryEligibility.RequiresCurrentRevision),
            TwCategoryMembershipReplacementDisposition.AuthorizationRejected => new(TwCategoryIndexingDisposition.UnauthorizedMutation, pageId, revision, result.DiagnosticCode, TwCategoryIndexConsistencyState.Unknown, TwCategoryRecoveryEligibility.RequiresAuthorization),
            TwCategoryMembershipReplacementDisposition.PersistenceFailure => new(TwCategoryIndexingDisposition.PersistenceFailure, pageId, revision, result.DiagnosticCode ?? "CategoryPersistenceFailure", TwCategoryIndexConsistencyState.Failed, TwCategoryRecoveryEligibility.EligibleAfterNewAuthorization),
            _ => new(TwCategoryIndexingDisposition.InvalidRequest, pageId, revision, result.DiagnosticCode ?? "InvalidReplacementResult")
        };

    private static TwCategoryRecoveryEligibility RecoveryFor(TwCategoryIndexConsistencyState state)
        => state switch
        {
            TwCategoryIndexConsistencyState.Current => TwCategoryRecoveryEligibility.NotEligible,
            TwCategoryIndexConsistencyState.Stale => TwCategoryRecoveryEligibility.RequiresCurrentRevision,
            TwCategoryIndexConsistencyState.Incomplete => TwCategoryRecoveryEligibility.EligibleAfterDependencyRecovery,
            _ => TwCategoryRecoveryEligibility.EligibleAfterNewAuthorization
        };

    private static TwCategoryReconciliationResult ToReconciliationResult(
        TwCategoryReconciliationRequest request,
        int resolvedRevision,
        TwCategoryIndexingResult result)
    {
        var disposition = result.Disposition switch
        {
            TwCategoryIndexingDisposition.Applied => TwCategoryReconciliationDisposition.Reconciled,
            TwCategoryIndexingDisposition.NoChange => TwCategoryReconciliationDisposition.NoChange,
            TwCategoryIndexingDisposition.Invalidated => TwCategoryReconciliationDisposition.Reconciled,
            TwCategoryIndexingDisposition.IncompleteExtraction => TwCategoryReconciliationDisposition.Incomplete,
            TwCategoryIndexingDisposition.RevisionConflict => TwCategoryReconciliationDisposition.Superseded,
            TwCategoryIndexingDisposition.UnauthorizedMutation => TwCategoryReconciliationDisposition.Unauthorized,
            TwCategoryIndexingDisposition.Cancelled => TwCategoryReconciliationDisposition.Cancelled,
            TwCategoryIndexingDisposition.RepositoryUnavailable => TwCategoryReconciliationDisposition.Unavailable,
            TwCategoryIndexingDisposition.PersistenceFailure => TwCategoryReconciliationDisposition.Failed,
            _ => TwCategoryReconciliationDisposition.Invalid
        };
        return new(disposition, request.PageId, request.ExpectedPageRevision, resolvedRevision, result, result.RecoveryEligibility, result.DiagnosticCode);
    }

    private static TwCategoryReconciliationResult ReconciliationInvalid(int pageId, int? requestedRevision, string? code)
    {
        var result = new TwCategoryIndexingResult(TwCategoryIndexingDisposition.InvalidRequest, pageId, requestedRevision, code);
        return new(TwCategoryReconciliationDisposition.Invalid, pageId, requestedRevision, null, result, result.RecoveryEligibility, code);
    }

    private static TwCategoryReconciliationResult ReconciliationUnauthorized(TwCategoryReconciliationRequest request, string code)
    {
        var result = new TwCategoryIndexingResult(TwCategoryIndexingDisposition.UnauthorizedMutation, request.PageId, request.ExpectedPageRevision, code, TwCategoryIndexConsistencyState.Unknown, TwCategoryRecoveryEligibility.RequiresAuthorization);
        return new(TwCategoryReconciliationDisposition.Unauthorized, request.PageId, request.ExpectedPageRevision, null, result, result.RecoveryEligibility, code);
    }

    private static bool TryValidate(TwCategoryReconciliationRequest? request, out string code)
    {
        code = string.Empty;
        if (request is null) { code = "MissingRequest"; return false; }
        if (request.PageId <= 0) { code = "InvalidPageId"; return false; }
        if (string.IsNullOrWhiteSpace(request.PageNavigation)) { code = "MissingPageNavigation"; return false; }
        if (request.ExpectedPageRevision is <= 0) { code = "InvalidExpectedPageRevision"; return false; }
        if (request.MutationKind == TwCategoryIndexMutationKind.HistoricalRead) { code = "HistoricalReadCannotReconcile"; return false; }
        try
        {
            if (!string.Equals(new TwNamespaceNavigation(request.PageNavigation).Canonical, request.PageNavigation, StringComparison.OrdinalIgnoreCase))
            {
                code = "PageNavigationNotCanonical";
                return false;
            }
        }
        catch
        {
            code = "InvalidPageNavigation";
            return false;
        }
        return true;
    }
}

/// <summary>
/// Explicit fail-closed provider used until an authorized category repository
/// is implemented and registered. It never reports a successful write.
/// </summary>
public sealed class RejectingTwCategoryMembershipRepository : ITwCategoryMembershipRepository
{
    private static NotSupportedException Unavailable()
        => new("Category membership persistence is not enabled.");

    public Task<TwCategoryIndexConsistencyResult> GetIndexConsistencyAsync(int pageId, CancellationToken cancellationToken = default)
        => Task.FromException<TwCategoryIndexConsistencyResult>(Unavailable());

    public Task<TwCategoryIndexConsistencyResult> GetCategoryIndexConsistencyAsync(TwCategoryIdentity category, CancellationToken cancellationToken = default)
        => Task.FromException<TwCategoryIndexConsistencyResult>(Unavailable());

    public Task<IReadOnlyList<TwCategoryMembershipRecord>> GetMembershipsByPageIdAsync(int pageId, CancellationToken cancellationToken = default)
        => Task.FromException<IReadOnlyList<TwCategoryMembershipRecord>>(Unavailable());

    public Task<TwCategoryMembershipReplacementResult> ReplaceMembershipsAsync(TwCategoryMembershipReplacementRequest request, CancellationToken cancellationToken = default)
        => Task.FromException<TwCategoryMembershipReplacementResult>(Unavailable());

    public Task<TwCategoryMemberPage> GetMembersAsync(TwCategoryMemberQuery query, CancellationToken cancellationToken = default)
        => Task.FromException<TwCategoryMemberPage>(Unavailable());

    public Task<TwCategoryMembershipReplacementResult> InvalidateMembershipsAsync(int pageId, int expectedPageRevision, CancellationToken cancellationToken = default)
        => Task.FromException<TwCategoryMembershipReplacementResult>(Unavailable());
}
