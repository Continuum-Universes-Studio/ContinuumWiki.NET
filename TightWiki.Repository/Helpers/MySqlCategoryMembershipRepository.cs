using Dapper;
using TightWiki.Library.Database;
using TightWiki.Plugin.Interfaces.Persistence;
using TightWiki.Plugin.Models;

namespace TightWiki.Repository.Helpers;

/// <summary>
/// MariaDB category repository. This type is only registered by the explicit
/// category-persistence activation method; the application defaults remain
/// <see cref="RejectingTwCategoryMembershipRepository"/>.
/// </summary>
public sealed class MySqlCategoryMembershipRepository : ITwCategoryMembershipRepository
{
    private readonly IDatabaseConnectionFactory _connectionFactory;

    public MySqlCategoryMembershipRepository(IDatabaseConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<TwCategoryIndexConsistencyResult> GetIndexConsistencyAsync(
        int pageId,
        CancellationToken cancellationToken = default)
    {
        if (pageId <= 0) throw new ArgumentOutOfRangeException(nameof(pageId));

        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        var page = await session.Connection.QuerySingleOrDefaultAsync<PageRevisionRow>(new CommandDefinition("""
            SELECT Revision, IsDeleted
            FROM Page
            WHERE Id = @PageId;
            """, new { PageId = pageId }, cancellationToken: cancellationToken));
        if (page is null || page.IsDeleted)
            return new(TwCategoryIndexConsistencyState.Unknown, pageId, DiagnosticCode: "PageUnavailable");

        var state = await session.Connection.QuerySingleOrDefaultAsync<IndexStateRow>(new CommandDefinition("""
            SELECT IndexedPageRevision, IndexState, IndexGeneration,
                   SourcePageId, SourceRevisionId, SourceWikiIdentity, IndexedAtUtc
            FROM PageCategoryIndexState
            WHERE PageId = @PageId;
            """, new { PageId = pageId }, cancellationToken: cancellationToken));
        if (state is null)
        return new(TwCategoryIndexConsistencyState.Unindexed, pageId, checked((int)page.Revision), DiagnosticCode: "IndexStateMissing");

        var provenance = new TwCategoryIndexProvenance(
            state.SourcePageId,
            state.SourceRevisionId,
            state.SourceWikiIdentity,
            state.IndexGeneration,
            EnsureUtc(state.IndexedAtUtc));
        var durableState = ParseDurableState(state.IndexState);
        var consistency = durableState switch
        {
            TwCategoryDurableIndexState.Incomplete => TwCategoryIndexConsistencyState.Incomplete,
            TwCategoryDurableIndexState.Failed => TwCategoryIndexConsistencyState.Failed,
            TwCategoryDurableIndexState.Unknown => TwCategoryIndexConsistencyState.Unknown,
            TwCategoryDurableIndexState.Verified when state.IndexedPageRevision == page.Revision
                => TwCategoryIndexConsistencyState.Current,
            TwCategoryDurableIndexState.Verified when state.IndexedPageRevision < page.Revision
                => TwCategoryIndexConsistencyState.Stale,
            _ => TwCategoryIndexConsistencyState.Unknown
        };
        var diagnostic = consistency switch
        {
            TwCategoryIndexConsistencyState.Current => "VerifiedRevisionMatchesCurrentPage",
            TwCategoryIndexConsistencyState.Stale => "VerifiedRevisionIsOlderThanCurrentPage",
            TwCategoryIndexConsistencyState.Incomplete => "StoredIndexIsIncomplete",
            TwCategoryIndexConsistencyState.Failed => "StoredIndexFailed",
            TwCategoryIndexConsistencyState.Unknown when state.IndexedPageRevision > page.Revision => "IndexedRevisionAheadOfCurrentPage",
            _ => "StoredIndexStateUnknown"
        };
        return new(consistency, pageId, checked((int)page.Revision), checked((int)state.IndexedPageRevision), provenance, diagnostic);
    }

    public async Task<TwCategoryIndexConsistencyResult> GetCategoryIndexConsistencyAsync(
        TwCategoryIdentity category,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(category);
        if (!TwCategoryIdentity.TryCreate(category.NormalizedTitle, out var canonical)
            || canonical != category)
            throw new ArgumentException("Category navigation must be canonical.", nameof(category));

        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        var state = await session.Connection.QuerySingleAsync<string>(new CommandDefinition("""
            SELECT CASE
                WHEN EXISTS (
                    SELECT 1
                    FROM Page P
                    INNER JOIN PageCategoryIndexState S ON S.PageId = P.Id
                    WHERE P.IsDeleted = FALSE AND S.IndexState = 'Failed'
                ) THEN 'Failed'
                WHEN EXISTS (
                    SELECT 1
                    FROM Page P
                    INNER JOIN PageCategoryIndexState S ON S.PageId = P.Id
                    WHERE P.IsDeleted = FALSE AND S.IndexState = 'Incomplete'
                ) THEN 'Incomplete'
                WHEN EXISTS (
                    SELECT 1
                    FROM Page P
                    INNER JOIN PageCategoryIndexState S ON S.PageId = P.Id
                    WHERE P.IsDeleted = FALSE AND S.IndexState = 'Unknown'
                ) THEN 'Unknown'
                WHEN EXISTS (
                    SELECT 1
                    FROM Page P
                    INNER JOIN PageCategoryIndexState S ON S.PageId = P.Id
                    WHERE P.IsDeleted = FALSE
                      AND S.IndexState = 'Verified'
                      AND S.IndexedPageRevision > P.Revision
                ) THEN 'Unknown'
                WHEN EXISTS (
                    SELECT 1
                    FROM Page P
                    INNER JOIN PageCategoryIndexState S ON S.PageId = P.Id
                    WHERE P.IsDeleted = FALSE
                      AND S.IndexState = 'Verified'
                      AND S.IndexedPageRevision < P.Revision
                ) THEN 'Stale'
                WHEN EXISTS (
                    SELECT 1
                    FROM Page P
                    LEFT JOIN PageCategoryIndexState S ON S.PageId = P.Id
                    WHERE P.IsDeleted = FALSE
                      AND (S.PageId IS NULL
                           OR S.IndexState <> 'Verified'
                           OR S.IndexedPageRevision <> P.Revision)
                ) THEN 'Unindexed'
                ELSE 'Current'
            END;
            """, cancellationToken: cancellationToken));

        if (!Enum.TryParse<TwCategoryIndexConsistencyState>(state, ignoreCase: false, out var consistency))
            consistency = TwCategoryIndexConsistencyState.Unknown;
        return new(consistency, 0, DiagnosticCode: $"CategoryIndexState:{state}");
    }

    public async Task<IReadOnlyList<TwCategoryMembershipRecord>> GetMembershipsByPageIdAsync(
        int pageId,
        CancellationToken cancellationToken = default)
    {
        if (pageId <= 0) throw new ArgumentOutOfRangeException(nameof(pageId));

        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        var rows = await session.Connection.QueryAsync<MembershipRow>(new CommandDefinition("""
            SELECT PageId, CategoryNavigation, SortKey, SourcePageRevision,
                   SourcePageId, SourceRevisionId, SourceWikiIdentity,
                   IndexGeneration, IndexedAtUtc, IndexState
            FROM PageCategoryMembership
            WHERE PageId = @PageId
            ORDER BY CategoryNavigation, SortKey, PageId;
            """, new { PageId = pageId }, cancellationToken: cancellationToken));

        return rows.Select(MapMembership).ToList();
    }

    public async Task<TwCategoryMembershipReplacementResult> ReplaceMembershipsAsync(
        TwCategoryMembershipReplacementRequest request,
        CancellationToken cancellationToken = default)
    {
        var validation = TwCategoryMembershipReplacementPolicy.Validate(request);
        if (validation is not null)
            return Invalid(request?.PageId ?? 0, validation);
        if (!request.ExtractionComplete)
            return new(TwCategoryMembershipReplacementDisposition.IncompleteExtraction, request.PageId, request.ExpectedPageRevision, "IncompleteExtraction");

        var sourcePageId = request.SourcePageId ?? request.DesiredMemberships.FirstOrDefault()?.SourcePageId;
        var sourceRevisionId = request.SourceRevisionId ?? request.DesiredMemberships.FirstOrDefault()?.SourceRevisionId;
        var sourceWikiIdentity = request.SourceWikiIdentity ?? request.DesiredMemberships.FirstOrDefault()?.SourceWikiIdentity;
        if (string.IsNullOrWhiteSpace(sourcePageId) || string.IsNullOrWhiteSpace(sourceRevisionId))
            return Invalid(request.PageId, "MissingSourceProvenance");

        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        await using var transaction = await session.BeginTransactionAsync(cancellationToken);
        try
        {
            var page = await session.Connection.QuerySingleOrDefaultAsync<PageRevisionRow>(new CommandDefinition("""
                SELECT Revision, IsDeleted
                FROM Page
                WHERE Id = @PageId
                FOR UPDATE;
                """, new { request.PageId }, transaction: transaction.Transaction, cancellationToken: cancellationToken));
            if (page is null || page.IsDeleted)
                return new(TwCategoryMembershipReplacementDisposition.RevisionConflict, request.PageId, DiagnosticCode: "PageUnavailable");
            if (page.Revision != (uint)request.ExpectedPageRevision)
                return new(TwCategoryMembershipReplacementDisposition.RevisionConflict, request.PageId, checked((int)page.Revision), "CurrentPageRevisionMismatch");

            var existingState = await session.Connection.QuerySingleOrDefaultAsync<IndexStateRow>(new CommandDefinition("""
                SELECT IndexedPageRevision, IndexState, IndexGeneration,
                       SourcePageId, SourceRevisionId, SourceWikiIdentity, IndexedAtUtc
                FROM PageCategoryIndexState
                WHERE PageId = @PageId
                FOR UPDATE;
                """, new { request.PageId }, transaction: transaction.Transaction, cancellationToken: cancellationToken));
            if (existingState is not null && existingState.IndexedPageRevision > (uint)request.ExpectedPageRevision)
                return new(TwCategoryMembershipReplacementDisposition.RevisionConflict, request.PageId, checked((int)existingState.IndexedPageRevision), "NewerIndexRevisionExists");

            var existing = await session.Connection.QueryAsync<MembershipRow>(new CommandDefinition("""
                SELECT PageId, CategoryNavigation, SortKey, SourcePageRevision,
                       SourcePageId, SourceRevisionId, SourceWikiIdentity,
                       IndexGeneration, IndexedAtUtc, IndexState
                FROM PageCategoryMembership
                WHERE PageId = @PageId;
                """, new { request.PageId }, transaction: transaction.Transaction, cancellationToken: cancellationToken));
            if (existingState is not null
                && string.Equals(existingState.IndexState, nameof(TwCategoryDurableIndexState.Verified), StringComparison.Ordinal)
                && existingState.IndexedPageRevision == (uint)request.ExpectedPageRevision
                && Equivalent(request.DesiredMemberships, existing.Select(MapMembership).ToList()))
            {
                await transaction.CommitAsync(cancellationToken);
                return new(TwCategoryMembershipReplacementDisposition.NoChange, request.PageId, request.ExpectedPageRevision, "VerifiedMembershipsAlreadyCurrent");
            }

            await session.Connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM PageCategoryMembership WHERE PageId = @PageId;",
                new { request.PageId }, transaction: transaction.Transaction, cancellationToken: cancellationToken));

            foreach (var membership in request.DesiredMemberships)
            {
                await session.Connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO PageCategoryMembership
                        (PageId, CategoryNavigation, SortKey, SourcePageRevision,
                         SourcePageId, SourceRevisionId, SourceWikiIdentity,
                         IndexGeneration, IndexedAtUtc, IndexState)
                    VALUES
                        (@PageId, @CategoryNavigation, @SortKey, @SourcePageRevision,
                         @SourcePageId, @SourceRevisionId, @SourceWikiIdentity,
                         @IndexGeneration, @IndexedAtUtc, 'Verified');
                    """, new
                {
                    PageId = request.PageId,
                    CategoryNavigation = membership.Category.CanonicalNavigation,
                    membership.SortKey,
                    SourcePageRevision = request.ExpectedPageRevision,
                    SourcePageId = membership.SourcePageId ?? sourcePageId,
                    SourceRevisionId = membership.SourceRevisionId ?? sourceRevisionId,
                    SourceWikiIdentity = membership.SourceWikiIdentity ?? sourceWikiIdentity,
                    request.IndexGeneration,
                    request.IndexedAtUtc
                }, transaction: transaction.Transaction, cancellationToken: cancellationToken));
            }

            await session.Connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO PageCategoryIndexState
                    (PageId, IndexedPageRevision, IndexState, IndexGeneration,
                     SourcePageId, SourceRevisionId, SourceWikiIdentity, IndexedAtUtc)
                VALUES
                    (@PageId, @IndexedPageRevision, 'Verified', @IndexGeneration,
                     @SourcePageId, @SourceRevisionId, @SourceWikiIdentity, @IndexedAtUtc)
                ON DUPLICATE KEY UPDATE
                    IndexedPageRevision = VALUES(IndexedPageRevision),
                    IndexState = VALUES(IndexState),
                    IndexGeneration = VALUES(IndexGeneration),
                    SourcePageId = VALUES(SourcePageId),
                    SourceRevisionId = VALUES(SourceRevisionId),
                    SourceWikiIdentity = VALUES(SourceWikiIdentity),
                    IndexedAtUtc = VALUES(IndexedAtUtc);
                """, new
            {
                PageId = request.PageId,
                IndexedPageRevision = request.ExpectedPageRevision,
                request.IndexGeneration,
                SourcePageId = sourcePageId,
                SourceRevisionId = sourceRevisionId,
                SourceWikiIdentity = sourceWikiIdentity,
                request.IndexedAtUtc
            }, transaction: transaction.Transaction, cancellationToken: cancellationToken));

