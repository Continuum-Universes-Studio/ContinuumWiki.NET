using Dapper;
using Microsoft.Extensions.Logging;
using TightWiki.Library.Database;
using TightWiki.Plugin.Models;
using TightWiki.Plugin.Interfaces.Persistence;

namespace TightWiki.Repository.Helpers;

/// <summary>
/// Provider-neutral MySQL operations repository. It intentionally does not implement
/// the legacy SQLite-shaped plugin interfaces until those public contracts are versioned.
/// </summary>
public sealed class MySqlOperationsRepository : ITwApplicationOperationsRepository
{
    private readonly IDatabaseConnectionFactory _connectionFactory;

    public MySqlOperationsRepository(IDatabaseConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task WriteLogAsync(LogLevel severity, string? text = null, string? exceptionText = null, string? stackTrace = null, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        await session.Connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO Log (SeverityId, Text, ExceptionText, StackTrace, CreatedDate)
            SELECT Id, @Text, @ExceptionText, @StackTrace, @CreatedDate FROM Severity WHERE Name = @Severity;
            """, new { Severity = severity.ToString(), Text = text, ExceptionText = exceptionText, StackTrace = stackTrace, CreatedDate = DateTime.UtcNow }, cancellationToken: cancellationToken));
    }

    public async Task<int> GetExceptionCountAsync(CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        return await session.Connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM Log WHERE SeverityId IN (SELECT Id FROM Severity WHERE Name IN ('Error', 'Critical'));", cancellationToken: cancellationToken));
    }

    public async Task PurgeLogsAsync(CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        await session.Connection.ExecuteAsync(new CommandDefinition("DELETE FROM Log;", cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<TwEventLogSeverity>> GetSeveritiesAsync(CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        var rows = await session.Connection.QueryAsync<TwEventLogSeverity>(new CommandDefinition("SELECT Id, Name FROM Severity ORDER BY Id;", cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<TwLogEntry>> GetLogEntriesPagedAsync(int pageNumber, string? orderBy = null, string? orderByDirection = null, string? severity = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        var size = pageSize.GetValueOrDefault(50);
        if (size < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));
        var orderColumn = orderBy?.ToLowerInvariant() switch { "createddate" => "L.CreatedDate", "severity" => "S.Name", _ => "L.Id" };
        var direction = string.Equals(orderByDirection, "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        var rows = await session.Connection.QueryAsync<TwLogEntry>(new CommandDefinition($"SELECT L.Id, S.Name AS Severity, L.Text, L.ExceptionText, L.StackTrace, L.CreatedDate, @PageSize AS PaginationPageSize, CEILING(COUNT(*) OVER () / @PageSize) AS PaginationPageCount FROM Log L JOIN Severity S ON S.Id = L.SeverityId WHERE (@Severity IS NULL OR S.Name = @Severity) ORDER BY {orderColumn} {direction} LIMIT @PageSize OFFSET @Offset;", new { PageSize = size, Offset = checked((pageNumber - 1) * size), Severity = severity }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<TwLogEntry> GetLogEntryByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        return await session.Connection.QuerySingleAsync<TwLogEntry>(new CommandDefinition("SELECT L.Id, S.Name AS Severity, L.Text, L.ExceptionText, L.StackTrace, L.CreatedDate FROM Log L JOIN Severity S ON S.Id = L.SeverityId WHERE L.Id = @Id;", new { Id = id }, cancellationToken: cancellationToken));
    }

    public async Task<int> IncrementPageViewCountAsync(int pageId, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        return await session.Connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO PageStatistics (PageId, LastCompileDateTime, TotalCompilationCount, TotalViewCount)
            SELECT Id, @Now, 1, 1 FROM Page WHERE Id = @PageId
            ON DUPLICATE KEY UPDATE TotalViewCount = TotalViewCount + 1;
            """, new { PageId = pageId, Now = DateTime.UtcNow }, cancellationToken: cancellationToken));
    }

    public async Task MergePageCompilationStatisticsAsync(int pageId, double wikifyTimeMs, int matchCount, int errorCount, int outgoingLinkCount, int tagCount, int processedBodySize, int bodySize, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        await session.Connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO PageStatistics (PageId, LastCompileDateTime, TotalCompilationCount, TotalViewCount, LastWikifyTimeMs, TotalWikifyTimeMs, LastMatchCount, LastErrorCount, LastOutgoingLinkCount, LastTagCount, LastProcessedBodySize, LastBodySize)
            SELECT Id, @Now, 1, 0, @Time, @Time, @Matches, @Errors, @Links, @Tags, @Processed, @Body FROM Page WHERE Id = @PageId
            ON DUPLICATE KEY UPDATE LastCompileDateTime = @Now, TotalCompilationCount = TotalCompilationCount + 1, LastWikifyTimeMs = @Time, TotalWikifyTimeMs = TotalWikifyTimeMs + @Time, LastMatchCount = @Matches, LastErrorCount = @Errors, LastOutgoingLinkCount = @Links, LastTagCount = @Tags, LastProcessedBodySize = @Processed, LastBodySize = @Body;
            """, new { PageId = pageId, Now = DateTime.UtcNow, Time = wikifyTimeMs, Matches = matchCount, Errors = errorCount, Links = outgoingLinkCount, Tags = tagCount, Processed = processedBodySize, Body = bodySize }, cancellationToken: cancellationToken));
    }

    public async Task<int> GetPageTotalViewCountAsync(int pageId, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        return await session.Connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COALESCE(TotalViewCount, 0) FROM PageStatistics WHERE PageId = @PageId;", new { PageId = pageId }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<TwPageStatistics>> GetPageStatisticsPagedAsync(int pageNumber, string? orderBy = null, string? orderByDirection = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        var size = pageSize.GetValueOrDefault(50);
        if (size < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));
        var orderColumn = orderBy?.ToLowerInvariant() switch { "navigation" => "P.Navigation", "totalviewcount" => "S.TotalViewCount", "lastcompiledatetime" => "S.LastCompileDateTime", _ => "P.Name" };
        var direction = string.Equals(orderByDirection, "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        var rows = await session.Connection.QueryAsync<TwPageStatistics>(new CommandDefinition($"SELECT P.Name AS PageName, P.`Namespace` AS `Namespace`, P.Navigation, P.Revision AS Revisions, S.PageId, S.TotalViewCount, S.LastCompileDateTime, S.TotalCompilationCount, S.LastWikifyTimeMs, S.TotalWikifyTimeMs, S.LastMatchCount, S.LastErrorCount, S.LastOutgoingLinkCount, S.LastTagCount, S.LastProcessedBodySize, S.LastBodySize, @PageSize AS PaginationPageSize, CEILING(COUNT(*) OVER () / @PageSize) AS PaginationPageCount FROM PageStatistics S JOIN Page P ON P.Id = S.PageId WHERE P.IsDeleted = FALSE ORDER BY {orderColumn} {direction} LIMIT @PageSize OFFSET @Offset;", new { PageSize = size, Offset = checked((pageNumber - 1) * size) }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task PurgePageStatisticsAsync(CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        await session.Connection.ExecuteAsync(new CommandDefinition("DELETE FROM PageStatistics;", cancellationToken: cancellationToken));
    }

    public async Task<int> UpsertEmojiAsync(TwUpsertEmoji emoji, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(emoji);
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        await using var transaction = await session.BeginTransactionAsync(cancellationToken);
        try
        {
            var id = await session.Connection.ExecuteScalarAsync<int>(new CommandDefinition("""
                INSERT INTO Emoji (Id, Name, Shortcut, ImageData, MimeType) VALUES (NULLIF(@Id, 0), @Name, @Shortcut, @ImageData, @MimeType)
                ON DUPLICATE KEY UPDATE Id = LAST_INSERT_ID(Id), Name = VALUES(Name), Shortcut = VALUES(Shortcut), ImageData = VALUES(ImageData), MimeType = VALUES(MimeType);
                SELECT LAST_INSERT_ID();
                """, new { Id = emoji.Id ?? 0, emoji.Name, Shortcut = $"%%{emoji.Name.ToLowerInvariant()}%%", emoji.ImageData, emoji.MimeType }, transaction.Transaction, cancellationToken: cancellationToken));
            await session.Connection.ExecuteAsync(new CommandDefinition("DELETE FROM EmojiCategory WHERE EmojiId = @Id;", new { Id = id }, transaction.Transaction, cancellationToken: cancellationToken));
            foreach (var category in emoji.Categories ?? [])
                await session.Connection.ExecuteAsync(new CommandDefinition("INSERT INTO EmojiCategory (EmojiId, Category) VALUES (@Id, @Category);", new { Id = id, Category = category }, transaction.Transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            emoji.Id = id;
            return id;
        }
        catch { await transaction.RollbackAsync(cancellationToken); throw; }
    }
}
