using Dapper;
using TightWiki.Library.Database;
using TightWiki.Library.Security;
using TightWiki.Plugin;
using TightWiki.Plugin.Models;
using TightWiki.Plugin.Interfaces.Persistence;

namespace TightWiki.Repository.Helpers;

/// <summary>Provider-neutral MySQL support operations for comments, files, instructions, and search tokens.</summary>
public sealed class MySqlPageSupportRepository : ITwApplicationPageSupportRepository
{
    private readonly IDatabaseConnectionFactory _connectionFactory;

    public MySqlPageSupportRepository(IDatabaseConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<int> InsertCommentAsync(int pageId, Guid userId, string body, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        return await session.Connection.ExecuteScalarAsync<int>(new CommandDefinition("INSERT INTO PageComment (PageId, CreatedDate, UserId, Body) VALUES (@PageId, @CreatedDate, @UserId, @Body); SELECT LAST_INSERT_ID();", new { PageId = pageId, CreatedDate = DateTime.UtcNow, UserId = userId.ToString("D"), Body = body }, cancellationToken: cancellationToken));
    }

    public async Task<int> DeleteCommentAsync(int pageId, int commentId, Guid? userId = null, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        return await session.Connection.ExecuteAsync(new CommandDefinition("DELETE FROM PageComment WHERE PageId = @PageId AND Id = @CommentId AND (@UserId IS NULL OR UserId = @UserId);", new { PageId = pageId, CommentId = commentId, UserId = userId?.ToString("D") }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<TwPageComment>> GetCommentsAsync(int pageId, int pageNumber = 1, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        var rows = await session.Connection.QueryAsync<TwPageComment>(new CommandDefinition("""
            SELECT C.Id, C.PageId, C.UserId, C.Body, U.UserName, P.Navigation AS UserNavigation, W.Name AS PageName, C.CreatedDate
            FROM PageComment C
            JOIN Page W ON W.Id = C.PageId
            LEFT JOIN AspNetUsers U ON U.Id = C.UserId
            LEFT JOIN Profile P ON P.UserId = C.UserId
            WHERE C.PageId = @PageId ORDER BY C.CreatedDate DESC, C.Id DESC LIMIT @PageSize OFFSET @Offset;
            """, new { PageId = pageId, PageSize = pageSize, Offset = (pageNumber - 1) * pageSize }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<int> UpsertFileAsync(int pageId, string name, string navigation, string contentType, byte[] data, Guid createdByUserId, int dataHash, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        await using var transaction = await session.BeginTransactionAsync(cancellationToken);
        try
        {
            var fileId = await session.Connection.ExecuteScalarAsync<int>(new CommandDefinition("INSERT INTO PageFile (PageId, Name, Navigation, Revision, CreatedDate) VALUES (@PageId, @Name, @Navigation, 0, @CreatedDate) ON DUPLICATE KEY UPDATE Id = LAST_INSERT_ID(Id), Navigation = VALUES(Navigation); SELECT LAST_INSERT_ID();", new { PageId = pageId, Name = name, Navigation = navigation, CreatedDate = DateTime.UtcNow }, transaction.Transaction, cancellationToken: cancellationToken));
            var revision = await session.Connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COALESCE(MAX(Revision), -1) + 1 FROM PageFileRevision WHERE PageFileId = @FileId;", new { FileId = fileId }, transaction.Transaction, cancellationToken: cancellationToken));
            await session.Connection.ExecuteAsync(new CommandDefinition("INSERT INTO PageFileRevision (PageFileId, ContentType, Size, CreatedByUserId, CreatedDate, Data, Revision, DataHash) VALUES (@FileId, @ContentType, @Size, @UserId, @CreatedDate, @Data, @Revision, @DataHash); UPDATE PageFile SET Revision = @Revision WHERE Id = @FileId;", new { FileId = fileId, ContentType = contentType, Size = data.LongLength, UserId = createdByUserId.ToString("D"), CreatedDate = DateTime.UtcNow, Data = data, Revision = revision, DataHash = dataHash }, transaction.Transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return fileId;
        }
        catch { await transaction.RollbackAsync(cancellationToken); throw; }
    }

    public async Task ReplaceProcessingInstructionsAsync(int pageId, IEnumerable<string> instructions, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        await using var transaction = await session.BeginTransactionAsync(cancellationToken);
        try
        {
            await session.Connection.ExecuteAsync(new CommandDefinition("DELETE FROM PageProcessingInstruction WHERE PageId = @PageId;", new { PageId = pageId }, transaction.Transaction, cancellationToken: cancellationToken));
            foreach (var instruction in instructions.Where(o => !string.IsNullOrWhiteSpace(o)).Distinct(StringComparer.OrdinalIgnoreCase))
                await session.Connection.ExecuteAsync(new CommandDefinition("INSERT INTO PageProcessingInstruction (PageId, Instruction) VALUES (@PageId, @Instruction);", new { PageId = pageId, Instruction = instruction }, transaction.Transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
        }
        catch { await transaction.RollbackAsync(cancellationToken); throw; }
    }

    public async Task ReplaceSearchTokensAsync(int pageId, IEnumerable<TwPageToken> tokens, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        await using var transaction = await session.BeginTransactionAsync(cancellationToken);
        try
        {
            await session.Connection.ExecuteAsync(new CommandDefinition("DELETE FROM PageToken WHERE PageId = @PageId;", new { PageId = pageId }, transaction.Transaction, cancellationToken: cancellationToken));
            foreach (var token in tokens.Where(o => !string.IsNullOrWhiteSpace(o.Token)).GroupBy(o => o.Token, StringComparer.OrdinalIgnoreCase).Select(o => o.First()))
                await session.Connection.ExecuteAsync(new CommandDefinition("INSERT INTO PageToken (PageId, Token, Weight, DoubleMetaphone) VALUES (@PageId, @Token, @Weight, @DoubleMetaphone);", new { PageId = pageId, token.Token, token.Weight, token.DoubleMetaphone }, transaction.Transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
        }
        catch { await transaction.RollbackAsync(cancellationToken); throw; }
    }

    public async Task<IReadOnlyList<int>> FindPageIdsByTokensAsync(IEnumerable<string> tokens, CancellationToken cancellationToken = default)
    {
        var values = tokens.Where(o => !string.IsNullOrWhiteSpace(o)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (values.Length == 0) return [];
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        var rows = await session.Connection.QueryAsync<int>(new CommandDefinition("SELECT DISTINCT PageId FROM PageToken WHERE Token IN @Tokens ORDER BY PageId;", new { Tokens = values }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task ReplaceDerivedMetadataAsync(int pageId, IEnumerable<string> tags, IEnumerable<TwPageReference> references, IEnumerable<string> instructions, IEnumerable<TwPageToken> tokens, CancellationToken cancellationToken = default)
    {
        var tagValues = tags.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var referenceValues = references.Where(value => !string.IsNullOrWhiteSpace(value.Navigation)).GroupBy(value => value.Navigation, StringComparer.OrdinalIgnoreCase).Select(value => value.First()).ToArray();
        var instructionValues = instructions.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var tokenValues = tokens.Where(value => !string.IsNullOrWhiteSpace(value.Token)).GroupBy(value => value.Token, StringComparer.OrdinalIgnoreCase).Select(value => value.First()).ToArray();
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        await using var transaction = await session.BeginTransactionAsync(cancellationToken);
        try
        {
            await session.Connection.ExecuteAsync(Command("DELETE FROM PageTag WHERE PageId = @PageId; DELETE FROM PageReference WHERE PageId = @PageId; DELETE FROM PageProcessingInstruction WHERE PageId = @PageId; DELETE FROM PageToken WHERE PageId = @PageId;", new { PageId = pageId }, transaction.Transaction, cancellationToken));
            foreach (var tag in tagValues)
                await session.Connection.ExecuteAsync(Command("INSERT INTO PageTag (PageId, Tag) VALUES (@PageId, @Tag);", new { PageId = pageId, Tag = tag }, transaction.Transaction, cancellationToken));
            foreach (var reference in referenceValues)
                await session.Connection.ExecuteAsync(Command("INSERT INTO PageReference (PageId, ReferencesPageName, ReferencesPageNavigation, ReferencesPageId) VALUES (@PageId, @Name, @Navigation, (SELECT Id FROM Page WHERE ActiveNavigation = @Navigation LIMIT 1));", new { PageId = pageId, Name = string.IsNullOrWhiteSpace(reference.Namespace) ? reference.Name : $"{reference.Namespace} :: {reference.Name}", Navigation = TwNavigation.Clean(reference.Navigation) }, transaction.Transaction, cancellationToken));
            foreach (var instruction in instructionValues)
                await session.Connection.ExecuteAsync(Command("INSERT INTO PageProcessingInstruction (PageId, Instruction) VALUES (@PageId, @Instruction);", new { PageId = pageId, Instruction = instruction }, transaction.Transaction, cancellationToken));
            foreach (var token in tokenValues)
                await session.Connection.ExecuteAsync(Command("INSERT INTO PageToken (PageId, Token, Weight, DoubleMetaphone) VALUES (@PageId, @Token, @Weight, @DoubleMetaphone);", new { PageId = pageId, token.Token, token.Weight, token.DoubleMetaphone }, transaction.Transaction, cancellationToken));
            await transaction.CommitAsync(cancellationToken);
        }
        catch { await transaction.RollbackAsync(cancellationToken); throw; }
    }

    public async Task<int> GetCountOfPageAttachmentsAsync(int pageId, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        return await session.Connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM PageFile WHERE PageId = @PageId;", new { PageId = pageId }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<TwPageFileAttachmentInfo>> GetPageFilesInfoByPageIdAsync(int pageId, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        var rows = await session.Connection.QueryAsync<TwPageFileAttachmentInfo>(new CommandDefinition("""
            SELECT PF.Id, PF.PageId, PF.Name, PFR.ContentType, PFR.Size, PF.CreatedDate,
                   PR.Revision AS PageRevision, PFR.Revision AS FileRevision,
                   PF.Navigation AS FileNavigation, P.Navigation AS PageNavigation
            FROM PageFile PF JOIN Page P ON P.Id = PF.PageId AND P.IsDeleted = FALSE
            JOIN PageRevision PR ON PR.PageId = P.Id AND PR.Revision = P.Revision
            JOIN PageRevisionAttachment PRA ON PRA.PageId = P.Id AND PRA.PageFileId = PF.Id AND PRA.PageRevision = PR.Revision
            JOIN PageFileRevision PFR ON PFR.PageFileId = PF.Id AND PFR.Revision = PRA.FileRevision
            WHERE P.Id = @PageId ORDER BY PF.Name, PF.Id;
            """, new { PageId = pageId }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<TwPageFileAttachmentInfo>> GetPageFilesInfoByPageNavigationAndPageRevisionPagedAsync(string pageNavigation, int pageNumber, int? pageSize = null, int? pageRevision = null, CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        var size = pageSize.GetValueOrDefault(25);
        if (size < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        var rows = await session.Connection.QueryAsync<TwPageFileAttachmentInfo>(new CommandDefinition("""
            SELECT PF.Id, PF.PageId, PF.Name, PFR.ContentType, PFR.Size, PF.CreatedDate,
                   PRA.PageRevision, PRA.FileRevision, PF.Navigation AS FileNavigation,
                   P.Navigation AS PageNavigation, @PageSize AS PaginationPageSize,
                   CEILING(COUNT(*) OVER () / @PageSize) AS PaginationPageCount
            FROM Page P JOIN PageRevision PR ON PR.PageId = P.Id AND PR.Revision = COALESCE(@PageRevision, P.Revision)
            JOIN PageRevisionAttachment PRA ON PRA.PageId = P.Id AND PRA.PageRevision = PR.Revision
            JOIN PageFile PF ON PF.Id = PRA.PageFileId
            JOIN PageFileRevision PFR ON PFR.PageFileId = PF.Id AND PFR.Revision = PRA.FileRevision
            WHERE P.Navigation = @PageNavigation AND P.IsDeleted = FALSE
            ORDER BY PF.Name, PF.Id LIMIT @PageSize OFFSET @Offset;
            """, new { PageNavigation = TwNamespaceNavigation.CleanAndValidate(pageNavigation), PageRevision = pageRevision, PageSize = size, Offset = checked((pageNumber - 1) * size) }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<TwPageFileAttachment?> GetPageFileAttachmentByPageNavigationFileRevisionAndFileNavigationAsync(string pageNavigation, string fileNavigation, int? fileRevision = null, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        return await session.Connection.QuerySingleOrDefaultAsync<TwPageFileAttachment>(new CommandDefinition("""
            SELECT PF.Id, PF.PageId, PF.Name, PFR.ContentType, PFR.Size, PF.CreatedDate, PFR.Data,
                   PF.Navigation AS FileNavigation, P.Navigation AS PageNavigation
            FROM Page P JOIN PageFile PF ON PF.PageId = P.Id
            JOIN PageFileRevision PFR ON PFR.PageFileId = PF.Id AND PFR.Revision = COALESCE(@FileRevision, PF.Revision)
            WHERE P.Navigation = @PageNavigation AND PF.Navigation = @FileNavigation AND P.IsDeleted = FALSE;
            """, new { PageNavigation = TwNamespaceNavigation.CleanAndValidate(pageNavigation), FileNavigation = TwNavigation.Clean(fileNavigation), FileRevision = fileRevision }, cancellationToken: cancellationToken));
    }

    public async Task<TwPageFileAttachment?> GetPageFileAttachmentByPageNavigationPageRevisionAndFileNavigationAsync(string pageNavigation, string fileNavigation, int? pageRevision = null, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        return await session.Connection.QuerySingleOrDefaultAsync<TwPageFileAttachment>(new CommandDefinition("""
            SELECT PF.Id, PF.PageId, PF.Name, PFR.ContentType, PFR.Size, PF.CreatedDate, PFR.Data,
                   PF.Navigation AS FileNavigation, P.Navigation AS PageNavigation
            FROM Page P JOIN PageRevision PR ON PR.PageId = P.Id AND PR.Revision = COALESCE(@PageRevision, P.Revision)
            JOIN PageRevisionAttachment PRA ON PRA.PageId = P.Id AND PRA.PageRevision = PR.Revision
            JOIN PageFile PF ON PF.Id = PRA.PageFileId AND PF.Navigation = @FileNavigation
            JOIN PageFileRevision PFR ON PFR.PageFileId = PF.Id AND PFR.Revision = PRA.FileRevision
            WHERE P.Navigation = @PageNavigation AND P.IsDeleted = FALSE;
            """, new { PageNavigation = TwNamespaceNavigation.CleanAndValidate(pageNavigation), FileNavigation = TwNavigation.Clean(fileNavigation), PageRevision = pageRevision }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<TwPageFileAttachmentInfo>> GetPageFileAttachmentRevisionsByPageAndFileNavigationPagedAsync(string pageNavigation, string fileNavigation, int pageNumber, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        var size = pageSize.GetValueOrDefault(25);
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        var rows = await session.Connection.QueryAsync<TwPageFileAttachmentInfo>(new CommandDefinition("""
            SELECT PF.Id, PF.PageId, PF.Name, PFR.ContentType, PFR.Size, PF.CreatedDate,
                   PFR.Revision AS FileRevision, PF.Navigation AS FileNavigation,
                   P.Navigation AS PageNavigation, CAST(PFR.CreatedByUserId AS CHAR(36)) AS CreatedByUserId,
                   @PageSize AS PaginationPageSize,
                   CEILING(COUNT(*) OVER () / @PageSize) AS PaginationPageCount
            FROM Page P JOIN PageFile PF ON PF.PageId = P.Id AND PF.Navigation = @FileNavigation
            JOIN PageFileRevision PFR ON PFR.PageFileId = PF.Id
            WHERE P.Navigation = @PageNavigation AND P.IsDeleted = FALSE
            ORDER BY PFR.Revision DESC LIMIT @PageSize OFFSET @Offset;
            """, new { PageNavigation = TwNamespaceNavigation.CleanAndValidate(pageNavigation), FileNavigation = TwNavigation.Clean(fileNavigation), PageSize = size, Offset = checked((pageNumber - 1) * size) }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<int> UpsertPageFileAsync(TwPageFileAttachment item, Guid userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.PageId <= 0) throw new ArgumentOutOfRangeException(nameof(item));
        var navigation = TwNavigation.Clean(item.FileNavigation);
        if (string.IsNullOrWhiteSpace(navigation)) throw new ArgumentException("A valid file navigation is required.", nameof(item));
        if (item.Data is null || item.Data.LongLength != item.Size) throw new ArgumentException("Attachment size does not match its content.", nameof(item));
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        await using var transaction = await session.BeginTransactionAsync(cancellationToken);
        try
        {
            var fileId = await session.Connection.ExecuteScalarAsync<int>(Command("""
                SELECT Id FROM PageFile WHERE PageId = @PageId AND Navigation = @Navigation LIMIT 1;
                """, new { item.PageId, Navigation = navigation }, transaction.Transaction, cancellationToken));
            if (fileId == 0)
            {
                fileId = await session.Connection.ExecuteScalarAsync<int>(Command("INSERT INTO PageFile (PageId, Name, Navigation, Revision, CreatedDate) VALUES (@PageId, @Name, @Navigation, 0, @CreatedDate); SELECT LAST_INSERT_ID();", new { item.PageId, item.Name, Navigation = navigation, CreatedDate = item.CreatedDate == default ? DateTime.UtcNow : item.CreatedDate }, transaction.Transaction, cancellationToken));
            }

            var current = await session.Connection.QuerySingleOrDefaultAsync<FileRevisionState>(Command("SELECT PFR.Revision, PFR.DataHash FROM PageFileRevision PFR WHERE PFR.PageFileId = @FileId ORDER BY PFR.Revision DESC LIMIT 1;", new { FileId = fileId }, transaction.Transaction, cancellationToken));
            var hash = SecurityUtility.Crc32(item.Data);
            var pageRevision = await session.Connection.ExecuteScalarAsync<int>(Command("SELECT Revision FROM Page WHERE Id = @PageId AND IsDeleted = FALSE;", new { item.PageId }, transaction.Transaction, cancellationToken));
            if (pageRevision == 0) throw new InvalidOperationException("The target page does not exist or is deleted.");
            var attached = await session.Connection.QuerySingleOrDefaultAsync<int?>(Command("SELECT FileRevision FROM PageRevisionAttachment WHERE PageId = @PageId AND PageFileId = @FileId AND PageRevision = @PageRevision;", new { item.PageId, FileId = fileId, PageRevision = pageRevision }, transaction.Transaction, cancellationToken));
            if (attached.HasValue && current is not null && current.DataHash == hash)
            {
                await transaction.CommitAsync(cancellationToken);
                return fileId;
            }

            var revision = (current?.Revision ?? -1) + 1;
            await session.Connection.ExecuteAsync(Command("INSERT INTO PageFileRevision (PageFileId, ContentType, Size, CreatedByUserId, CreatedDate, Data, Revision, DataHash) VALUES (@FileId, @ContentType, @Size, @UserId, @CreatedDate, @Data, @Revision, @DataHash); UPDATE PageFile SET Revision = @Revision, Name = @Name WHERE Id = @FileId; DELETE FROM PageRevisionAttachment WHERE PageId = @PageId AND PageFileId = @FileId AND PageRevision = @PageRevision; INSERT INTO PageRevisionAttachment (PageId, PageFileId, FileRevision, PageRevision) VALUES (@PageId, @FileId, @Revision, @PageRevision);", new { FileId = fileId, item.ContentType, Size = item.Data.LongLength, UserId = userId.ToString("D"), CreatedDate = item.CreatedDate == default ? DateTime.UtcNow : item.CreatedDate, Data = item.Data, Revision = revision, DataHash = hash, item.PageId, PageRevision = pageRevision, item.Name }, transaction.Transaction, cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return fileId;
        }
        catch { await transaction.RollbackAsync(cancellationToken); throw; }
    }

    public async Task DetachPageRevisionAttachmentAsync(string pageNavigation, string fileNavigation, int pageRevision, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        await session.Connection.ExecuteAsync(new CommandDefinition("DELETE PRA FROM PageRevisionAttachment PRA JOIN Page P ON P.Id = PRA.PageId JOIN PageFile PF ON PF.Id = PRA.PageFileId WHERE P.Navigation = @PageNavigation AND PF.Navigation = @FileNavigation AND PRA.PageRevision = @PageRevision;", new { PageNavigation = TwNamespaceNavigation.CleanAndValidate(pageNavigation), FileNavigation = TwNavigation.Clean(fileNavigation), PageRevision = pageRevision }, cancellationToken: cancellationToken));
    }

    public async Task DeletePageAttachmentRevisionAsync(int pageFileId, int revision, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        await using var transaction = await session.BeginTransactionAsync(cancellationToken);
        try
        {
            await session.Connection.ExecuteAsync(Command("DELETE FROM PageRevisionAttachment WHERE PageFileId = @PageFileId AND FileRevision = @Revision; DELETE FROM PageFileRevision WHERE PageFileId = @PageFileId AND Revision = @Revision; UPDATE PageFile SET Revision = (SELECT MAX(Revision) FROM PageFileRevision WHERE PageFileId = @PageFileId) WHERE Id = @PageFileId AND EXISTS (SELECT 1 FROM PageFileRevision WHERE PageFileId = @PageFileId); DELETE FROM PageFile WHERE Id = @PageFileId AND NOT EXISTS (SELECT 1 FROM PageFileRevision WHERE PageFileId = @PageFileId);", new { PageFileId = pageFileId, Revision = revision }, transaction.Transaction, cancellationToken));
            await transaction.CommitAsync(cancellationToken);
        }
        catch { await transaction.RollbackAsync(cancellationToken); throw; }
    }

    public async Task<IReadOnlyList<TwPageAttachmentSummary>> GetAllPageAttachmentsPagedAsync(int pageNumber, string? searchString = null, int status = 0, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1 || pageSize < 1) throw new ArgumentOutOfRangeException(pageNumber < 1 ? nameof(pageNumber) : nameof(pageSize));
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        var rows = await session.Connection.QueryAsync<TwPageAttachmentSummary>(new CommandDefinition("""
            WITH Attachments AS (
                SELECT PFR.PageFileId, PFR.Revision AS FileRevision, PFR.Size, PFR.ContentType, PFR.CreatedDate,
                       PF.Name AS FileName, PF.Navigation AS FileNavigation, P.Name AS PageName, P.`Namespace`, P.Navigation AS PageNavigation,
                       (SELECT COUNT(*) FROM PageRevisionAttachment PRA WHERE PRA.PageFileId = PFR.PageFileId AND PRA.FileRevision = PFR.Revision) AS PageRevisionCount,
                       (SELECT COUNT(*) FROM PageRevisionAttachment PRA WHERE PRA.PageFileId = PFR.PageFileId AND PRA.FileRevision = PFR.Revision AND PRA.PageRevision = P.Revision) AS CurrentPageRevisionCount
                FROM PageFileRevision PFR JOIN PageFile PF ON PF.Id = PFR.PageFileId JOIN Page P ON P.Id = PF.PageId
            )
            SELECT A.*, @PageSize AS PaginationPageSize, CEILING(COUNT(*) OVER () / @PageSize) AS PaginationPageCount
            FROM Attachments A
            WHERE (@Status = 0 OR (@Status = 1 AND A.PageRevisionCount = 0) OR (@Status = 2 AND A.PageRevisionCount > 0))
              AND (@Search = '' OR A.FileName LIKE CONCAT('%', @Search, '%') OR A.PageName LIKE CONCAT('%', @Search, '%') OR A.`Namespace` LIKE CONCAT('%', @Search, '%') OR A.ContentType LIKE CONCAT('%', @Search, '%'))
            ORDER BY A.Size DESC, A.PageFileId, A.FileRevision LIMIT @PageSize OFFSET @Offset;
            """, new { Search = searchString?.Trim() ?? string.Empty, Status = status, PageSize = pageSize, Offset = checked((pageNumber - 1) * pageSize) }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<TwPageAttachmentTotals> GetPageAttachmentTotalsAsync(CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        return await session.Connection.QuerySingleAsync<TwPageAttachmentTotals>(new CommandDefinition("SELECT COUNT(*) AS TotalCount, COALESCE(SUM(PFR.Size), 0) AS TotalSize, COALESCE(SUM(CASE WHEN PRA.PageFileId IS NULL THEN 1 ELSE 0 END), 0) AS OrphanedCount, COALESCE(SUM(CASE WHEN PRA.PageFileId IS NULL THEN PFR.Size ELSE 0 END), 0) AS OrphanedSize FROM PageFileRevision PFR JOIN PageFile PF ON PF.Id = PFR.PageFileId JOIN Page P ON P.Id = PF.PageId LEFT JOIN (SELECT DISTINCT PageFileId, FileRevision FROM PageRevisionAttachment) PRA ON PRA.PageFileId = PFR.PageFileId AND PRA.FileRevision = PFR.Revision;", cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<TwOrphanedPageAttachment>> GetOrphanedPageAttachmentsPagedAsync(int pageNumber, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1 || pageSize < 1) throw new ArgumentOutOfRangeException(pageNumber < 1 ? nameof(pageNumber) : nameof(pageSize));
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        var rows = await session.Connection.QueryAsync<TwOrphanedPageAttachment>(new CommandDefinition("""
            SELECT PFR.PageFileId, P.Name AS PageName, P.`Namespace`, P.Navigation AS PageNavigation,
                   PF.Name AS FileName, PF.Navigation AS FileNavigation, PFR.Size, PFR.Revision AS FileRevision,
                   @PageSize AS PaginationPageSize, CEILING(COUNT(*) OVER () / @PageSize) AS PaginationPageCount
            FROM PageFileRevision PFR JOIN PageFile PF ON PF.Id = PFR.PageFileId JOIN Page P ON P.Id = PF.PageId
            LEFT JOIN PageRevisionAttachment PRA ON PRA.PageFileId = PFR.PageFileId AND PRA.FileRevision = PFR.Revision
            WHERE PRA.PageFileId IS NULL ORDER BY P.Name, PF.Name, PFR.Revision
            LIMIT @PageSize OFFSET @Offset;
            """, new { PageSize = pageSize, Offset = checked((pageNumber - 1) * pageSize) }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task PurgeOrphanedPageAttachmentsAsync(CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        await using var transaction = await session.BeginTransactionAsync(cancellationToken);
        try
        {
            await session.Connection.ExecuteAsync(Command("DELETE PFR FROM PageFileRevision PFR LEFT JOIN PageRevisionAttachment PRA ON PRA.PageFileId = PFR.PageFileId AND PRA.FileRevision = PFR.Revision WHERE PRA.PageFileId IS NULL; DELETE PF FROM PageFile PF LEFT JOIN PageFileRevision PFR ON PFR.PageFileId = PF.Id WHERE PFR.PageFileId IS NULL;", new { }, transaction.Transaction, cancellationToken));
            await transaction.CommitAsync(cancellationToken);
        }
        catch { await transaction.RollbackAsync(cancellationToken); throw; }
    }

    public async Task PurgeOrphanedPageAttachmentAsync(int pageFileId, int revision, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        await using var transaction = await session.BeginTransactionAsync(cancellationToken);
        try
        {
            await session.Connection.ExecuteAsync(Command("DELETE FROM PageFileRevision WHERE PageFileId = @PageFileId AND Revision = @Revision AND NOT EXISTS (SELECT 1 FROM PageRevisionAttachment WHERE PageFileId = @PageFileId AND FileRevision = @Revision); DELETE FROM PageFile WHERE Id = @PageFileId AND NOT EXISTS (SELECT 1 FROM PageFileRevision WHERE PageFileId = @PageFileId);", new { PageFileId = pageFileId, Revision = revision }, transaction.Transaction, cancellationToken));
            await transaction.CommitAsync(cancellationToken);
        }
        catch { await transaction.RollbackAsync(cancellationToken); throw; }
    }

    private sealed class FileRevisionState
    {
        public int Revision { get; set; }
        public int DataHash { get; set; }
    }

    private static CommandDefinition Command(string sql, object parameters, System.Data.Common.DbTransaction? transaction = null, CancellationToken cancellationToken = default)
        => new(sql, parameters, transaction, cancellationToken: cancellationToken);
}