            await transaction.CommitAsync(cancellationToken);
            return new(TwCategoryMembershipReplacementDisposition.Applied, request.PageId, request.ExpectedPageRevision, "MembershipsReplaced");
        }
        catch (OperationCanceledException)
        {
            await RollbackSafelyAsync(transaction);
            throw;
        }
        catch
        {
            await RollbackSafelyAsync(transaction);
            return new(TwCategoryMembershipReplacementDisposition.PersistenceFailure, request.PageId, request.ExpectedPageRevision, "CategoryPersistenceFailure");
        }
    }

    public async Task<TwCategoryMemberPage> GetMembersAsync(
        TwCategoryMemberQuery query,
        CancellationToken cancellationToken = default)
    {
        var validation = TwCategoryMembershipReplacementPolicy.Validate(query);
        if (validation is not null)
            throw new ArgumentException(validation, nameof(query));
        if (!TwCategoryIdentity.TryCreate(query.Category.NormalizedTitle, out var canonicalCategory)
            || canonicalCategory != query.Category)
            throw new ArgumentException("Category navigation must be canonical.", nameof(query));

        var pageSize = query.PageSize;
        var parameters = new
        {
            CategoryNavigation = query.Category.CanonicalNavigation,
            ViewerUserId = query.ViewerUserId?.ToString("D"),
            LimitPlusOne = pageSize + 1,
            Offset = checked((query.PageNumber - 1) * pageSize)
        };
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        var rows = await session.Connection.QueryAsync<CategoryMemberRow>(new CommandDefinition("""
            WITH EffectiveReadRules AS (
                SELECT RP.Namespace, RP.PageId, PD.Name AS PermissionDisposition
                FROM RolePermission RP
                INNER JOIN Role R ON R.Id = RP.RoleId
                INNER JOIN Permission P ON P.Id = RP.PermissionId AND P.Name = 'Read'
                INNER JOIN PermissionDisposition PD ON PD.Id = RP.PermissionDispositionId
                WHERE (@ViewerUserId IS NULL AND R.Name = 'Anonymous')
                   OR (@ViewerUserId IS NOT NULL AND EXISTS (
                       SELECT 1 FROM AccountRole AR
                       WHERE AR.RoleId = RP.RoleId AND AR.UserId = @ViewerUserId))
                UNION ALL
                SELECT AP.Namespace, AP.PageId, PD.Name AS PermissionDisposition
                FROM AccountPermission AP
                INNER JOIN Permission P ON P.Id = AP.PermissionId AND P.Name = 'Read'
                INNER JOIN PermissionDisposition PD ON PD.Id = AP.PermissionDispositionId
                WHERE @ViewerUserId IS NOT NULL AND AP.UserId = @ViewerUserId
            )
            SELECT M.PageId, P.Navigation AS PageNavigation, P.Name AS PageName,
                   P.Description, M.SortKey
            FROM PageCategoryMembership M
            INNER JOIN PageCategoryIndexState S ON S.PageId = M.PageId
                AND S.IndexState = 'Verified'
                AND S.IndexedPageRevision = M.SourcePageRevision
            INNER JOIN Page P ON P.Id = M.PageId AND P.IsDeleted = FALSE
            WHERE M.CategoryNavigation = @CategoryNavigation
              AND (
                    EXISTS (
                        SELECT 1 FROM AccountRole AR
                        INNER JOIN Role R ON R.Id = AR.RoleId
                        WHERE @ViewerUserId IS NOT NULL
                          AND AR.UserId = @ViewerUserId
                          AND R.Name = 'Administrator'
                    )
                    OR (
                        EXISTS (SELECT 1 FROM EffectiveReadRules X
                            WHERE X.PageId = CAST(P.Id AS CHAR)
                              AND X.PermissionDisposition = 'Allow')
                        AND NOT EXISTS (SELECT 1 FROM EffectiveReadRules X
                            WHERE X.PageId = CAST(P.Id AS CHAR)
                              AND X.PermissionDisposition = 'Deny')
                    )
                    OR (
                        NOT EXISTS (SELECT 1 FROM EffectiveReadRules X WHERE X.PageId = CAST(P.Id AS CHAR))
                        AND P.`Namespace` IS NOT NULL
                        AND EXISTS (SELECT 1 FROM EffectiveReadRules X WHERE X.Namespace = P.`Namespace`
                            AND X.PermissionDisposition = 'Allow')
                        AND NOT EXISTS (SELECT 1 FROM EffectiveReadRules X WHERE X.Namespace = P.`Namespace`
                            AND X.PermissionDisposition = 'Deny')
                    )
                    OR (
                        NOT EXISTS (SELECT 1 FROM EffectiveReadRules X WHERE X.PageId = CAST(P.Id AS CHAR))
                        AND NOT EXISTS (SELECT 1 FROM EffectiveReadRules X WHERE X.Namespace = P.`Namespace` AND P.`Namespace` IS NOT NULL)
                        AND EXISTS (SELECT 1 FROM EffectiveReadRules X WHERE X.PageId = '*'
                            AND X.PermissionDisposition = 'Allow')
                        AND NOT EXISTS (SELECT 1 FROM EffectiveReadRules X WHERE X.PageId = '*'
                            AND X.PermissionDisposition = 'Deny')
                    )
                    OR (
                        NOT EXISTS (SELECT 1 FROM EffectiveReadRules X WHERE X.PageId = CAST(P.Id AS CHAR))
                        AND NOT EXISTS (SELECT 1 FROM EffectiveReadRules X WHERE X.Namespace = P.`Namespace` AND P.`Namespace` IS NOT NULL)
                        AND NOT EXISTS (SELECT 1 FROM EffectiveReadRules X WHERE X.PageId = '*')
                        AND EXISTS (SELECT 1 FROM EffectiveReadRules X WHERE X.Namespace = '*'
                            AND X.PermissionDisposition = 'Allow')
                        AND NOT EXISTS (SELECT 1 FROM EffectiveReadRules X WHERE X.Namespace = '*'
                            AND X.PermissionDisposition = 'Deny')
                    )
                  )
            ORDER BY (M.SortKey IS NULL), M.SortKey COLLATE utf8mb4_bin, M.PageId
            LIMIT @LimitPlusOne OFFSET @Offset;
            """, parameters, cancellationToken: cancellationToken));

        var members = rows.Select(row => new TwCategoryMember(
            checked((int)row.PageId), row.PageNavigation, row.PageName, row.Description, row.SortKey)).ToList();
        var hasMore = members.Count > pageSize;
        if (hasMore) members.RemoveAt(members.Count - 1);
        return new(members, hasMore);
    }

    public async Task<TwCategoryMembershipReplacementResult> InvalidateMembershipsAsync(
        int pageId,
        int expectedPageRevision,
        CancellationToken cancellationToken = default)
    {
        if (pageId <= 0) return Invalid(pageId, "InvalidPageId");
        if (expectedPageRevision <= 0) return Invalid(pageId, "InvalidExpectedPageRevision");

        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        await using var transaction = await session.BeginTransactionAsync(cancellationToken);
        try
        {
            var page = await session.Connection.QuerySingleOrDefaultAsync<PageRevisionRow>(new CommandDefinition("""
                SELECT Revision, IsDeleted FROM Page WHERE Id = @PageId FOR UPDATE;
                """, new { PageId = pageId }, transaction: transaction.Transaction, cancellationToken: cancellationToken));
            // A committed deletion is the only safe caller for this boundary:
            // require the row to be deleted and the exact authoritative
            // revision before removing its visible membership rows.
            if (page is null || !page.IsDeleted || page.Revision != (uint)expectedPageRevision)
                return new(TwCategoryMembershipReplacementDisposition.RevisionConflict, pageId, page is null ? null : checked((int)page.Revision), "CurrentPageRevisionMismatch");

            await session.Connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM PageCategoryMembership WHERE PageId = @PageId;",
                new { PageId = pageId }, transaction: transaction.Transaction, cancellationToken: cancellationToken));
            await session.Connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO PageCategoryIndexState
                    (PageId, IndexedPageRevision, IndexState, IndexGeneration, IndexedAtUtc)
                VALUES (@PageId, @Revision, 'Unknown', @IndexGeneration, @IndexedAtUtc)
                ON DUPLICATE KEY UPDATE
                    IndexedPageRevision = VALUES(IndexedPageRevision),
                    IndexState = VALUES(IndexState),
                    IndexGeneration = VALUES(IndexGeneration),
                    SourcePageId = NULL,
                    SourceRevisionId = NULL,
                    SourceWikiIdentity = NULL,
                    IndexedAtUtc = VALUES(IndexedAtUtc);
                """, new { PageId = pageId, Revision = expectedPageRevision, IndexGeneration = Guid.NewGuid(), IndexedAtUtc = DateTime.UtcNow }, transaction: transaction.Transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return new(TwCategoryMembershipReplacementDisposition.Applied, pageId, expectedPageRevision, "MembershipsInvalidatedAfterDeletion");
        }
        catch (OperationCanceledException)
        {
            await RollbackSafelyAsync(transaction);
            throw;
        }
        catch
        {
            await RollbackSafelyAsync(transaction);
            return new(TwCategoryMembershipReplacementDisposition.PersistenceFailure, pageId, expectedPageRevision, "CategoryPersistenceFailure");
        }
    }

    private static TwCategoryMembershipRecord MapMembership(MembershipRow row)
    {
        if (!TwCategoryIdentity.TryCreateFromNavigation(row.CategoryNavigation, out var category))
            throw new InvalidDataException("The category membership table contains a non-canonical category navigation.");

        return new(
            checked((int)row.PageId),
            category,
            row.SortKey,
            checked((int)row.SourcePageRevision),
            row.SourcePageId,
            row.SourceRevisionId,
            row.SourceWikiIdentity,
            row.IndexGeneration,
            EnsureUtc(row.IndexedAtUtc),
            ParseMembershipState(row.IndexState));
    }

    private static TwCategoryMembershipIndexState ParseMembershipState(string value)
        => Enum.TryParse<TwCategoryMembershipIndexState>(value, ignoreCase: false, out var state)
            ? state
            : throw new InvalidDataException("The category membership table contains an unknown index state.");

    private static TwCategoryDurableIndexState ParseDurableState(string value)
        => Enum.TryParse<TwCategoryDurableIndexState>(value, ignoreCase: false, out var state)
            ? state
            : throw new InvalidDataException("The category index state table contains an unknown index state.");

    private static bool Equivalent(
        IReadOnlyList<TwCategoryMembershipRecord> left,
        IReadOnlyList<TwCategoryMembershipRecord> right)
    {
        static string Key(TwCategoryMembershipRecord value)
            => $"{value.Category.CanonicalNavigation}\u001f{value.SortKey ?? string.Empty}\u001f{value.SourcePageRevision}\u001f{value.SourceRevisionId ?? string.Empty}";

        return left.Select(Key).OrderBy(value => value, StringComparer.Ordinal)
            .SequenceEqual(right.Select(Key).OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal);
    }

    private static TwCategoryMembershipReplacementResult Invalid(int pageId, string diagnosticCode)
        => new(TwCategoryMembershipReplacementDisposition.InvalidRequest, pageId, DiagnosticCode: diagnosticCode);

    private static string SanitizeDiagnosticMessage(string message)
        => new string(message.Take(160).Select(character => char.IsAsciiLetterOrDigit(character) || character is ' ' or ':' or '.' or '-' or '_' or '(' or ')' ? character : '_').ToArray());

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static async Task RollbackSafelyAsync(IDatabaseTransaction transaction)
    {
        try { await transaction.RollbackAsync(CancellationToken.None); }
        catch { /* Preserve the original persistence or cancellation result. */ }
    }

    private sealed record PageRevisionRow(uint Revision, bool IsDeleted);

    private sealed record IndexStateRow(
        uint IndexedPageRevision,
        string IndexState,
        Guid IndexGeneration,
        string? SourcePageId,
        string? SourceRevisionId,
        string? SourceWikiIdentity,
        DateTime IndexedAtUtc);

    private sealed record MembershipRow(
        uint PageId,
        string CategoryNavigation,
        string? SortKey,
        uint SourcePageRevision,
        string? SourcePageId,
        string? SourceRevisionId,
        string? SourceWikiIdentity,
        Guid IndexGeneration,
        DateTime IndexedAtUtc,
        string IndexState);

    private sealed record CategoryMemberRow(
        uint PageId,
        string PageNavigation,
        string PageName,
        string Description,
        string? SortKey);
}
