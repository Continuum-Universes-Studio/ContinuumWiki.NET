namespace TightWiki.Plugin.Models;

#pragma warning disable CS1591

public static class TwCategoryMembershipLimits
{
    public const int MaxCategoryNavigationCharacters = 128;
    public const int MaxSortKeyCharacters = 512;
    public const int MaxCategoryMemberPageSize = 100;
}

/// <summary>Canonical Category namespace identity shared by persistence contracts.</summary>
public sealed record TwCategoryIdentity(string CanonicalNavigation, string NormalizedTitle)
{
    public const string NamespaceName = "Category";

    public static bool TryCreate(string? sourceTitle, out TwCategoryIdentity identity)
    {
        identity = null!;
        if (string.IsNullOrWhiteSpace(sourceTitle)) return false;

        TwNamespaceNavigation navigation;
        try
        {
            navigation = new TwNamespaceNavigation($"{NamespaceName}::{sourceTitle.Trim()}");
        }
        catch
        {
            return false;
        }

        if (!string.Equals(navigation.Namespace, NamespaceName, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(navigation.Page)
            || navigation.Canonical.Length > TwCategoryMembershipLimits.MaxCategoryNavigationCharacters)
            return false;

        identity = new(navigation.Canonical, navigation.Page);
        return true;
    }

    /// <summary>
    /// Parses either a category title or an existing Category namespace
    /// navigation using the same TwNamespaceNavigation rules.
    /// </summary>
    public static bool TryCreateFromNavigation(string? source, out TwCategoryIdentity identity)
    {
        identity = null!;
        if (string.IsNullOrWhiteSpace(source)) return false;

        var value = source.Trim();
        try
        {
            if (value.Contains("::", StringComparison.Ordinal))
            {
                var navigation = new TwNamespaceNavigation(value);
                if (!string.Equals(navigation.Namespace, NamespaceName, StringComparison.OrdinalIgnoreCase))
                    return false;
                value = navigation.Page;
            }
        }
        catch
        {
            return false;
        }

        return TryCreate(value, out identity);
    }
}

public enum TwCategoryMembershipIndexState
{
    Verified,
    Incomplete,
    Unknown
}

/// <summary>State persisted independently from category membership rows.</summary>
public enum TwCategoryDurableIndexState
{
    Verified,
    Incomplete,
    Failed,
    Unknown
}

/// <summary>One durable membership candidate for a specific page revision.</summary>
public sealed record TwCategoryMembershipRecord(
    int PageId,
    TwCategoryIdentity Category,
    string? SortKey,
    int SourcePageRevision,
    string? SourcePageId,
    string? SourceRevisionId,
    string? SourceWikiIdentity,
    Guid IndexGeneration,
    DateTime IndexedAtUtc,
    TwCategoryMembershipIndexState IndexState = TwCategoryMembershipIndexState.Verified);

/// <summary>Authorized mutation input for an atomic membership replacement.</summary>
public sealed record TwCategoryMembershipReplacementRequest(
    int PageId,
    int ExpectedPageRevision,
    IReadOnlyList<TwCategoryMembershipRecord> DesiredMemberships,
    bool ExtractionComplete,
    Guid IndexGeneration,
    DateTime IndexedAtUtc,
    string? SourcePageId = null,
    string? SourceRevisionId = null,
    string? SourceWikiIdentity = null);

public enum TwCategoryMembershipReplacementDisposition
{
    Applied,
    NoChange,
    IncompleteExtraction,
    RevisionConflict,
    AuthorizationRejected,
    PersistenceFailure,
    InvalidRequest
}

public sealed record TwCategoryMembershipReplacementResult(
    TwCategoryMembershipReplacementDisposition Disposition,
    int PageId,
    int? AppliedPageRevision = null,
    string? DiagnosticCode = null);

/// <summary>Visibility-scoped, bounded category member query.</summary>
public sealed record TwCategoryMemberQuery(
    TwCategoryIdentity Category,
    Guid? ViewerUserId,
    int PageNumber = 1,
    int PageSize = 50);

/// <summary>Only page data already authorized for the requesting viewer.</summary>
public sealed record TwCategoryMember(
    int PageId,
    string PageNavigation,
    string PageName,
    string Description,
    string? SortKey);

public sealed record TwCategoryMemberPage(
    IReadOnlyList<TwCategoryMember> Members,
    bool HasMore);

/// <summary>Read-only category-page states exposed by the browse service.</summary>
public enum TwCategoryBrowseDisposition
{
    InvalidCategory,
    InvalidRequest,
    RepositoryUnavailable,
    Unindexed,
    IndexedEmpty,
    Populated,
    Inconsistent
}

/// <summary>
/// Visibility-filtered category browse output. The result deliberately has no
/// total count: the repository applies authorization before pagination, and a
/// count could otherwise disclose hidden members.
/// </summary>
public sealed record TwCategoryBrowseResult(
    TwCategoryBrowseDisposition Disposition,
    TwCategoryIdentity? Category = null,
    IReadOnlyList<TwCategoryMember>? Members = null,
    bool HasMore = false,
    TwCategoryIndexConsistencyResult? Consistency = null,
    string? DiagnosticCode = null)
{
    public IReadOnlyList<TwCategoryMember> VisibleMembers => Members ?? [];
}

public enum TwCategoryIndexMutationKind
{
    PageMutation,
    AuthorizedImport,
    AuthorizedMaintenance,
    HistoricalRead
}

public sealed record TwCategoryIndexingRequest(
    int PageId,
    string PageNavigation,
    int ExpectedPageRevision,
    TwCategoryIndexMutationKind MutationKind,
    Guid? ActorUserId = null);

public sealed record TwCategoryIndexAuthorizationContext(
    int PageId,
    string PageNavigation,
    int? ExpectedPageRevision,
    TwCategoryIndexMutationKind MutationKind,
    Guid? ActorUserId);

public sealed record TwCategoryReconciliationRequest(
    int PageId,
    string PageNavigation,
    int? ExpectedPageRevision,
    TwCategoryIndexMutationKind MutationKind,
    Guid? ActorUserId = null);

public enum TwCategoryIndexConsistencyState
{
    Current,
    Pending,
    Stale,
    Unindexed,
    Incomplete,
    Failed,
    Unavailable,
    Unknown
}

/// <summary>Authoritative provenance stored with the durable index state.</summary>
public sealed record TwCategoryIndexProvenance(
    string? SourcePageId,
    string? SourceRevisionId,
    string? SourceWikiIdentity,
    Guid? IndexGeneration,
    DateTime? IndexedAtUtc);

/// <summary>Read-back of durable category index state against the current page.</summary>
public sealed record TwCategoryIndexConsistencyResult(
    TwCategoryIndexConsistencyState State,
    int PageId,
    int? CurrentPageRevision = null,
    int? IndexedPageRevision = null,
    TwCategoryIndexProvenance? Provenance = null,
    string? DiagnosticCode = null);

public enum TwCategoryRecoveryEligibility
{
    NotEligible,
    EligibleAfterNewAuthorization,
    EligibleAfterDependencyRecovery,
    RequiresCurrentRevision,
    RequiresAuthorization
}

public enum TwCategoryIndexingDisposition
{
    Applied,
    NoChange,
    Invalidated,
    IncompleteExtraction,
    RevisionConflict,
    UnauthorizedMutation,
    InvalidRequest,
    Cancelled,
    RepositoryUnavailable,
    PersistenceFailure
}

public sealed record TwCategoryIndexingResult(
    TwCategoryIndexingDisposition Disposition,
    int PageId,
    int? PageRevision = null,
    string? DiagnosticCode = null,
    TwCategoryIndexConsistencyState ConsistencyState = TwCategoryIndexConsistencyState.Unknown,
    TwCategoryRecoveryEligibility RecoveryEligibility = TwCategoryRecoveryEligibility.NotEligible,
    string? ConsistencyEvidence = null);

public enum TwCategoryReconciliationDisposition
{
    Reconciled,
    NoChange,
    Incomplete,
    Superseded,
    Unauthorized,
    Cancelled,
    Unavailable,
    Failed,
    Invalid
}

public sealed record TwCategoryReconciliationResult(
    TwCategoryReconciliationDisposition Disposition,
    int PageId,
    int? RequestedRevision,
    int? ResolvedAuthoritativeRevision,
    TwCategoryIndexingResult IndexingResult,
    TwCategoryRecoveryEligibility RecoveryEligibility,
    string? DiagnosticCode = null);

/// <summary>Pure validation and concurrency policy for source-only tests.</summary>
public static class TwCategoryMembershipReplacementPolicy
{
    public static string? Validate(TwCategoryMemberQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Category is null || string.IsNullOrWhiteSpace(query.Category.CanonicalNavigation)) return "MissingCategory";
        if (query.PageNumber <= 0) return "InvalidPageNumber";
        if (query.PageSize <= 0 || query.PageSize > TwCategoryMembershipLimits.MaxCategoryMemberPageSize) return "PageSizeLimitExceeded";
        return null;
    }

