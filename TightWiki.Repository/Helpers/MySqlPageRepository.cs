using Dapper;
using TightWiki.Library;
using TightWiki.Library.Database;
using TightWiki.Library.Caching;
using TightWiki.Library.Security;
using TightWiki.Plugin;
using TightWiki.Plugin.Models;
using TightWiki.Plugin.Interfaces.Persistence;

namespace TightWiki.Repository.Helpers;

/// <summary>
/// Provider-neutral MySQL repository for the bounded page, revision, navigation,
/// tag, and outbound-reference domain. It intentionally does not implement the
/// legacy SQLite-shaped <c>ITwPageRepository</c> contract.
/// </summary>
public sealed class MySqlPageRepository : ITwApplicationPageRepository
{
    private readonly IDatabaseConnectionFactory _connectionFactory;

    public MySqlPageRepository(IDatabaseConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<List<TwPage>> AutoCompletePageAsync(string? searchText, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        var rows = await session.Connection.QueryAsync<TwPage>(new CommandDefinition("SELECT Id, `Namespace`, Navigation, Name FROM Page WHERE IsDeleted = FALSE AND Name LIKE CONCAT('%', @SearchText, '%') ORDER BY Name, Id LIMIT 25;", new { SearchText = searchText ?? string.Empty }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<List<string>> AutoCompleteNamespaceAsync(string? searchText, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        var rows = await session.Connection.QueryAsync<string>(new CommandDefinition("SELECT DISTINCT `Namespace` FROM Page WHERE IsDeleted = FALSE AND `Namespace` IS NOT NULL AND `Namespace` LIKE CONCAT('%', @SearchText, '%') ORDER BY `Namespace` LIMIT 25;", new { SearchText = searchText ?? string.Empty }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<List<TwPage>> SearchPagesAsync(IReadOnlyList<string> searchTerms, int pageNumber, int? pageSize = null, bool? allowFuzzyMatching = null, CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        var tokens = searchTerms.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (tokens.Length == 0) return [];
        var size = pageSize.GetValueOrDefault(25);
        if (size < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));
        var clauses = tokens.Select((_, index) => allowFuzzyMatching == true ? $"(T.Token = @Token{index} OR T.Token LIKE CONCAT(@Token{index}, '%'))" : $"T.Token = @Token{index}").ToArray();
        var parameters = new DynamicParameters();
        for (var index = 0; index < tokens.Length; index++) parameters.Add($"Token{index}", tokens[index]);
        parameters.Add("TokenCount", tokens.Length);
        parameters.Add("PageSize", size);
        parameters.Add("Offset", checked((pageNumber - 1) * size));
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        var sql = $"""
            SELECT P.Id, P.Name, P.`Namespace`, P.Navigation, P.Description, P.Revision,
                   P.CreatedByUserId, P.CreatedDate, P.ModifiedByUserId, P.ModifiedDate,
                   COUNT(DISTINCT T.Token) / @TokenCount * 100.0 AS Score,
                   COUNT(DISTINCT T.Token) / @TokenCount AS `Match`,
                   SUM(T.Weight) AS Weight,
                   @PageSize AS PaginationPageSize,
                   CEILING(COUNT(*) OVER () / @PageSize) AS PaginationPageCount
            FROM Page P JOIN PageToken T ON T.PageId = P.Id
            WHERE P.IsDeleted = FALSE AND ({string.Join(" OR ", clauses)})
            GROUP BY P.Id, P.Name, P.`Namespace`, P.Navigation, P.Description, P.Revision,
                     P.CreatedByUserId, P.CreatedDate, P.ModifiedByUserId, P.ModifiedDate
            HAVING COUNT(DISTINCT T.Token) >= @TokenCount
            ORDER BY Score DESC, P.Name, P.Id
            LIMIT @PageSize OFFSET @Offset;
            """;
        var rows = await session.Connection.QueryAsync<TwPage>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<List<TwPage>> GetPageInfoByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        var rows = await session.Connection.QueryAsync<TwPage>(new CommandDefinition("SELECT DISTINCT P.Id, P.Name, P.Description, P.Navigation, P.CreatedByUserId, P.CreatedDate, P.ModifiedByUserId, P.ModifiedDate FROM PageTag PT JOIN Page P ON P.Id = PT.PageId WHERE PT.Tag = @Tag AND P.IsDeleted = FALSE ORDER BY P.Name, P.Id;", new { Tag = tag }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<List<TwTagAssociation>> GetAssociatedTagsAsync(string tag, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        var rows = await session.Connection.QueryAsync<TwTagAssociation>(new CommandDefinition("SELECT Extent.Tag, COUNT(DISTINCT Extent.PageId) AS PageCount FROM PageTag Root JOIN PageTag Extent ON Extent.PageId = Root.PageId WHERE Root.Tag = @Tag GROUP BY Extent.Tag ORDER BY PageCount DESC, Extent.Tag LIMIT 100;", new { Tag = tag }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<List<TwPageToken>> GetSearchTokensByPageIdAsync(int pageId, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        var rows = await session.Connection.QueryAsync<TwPageToken>(new CommandDefinition("SELECT PageId, Token, DoubleMetaphone, Weight FROM PageToken WHERE PageId = @PageId ORDER BY Token;", new { PageId = pageId }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<string>> GetTagsByPageIdAsync(int pageId, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        var rows = await session.Connection.QueryAsync<string>(new CommandDefinition("SELECT Tag FROM PageTag WHERE PageId = @PageId ORDER BY Tag;", new { PageId = pageId }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<int> GetCurrentPageRevisionAsync(int pageId, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        return await session.Connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT Revision FROM Page WHERE Id = @PageId AND IsDeleted = FALSE;", new { PageId = pageId }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<TwPage>> GetTemplatePagesAsync(CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        var rows = await session.Connection.QueryAsync<TwPage>(new CommandDefinition("SELECT P.Id, P.Name, P.Description, R.Body, R.Revision, P.Navigation, P.CreatedByUserId, P.CreatedDate, R.ModifiedByUserId, R.ModifiedDate FROM Page P JOIN PageRevision R ON R.PageId = P.Id AND R.Revision = P.Revision WHERE P.`Namespace` = 'Templates' AND P.IsDeleted = FALSE ORDER BY P.Navigation;", cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<TwFeatureTemplate>> GetFeatureTemplatesAsync(CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        var rows = await session.Connection.QueryAsync<TwFeatureTemplate>(new CommandDefinition("SELECT Name, Type, 0 AS PageId, Description, TemplateText, PageName AS HelpPageNavigation FROM DefaultFeatureTemplates ORDER BY Name, Type;", cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<int> CreatePageAsync(TwPage page, IEnumerable<string>? tags = null, IEnumerable<TwPageReference>? references = null, IEnumerable<string>? instructions = null, IEnumerable<TwPageToken>? tokens = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var values = Normalize(page);
        var now = DateTime.UtcNow;

        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        await using var transaction = await session.BeginTransactionAsync(cancellationToken);
        try
        {
            var id = await session.Connection.ExecuteScalarAsync<int>(Command(
                "MySql.Pages.CreatePage.sql",
                new
                {
                    values.Name, values.Namespace, values.Navigation, values.Description,
                    CreatedByUserId = page.CreatedByUserId.ToString("D"), CreatedDate = now,
                    ModifiedByUserId = page.ModifiedByUserId.ToString("D"), ModifiedDate = now
                }, transaction.Transaction, cancellationToken));

            await session.Connection.ExecuteAsync(Command("MySql.Pages.InsertPageRevision.sql", new
            {
                PageId = id, Revision = 1, values.Name, values.Namespace, values.Navigation,
                values.Description, Body = page.Body ?? string.Empty,
                DataHash = SecurityUtility.Crc32(page.Body ?? string.Empty),
                ChangeSummary = page.ChangeSummary ?? string.Empty,
                ModifiedByUserId = page.ModifiedByUserId.ToString("D"), ModifiedDate = now
            }, transaction.Transaction, cancellationToken));

            if (tags is not null && references is not null && instructions is not null && tokens is not null)
                await ReplaceDerivedMetadataInTransactionAsync(session.Connection, transaction.Transaction, id, tags, references, instructions, tokens, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            page.Id = id;
            page.Revision = 1;
            page.MostCurrentRevision = 1;
            page.Navigation = values.Navigation;
            return id;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<int> UpdatePageAsync(TwPage page, int expectedRevision, IEnumerable<string>? tags = null, IEnumerable<TwPageReference>? references = null, IEnumerable<string>? instructions = null, IEnumerable<TwPageToken>? tokens = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.Id <= 0) throw new ArgumentOutOfRangeException(nameof(page), "An existing page ID is required.");
        if (expectedRevision <= 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        var values = Normalize(page);
        var now = DateTime.UtcNow;

        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        await using var transaction = await session.BeginTransactionAsync(cancellationToken);
        try
        {
            var current = await session.Connection.QuerySingleOrDefaultAsync<PageState>(Command(
                "SELECT Revision, Body, DataHash FROM PageRevision WHERE PageId = @PageId AND Revision = @ExpectedRevision FOR UPDATE;",
                new { PageId = page.Id, ExpectedRevision = expectedRevision }, transaction.Transaction, cancellationToken));
            if (current is null)
                throw new MySqlPageConcurrencyException(page.Id, expectedRevision);

            var nextRevision = expectedRevision + 1;
            var affected = await session.Connection.ExecuteAsync(Command("MySql.Pages.UpdatePage.sql", new
            {
                PageId = page.Id, values.Name, values.Namespace, values.Navigation, values.Description,
                Revision = nextRevision, ExpectedRevision = expectedRevision,
                ModifiedByUserId = page.ModifiedByUserId.ToString("D"), ModifiedDate = now
            }, transaction.Transaction, cancellationToken));
            if (affected != 1) throw new MySqlPageConcurrencyException(page.Id, expectedRevision);

            await session.Connection.ExecuteAsync(Command("MySql.Pages.InsertPageRevision.sql", new
            {
                PageId = page.Id, Revision = nextRevision, values.Name, values.Namespace, values.Navigation,
                values.Description, Body = page.Body ?? string.Empty,
                DataHash = SecurityUtility.Crc32(page.Body ?? string.Empty),
                ChangeSummary = page.ChangeSummary ?? string.Empty,
                ModifiedByUserId = page.ModifiedByUserId.ToString("D"), ModifiedDate = now
            }, transaction.Transaction, cancellationToken));

            await ReassociatePageAttachmentsInTransactionAsync(session.Connection, transaction.Transaction, page.Id, expectedRevision, nextRevision, cancellationToken);

            if (tags is not null && references is not null && instructions is not null && tokens is not null)
                await ReplaceDerivedMetadataInTransactionAsync(session.Connection, transaction.Transaction, page.Id, tags, references, instructions, tokens, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            page.Revision = nextRevision;
            page.MostCurrentRevision = nextRevision;
            page.Navigation = values.Navigation;
            page.ModifiedDate = now;
            return nextRevision;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public Task<TwPage?> GetPageByIdAsync(int pageId, int? revision = null, bool includeDeleted = false, CancellationToken cancellationToken = default)
        => QuerySingleAsync("MySql.Pages.GetPage.sql", new { PageId = pageId, Revision = revision, IncludeDeleted = includeDeleted }, cancellationToken);

    public Task<TwPage?> GetPageByNavigationAsync(string navigation, int? revision = null, bool includeDeleted = false, CancellationToken cancellationToken = default)
        => QuerySingleAsync("MySql.Pages.GetPageByNavigation.sql", new { Navigation = TwNamespaceNavigation.CleanAndValidate(navigation), Revision = revision, IncludeDeleted = includeDeleted }, cancellationToken);

    public async Task<IReadOnlyList<TwPageRevision>> GetRevisionHistoryAsync(string navigation, int pageNumber = 1, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        var rows = await session.Connection.QueryAsync<TwPageRevision>(Command("MySql.Pages.GetRevisionHistory.sql", new
        {
            Navigation = TwNamespaceNavigation.CleanAndValidate(navigation), PageNumber = pageNumber, PageSize = pageSize,
            Offset = checked((pageNumber - 1) * pageSize)
        }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<TwPage>> GetAllPagesAsync(CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        var rows = await session.Connection.QueryAsync<TwPage>(new CommandDefinition("SELECT P.Id, R.Name, R.`Namespace`, R.Navigation, R.Description, R.Body, R.Revision, P.Revision AS MostCurrentRevision, P.CreatedByUserId, P.CreatedDate, R.ModifiedByUserId, R.ModifiedDate FROM Page P JOIN PageRevision R ON R.PageId = P.Id AND R.Revision = P.Revision WHERE P.IsDeleted = FALSE ORDER BY P.Navigation, P.Id;", cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public Task<TwPage?> GetDeletedPageByIdAsync(int pageId, CancellationToken cancellationToken = default)
        => QuerySingleAsync("SELECT P.Id, R.Name, R.`Namespace`, R.Navigation, R.Description, R.Body, R.Revision, P.Revision AS MostCurrentRevision, P.CreatedByUserId, P.CreatedDate, R.ModifiedByUserId, R.ModifiedDate, P.DeletedByUserId, P.DeletedDate FROM Page P JOIN PageRevision R ON R.PageId = P.Id AND R.Revision = P.Revision WHERE P.Id = @PageId AND P.IsDeleted = TRUE;", new { PageId = pageId }, cancellationToken);

    public async Task<IReadOnlyList<TwDeletedPageRevision>> GetDeletedPageRevisionsAsync(int pageId, int pageNumber = 1, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        var rows = await session.Connection.QueryAsync<TwDeletedPageRevision>(Command("""
            SELECT R.PageId AS Id, R.Name, R.`Namespace`, R.Navigation, R.Description, R.Body,
                   R.Revision, R.ModifiedByUserId, R.ModifiedDate, R.ChangeSummary, R.DataHash,
                   R.DeletedByUserId, R.DeletedDate, U.AccountName AS DeletedByUserName,
                   @PageSize AS PaginationPageSize,
                   CEILING(COUNT(*) OVER () / @PageSize) AS PaginationPageCount
            FROM DeletedPageRevision R
            LEFT JOIN Profile U ON U.UserId = R.DeletedByUserId
            WHERE R.PageId = @PageId
            ORDER BY R.Revision DESC
            LIMIT @PageSize OFFSET @Offset;
            """, new { PageId = pageId, PageSize = pageSize, Offset = checked((pageNumber - 1) * pageSize) }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<TwDeletedPageRevision?> GetDeletedPageRevisionAsync(int pageId, int revision, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        return await session.Connection.QuerySingleOrDefaultAsync<TwDeletedPageRevision>(Command("""
            SELECT R.PageId AS Id, R.Name, R.`Namespace`, R.Navigation, R.Description, R.Body,
                   R.Revision, R.ModifiedByUserId, R.ModifiedDate, R.ChangeSummary, R.DataHash,
                   R.DeletedByUserId, R.DeletedDate, U.AccountName AS DeletedByUserName
            FROM DeletedPageRevision R
            LEFT JOIN Profile U ON U.UserId = R.DeletedByUserId
            WHERE R.PageId = @PageId AND R.Revision = @Revision;
            """, new { PageId = pageId, Revision = revision }, cancellationToken: cancellationToken));
    }

    public async Task ArchivePageRevisionAsync(int pageId, int revision, Guid deletedByUserId, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        await using var transaction = await session.BeginTransactionAsync(cancellationToken);
        string? navigation = null;
        try
        {
            var state = await session.Connection.QuerySingleOrDefaultAsync<PageRevisionState>(Command("""
                SELECT R.Navigation, P.Revision AS CurrentRevision
                FROM PageRevision R JOIN Page P ON P.Id = R.PageId
                WHERE R.PageId = @PageId AND R.Revision = @Revision AND P.IsDeleted = FALSE
                FOR UPDATE;
                """, new { PageId = pageId, Revision = revision }, transaction.Transaction, cancellationToken));
            if (state is null) throw new KeyNotFoundException($"Page revision {pageId}/{revision} was not found.");
            if (state.CurrentRevision == revision) throw new InvalidOperationException("The current page revision cannot be archived until another revision is current.");
            navigation = state.Navigation;

            var archiveParameters = new { PageId = pageId, Revision = revision, DeletedByUserId = deletedByUserId.ToString("D"), DeletedDate = DateTime.UtcNow };
            await session.Connection.ExecuteAsync(Command("""
                INSERT INTO DeletedPageRevision
                    (PageId, Revision, Name, `Namespace`, Navigation, Description, Body, DataHash,
                     ChangeSummary, ModifiedByUserId, ModifiedDate, DeletedByUserId, DeletedDate)
                SELECT PageId, Revision, Name, `Namespace`, Navigation, Description, Body, DataHash,
                       ChangeSummary, ModifiedByUserId, ModifiedDate, @DeletedByUserId, @DeletedDate
                FROM PageRevision WHERE PageId = @PageId AND Revision = @Revision;
                """, archiveParameters, transaction.Transaction, cancellationToken));
            await session.Connection.ExecuteAsync(Command("""
                INSERT INTO DeletedPageRevisionAttachment (PageId, PageFileId, FileRevision, PageRevision)
                SELECT PageId, PageFileId, FileRevision, PageRevision
                FROM PageRevisionAttachment WHERE PageId = @PageId AND PageRevision = @Revision;
                """, archiveParameters, transaction.Transaction, cancellationToken));
            await session.Connection.ExecuteAsync(Command("DELETE FROM PageRevisionAttachment WHERE PageId = @PageId AND PageRevision = @Revision;", archiveParameters, transaction.Transaction, cancellationToken));
            await session.Connection.ExecuteAsync(Command("DELETE FROM PageRevision WHERE PageId = @PageId AND Revision = @Revision;", archiveParameters, transaction.Transaction, cancellationToken));
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
        if (navigation is not null) ClearPageCache(pageId, navigation);
    }

    public async Task PurgeDeletedPageRevisionAsync(int pageId, int revision, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        await using var transaction = await session.BeginTransactionAsync(cancellationToken);
        try
        {
            await session.Connection.ExecuteAsync(Command("DELETE FROM DeletedPageRevision WHERE PageId = @PageId AND Revision = @Revision;", new { PageId = pageId, Revision = revision }, transaction.Transaction, cancellationToken));
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
        ClearPageCacheById(pageId);
    }

    public async Task PurgeDeletedPageRevisionsAsync(int pageId, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        await using var transaction = await session.BeginTransactionAsync(cancellationToken);
        try
        {
            await session.Connection.ExecuteAsync(Command("DELETE FROM DeletedPageRevision WHERE PageId = @PageId;", new { PageId = pageId }, transaction.Transaction, cancellationToken));
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
        ClearPageCacheById(pageId);
    }

    public async Task RestoreDeletedPageRevisionAsync(int pageId, int revision, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        await using var transaction = await session.BeginTransactionAsync(cancellationToken);
        string? navigation = null;
        try
        {
            var archived = await session.Connection.QuerySingleOrDefaultAsync<DeletedRevisionState>(Command("""
                SELECT PageId, Revision, Navigation FROM DeletedPageRevision
                WHERE PageId = @PageId AND Revision = @Revision FOR UPDATE;
                """, new { PageId = pageId, Revision = revision }, transaction.Transaction, cancellationToken));
            if (archived is null) throw new KeyNotFoundException($"Deleted page revision {pageId}/{revision} was not found.");
            var livePage = await session.Connection.QuerySingleOrDefaultAsync<int?>(Command("SELECT Id FROM Page WHERE Id = @PageId FOR UPDATE;", new { PageId = pageId }, transaction.Transaction, cancellationToken));
            if (livePage is null) throw new KeyNotFoundException($"Page {pageId} was not found.");
            navigation = archived.Navigation;

            var restoreParameters = new { PageId = pageId, Revision = revision };
            await session.Connection.ExecuteAsync(Command("""
                INSERT INTO PageRevision
                    (PageId, Revision, Name, `Namespace`, Navigation, Description, Body, DataHash,
                     ChangeSummary, ModifiedByUserId, ModifiedDate)
                SELECT PageId, Revision, Name, `Namespace`, Navigation, Description, Body, DataHash,
                       ChangeSummary, ModifiedByUserId, ModifiedDate
                FROM DeletedPageRevision WHERE PageId = @PageId AND Revision = @Revision;

                """, restoreParameters, transaction.Transaction, cancellationToken));
            await session.Connection.ExecuteAsync(Command("""
                INSERT INTO PageRevisionAttachment (PageId, PageFileId, FileRevision, PageRevision)
                SELECT PageId, PageFileId, FileRevision, PageRevision
                FROM DeletedPageRevisionAttachment WHERE PageId = @PageId AND PageRevision = @Revision;
                """, restoreParameters, transaction.Transaction, cancellationToken));
            await session.Connection.ExecuteAsync(Command("DELETE FROM DeletedPageRevision WHERE PageId = @PageId AND Revision = @Revision;", restoreParameters, transaction.Transaction, cancellationToken));
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
        if (navigation is not null) ClearPageCache(pageId, navigation);
    }

    public async Task<List<TwPage>> GetDeletedPagesAsync(int pageNumber, int? pageSize = null, IReadOnlyList<string>? searchTerms = null, CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        var size = pageSize.GetValueOrDefault(25);
        if (size < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));
        var terms = searchTerms?.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        var predicates = terms.Select((_, index) => $"(P.Name LIKE CONCAT('%', @Term{index}, '%') OR P.Navigation LIKE CONCAT('%', @Term{index}, '%') OR P.Description LIKE CONCAT('%', @Term{index}, '%'))").ToArray();
        var parameters = new DynamicParameters();
        for (var index = 0; index < terms.Length; index++) parameters.Add($"Term{index}", terms[index]);
        parameters.Add("PageSize", size);
        parameters.Add("Offset", checked((pageNumber - 1) * size));
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        var sql = $"""
            SELECT P.Id, P.Name, P.`Namespace`, P.Navigation, P.Description, P.Revision,
                   P.CreatedByUserId, P.CreatedDate, P.ModifiedByUserId, P.ModifiedDate,
                   P.DeletedByUserId, P.DeletedDate,
                   @PageSize AS PaginationPageSize,
                   CEILING(COUNT(*) OVER () / @PageSize) AS PaginationPageCount
            FROM Page P
            WHERE P.IsDeleted = TRUE { (predicates.Length == 0 ? string.Empty : "AND " + string.Join(" AND ", predicates)) }
            ORDER BY P.Name, P.Id
            LIMIT @PageSize OFFSET @Offset;
            """;
        var rows = await session.Connection.QueryAsync<TwPage>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<bool> DeletePageAsync(int pageId, int expectedRevision, Guid deletedByUserId, CancellationToken cancellationToken = default)
    {
        var page = await GetPageByIdAsync(pageId, expectedRevision, cancellationToken: cancellationToken);
        var changed = await ExecuteStateChangeAsync("MySql.Pages.DeletePage.sql", new { PageId = pageId, ExpectedRevision = expectedRevision, DeletedByUserId = deletedByUserId.ToString("D"), DeletedDate = DateTime.UtcNow }, cancellationToken) == 1;
        if (changed && page is not null) ClearPageCache(page.Id, page.Navigation);
        return changed;
    }

    public async Task<bool> RestorePageAsync(int pageId, CancellationToken cancellationToken = default)
    {
        var page = await GetDeletedPageByIdAsync(pageId, cancellationToken);
        var changed = await ExecuteStateChangeAsync("MySql.Pages.RestorePage.sql", new { PageId = pageId }, cancellationToken) == 1;
        if (changed && page is not null) ClearPageCache(page.Id, page.Navigation);
        return changed;
    }

    public async Task ReplaceTagsAsync(int pageId, IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        var values = tags.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        await using var transaction = await session.BeginTransactionAsync(cancellationToken);
        try
        {
            await session.Connection.ExecuteAsync(Command("MySql.Pages.ReplaceTags.sql", new { PageId = pageId }, transaction.Transaction, cancellationToken));
            foreach (var tag in values)
                await session.Connection.ExecuteAsync(Command("INSERT INTO PageTag (PageId, Tag) VALUES (@PageId, @Tag);", new { PageId = pageId, Tag = tag }, transaction.Transaction, cancellationToken));
            await transaction.CommitAsync(cancellationToken);
        }
        catch { await transaction.RollbackAsync(cancellationToken); throw; }
    }

    public async Task ReplaceReferencesAsync(int pageId, IEnumerable<TwPageReference> references, CancellationToken cancellationToken = default)
    {
        var values = references.Where(o => !string.IsNullOrWhiteSpace(o.Navigation)).GroupBy(o => o.Navigation, StringComparer.OrdinalIgnoreCase).Select(o => o.First()).ToList();
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        await using var transaction = await session.BeginTransactionAsync(cancellationToken);
        try
        {
            await session.Connection.ExecuteAsync(Command("MySql.Pages.ReplaceReferences.sql", new { PageId = pageId }, transaction.Transaction, cancellationToken));
            foreach (var reference in values)
                await session.Connection.ExecuteAsync(Command("INSERT INTO PageReference (PageId, ReferencesPageName, ReferencesPageNavigation, ReferencesPageId) VALUES (@PageId, @Name, @Navigation, (SELECT Id FROM Page WHERE ActiveNavigation = @Navigation));", new { PageId = pageId, Name = reference.Name, Navigation = TwNamespaceNavigation.CleanAndValidate(reference.Navigation) }, transaction.Transaction, cancellationToken));
            await transaction.CommitAsync(cancellationToken);
        }
        catch { await transaction.RollbackAsync(cancellationToken); throw; }
    }

    public async Task UpsertCurrentPageEditorAsync(int pageId, Guid userId, string accountName, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        await session.Connection.ExecuteAsync(new CommandDefinition("INSERT INTO CurrentPageEditors (PageId, UserId, AccountName, UTCDate) VALUES (@PageId, @UserId, @AccountName, UTC_TIMESTAMP(6)) ON DUPLICATE KEY UPDATE AccountName = VALUES(AccountName), UTCDate = UTC_TIMESTAMP(6);", new { PageId = pageId, UserId = userId.ToString("D"), AccountName = accountName }, cancellationToken: cancellationToken));
    }

    public async Task DeleteCurrentPageEditorAsync(int pageId, Guid userId, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        await session.Connection.ExecuteAsync(new CommandDefinition("DELETE FROM CurrentPageEditors WHERE PageId = @PageId AND UserId = @UserId;", new { PageId = pageId, UserId = userId.ToString("D") }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<string>> GetCurrentPageEditorsAsync(int pageId, int windowMinutes = 5, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        var rows = await session.Connection.QueryAsync<string>(new CommandDefinition("SELECT AccountName FROM CurrentPageEditors WHERE PageId = @PageId AND UTCDate >= DATE_SUB(UTC_TIMESTAMP(6), INTERVAL @WindowMinutes MINUTE) ORDER BY AccountName;", new { PageId = pageId, WindowMinutes = windowMinutes }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<TwProcessingInstructionCollection> GetProcessingInstructionsAsync(int pageId, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        var rows = await session.Connection.QueryAsync<TwProcessingInstruction>(new CommandDefinition("SELECT Instruction FROM PageProcessingInstruction WHERE PageId = @PageId ORDER BY Instruction;", new { PageId = pageId }, cancellationToken: cancellationToken));
        return new TwProcessingInstructionCollection { Collection = rows.AsList() };
    }

    private async Task<TwPage?> QuerySingleAsync(string resource, object parameters, CancellationToken cancellationToken)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        return await session.Connection.QuerySingleOrDefaultAsync<TwPage>(Command(resource, parameters, cancellationToken: cancellationToken));
    }

    private async Task<int> ExecuteStateChangeAsync(string resource, object parameters, CancellationToken cancellationToken)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        await using var transaction = await session.BeginTransactionAsync(cancellationToken);
        try
        {
            var result = await session.Connection.ExecuteAsync(Command(resource, parameters, transaction.Transaction, cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch { await transaction.RollbackAsync(cancellationToken); throw; }
    }

    private static void ClearPageCache(int pageId, string navigation)
    {
        MemCache.ClearCategory(MemCacheKey.Build(MemCache.Category.Page, [navigation]));
        MemCache.ClearCategory(MemCacheKey.Build(MemCache.Category.Page, [pageId]));
    }

    private static void ClearPageCacheById(int pageId)
        => MemCache.ClearCategory(MemCacheKey.Build(MemCache.Category.Page, [pageId]));

    private static async Task ReplaceDerivedMetadataInTransactionAsync(System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction, int pageId, IEnumerable<string> tags, IEnumerable<TwPageReference> references, IEnumerable<string> instructions, IEnumerable<TwPageToken> tokens, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM PageTag WHERE PageId = @PageId; DELETE FROM PageReference WHERE PageId = @PageId; DELETE FROM PageProcessingInstruction WHERE PageId = @PageId; DELETE FROM PageToken WHERE PageId = @PageId;", new { PageId = pageId }, transaction, cancellationToken: cancellationToken));
        foreach (var tag in tags.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
            await connection.ExecuteAsync(new CommandDefinition("INSERT INTO PageTag (PageId, Tag) VALUES (@PageId, @Tag);", new { PageId = pageId, Tag = tag }, transaction, cancellationToken: cancellationToken));
        foreach (var reference in references.Where(value => !string.IsNullOrWhiteSpace(value.Navigation)).GroupBy(value => value.Navigation, StringComparer.OrdinalIgnoreCase).Select(value => value.First()))
            await connection.ExecuteAsync(new CommandDefinition("INSERT INTO PageReference (PageId, ReferencesPageName, ReferencesPageNavigation, ReferencesPageId) VALUES (@PageId, @Name, @Navigation, (SELECT Id FROM Page WHERE ActiveNavigation = @Navigation LIMIT 1));", new { PageId = pageId, Name = string.IsNullOrWhiteSpace(reference.Namespace) ? reference.Name : $"{reference.Namespace} :: {reference.Name}", Navigation = TwNavigation.Clean(reference.Navigation) }, transaction, cancellationToken: cancellationToken));
        foreach (var instruction in instructions.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
            await connection.ExecuteAsync(new CommandDefinition("INSERT INTO PageProcessingInstruction (PageId, Instruction) VALUES (@PageId, @Instruction);", new { PageId = pageId, Instruction = instruction }, transaction, cancellationToken: cancellationToken));
        foreach (var token in tokens.Where(value => !string.IsNullOrWhiteSpace(value.Token)).GroupBy(value => value.Token, StringComparer.OrdinalIgnoreCase).Select(value => value.First()))
            await connection.ExecuteAsync(new CommandDefinition("INSERT INTO PageToken (PageId, Token, Weight, DoubleMetaphone) VALUES (@PageId, @Token, @Weight, @DoubleMetaphone);", new { PageId = pageId, token.Token, token.Weight, token.DoubleMetaphone }, transaction, cancellationToken: cancellationToken));
    }

    private static Task ReassociatePageAttachmentsInTransactionAsync(System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction, int pageId, int previousRevision, int pageRevision, CancellationToken cancellationToken)
        => connection.ExecuteAsync(Command("""
            INSERT IGNORE INTO PageRevisionAttachment (PageId, PageFileId, FileRevision, PageRevision)
            SELECT PRA.PageId, PRA.PageFileId, PRA.FileRevision, @PageRevision
            FROM PageRevisionAttachment PRA
            JOIN PageFile PF ON PF.Id = PRA.PageFileId AND PF.Revision = PRA.FileRevision
            WHERE PRA.PageId = @PageId AND PRA.PageRevision = @PreviousRevision;
            """, new { PageId = pageId, PreviousRevision = previousRevision, PageRevision = pageRevision }, transaction, cancellationToken));

    private static CommandDefinition Command(string resourceOrSql, object parameters, System.Data.Common.DbTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        var sql = resourceOrSql.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
            ? EmbeddedResourceReader.LoadText(resourceOrSql)
            : resourceOrSql;
        return new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken);
    }

    private static (string Name, string Namespace, string Navigation, string Description) Normalize(TwPage page)
    {
        if (string.IsNullOrWhiteSpace(page.Name)) throw new ArgumentException("A page name is required.", nameof(page));
        var navigation = TwNamespaceNavigation.CleanAndValidate(page.Name);
        if (navigation.Length > 128 || page.Name.Length > 128) throw new ArgumentException("Page names and navigation identifiers may not exceed 128 characters.", nameof(page));
        return (page.Name, page.Namespace, navigation, page.Description ?? string.Empty);
    }

    private sealed class PageState
    {
        public int Revision { get; set; }
        public string Body { get; set; } = string.Empty;
        public int DataHash { get; set; }
    }

    private sealed class PageRevisionState
    {
        public string Navigation { get; set; } = string.Empty;
        public int CurrentRevision { get; set; }
    }

    private sealed class DeletedRevisionState
    {
        public int PageId { get; set; }
        public int Revision { get; set; }
        public string Navigation { get; set; } = string.Empty;
    }
}

public sealed class MySqlPageConcurrencyException(int pageId, int expectedRevision)
    : InvalidOperationException($"Page {pageId} changed since revision {expectedRevision}; reload it before saving.");