    public static TwCategoryMembershipReplacementDisposition Classify(
        TwCategoryMembershipReplacementRequest request,
        int currentPageRevision,
        IReadOnlyList<TwCategoryMembershipRecord> existingMemberships)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(existingMemberships);

        var validation = Validate(request);
        if (validation is not null) return TwCategoryMembershipReplacementDisposition.InvalidRequest;
        if (!request.ExtractionComplete) return TwCategoryMembershipReplacementDisposition.IncompleteExtraction;
        if (request.ExpectedPageRevision != currentPageRevision) return TwCategoryMembershipReplacementDisposition.RevisionConflict;

        return Equivalent(request.DesiredMemberships, existingMemberships)
            ? TwCategoryMembershipReplacementDisposition.NoChange
            : TwCategoryMembershipReplacementDisposition.Applied;
    }

    public static string? Validate(TwCategoryMembershipReplacementRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PageId <= 0) return "InvalidPageId";
        if (request.ExpectedPageRevision <= 0) return "InvalidExpectedPageRevision";
        if (request.DesiredMemberships is null) return "MissingMembershipSet";
        if (request.IndexGeneration == Guid.Empty) return "MissingIndexGeneration";
        if (request.IndexedAtUtc.Kind != DateTimeKind.Utc) return "IndexTimestampMustBeUtc";

        var duplicateKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var membership in request.DesiredMemberships)
        {
            if (membership.PageId != request.PageId) return "MembershipPageMismatch";
            if (membership.SourcePageRevision != request.ExpectedPageRevision) return "MembershipRevisionMismatch";
            if (membership.IndexGeneration != request.IndexGeneration) return "MembershipGenerationMismatch";
            if (membership.IndexedAtUtc != request.IndexedAtUtc) return "MembershipTimestampMismatch";
            if (membership.IndexState != TwCategoryMembershipIndexState.Verified) return "MembershipNotVerified";
            if (membership.Category is null
                || !TwCategoryIdentity.TryCreate(membership.Category.NormalizedTitle, out var canonicalCategory)
                || canonicalCategory != membership.Category) return "CategoryNotCanonical";
            if (membership.SortKey?.Length > TwCategoryMembershipLimits.MaxSortKeyCharacters) return "SortKeyLimitExceeded";
            if (!duplicateKeys.Add($"{membership.Category.CanonicalNavigation}\u001f{membership.SortKey ?? string.Empty}")) return "DuplicateMembership";
        }

        return null;
    }

    private static bool Equivalent(IReadOnlyList<TwCategoryMembershipRecord> left, IReadOnlyList<TwCategoryMembershipRecord> right)
    {
        static string Key(TwCategoryMembershipRecord value)
            => $"{value.Category.CanonicalNavigation}\u001f{value.SortKey ?? string.Empty}\u001f{value.SourcePageRevision}\u001f{value.SourceRevisionId ?? string.Empty}";

        return left.Select(Key).OrderBy(value => value, StringComparer.Ordinal)
            .SequenceEqual(right.Select(Key).OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal);
    }
}

#pragma warning restore CS1591
