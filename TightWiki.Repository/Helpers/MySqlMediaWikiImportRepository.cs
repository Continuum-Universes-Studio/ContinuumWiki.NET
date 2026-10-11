using Dapper;
using System.Security.Cryptography;
using System.Text;
using TightWiki.Library.Database;
using TightWiki.Plugin.Interfaces.Persistence;
using TightWiki.Plugin.Models;

namespace TightWiki.Repository.Helpers;

/// <summary>Reads only the immutable MediaWiki import provenance used by rendering.</summary>
public sealed class MySqlMediaWikiImportRepository(IDatabaseConnectionFactory connectionFactory) : ITwMediaWikiImportRepository
{
    public async Task<TwMediaWikiRenderSource> GetRenderSourceAsync(int targetPageId, int targetRevision, CancellationToken cancellationToken = default)
    {
        await using var session = await connectionFactory.OpenSessionAsync(cancellationToken);
        var row = await session.Connection.QuerySingleOrDefaultAsync<RenderSourceRow>(new CommandDefinition("""
            SELECT R.ImportBatchId, R.SourcePageId, R.SourceRevisionId, P.OriginalTitle AS SourceTitle,
                   P.SourceNamespaceId, P.SourceNamespaceName, R.SourceTimestamp, P.IsRedirect,
                   P.RedirectTargetSourceTitle, P.RedirectTargetNavigation, B.SourceWikiIdentity,
                   R.ContentModel, R.TargetPageId, R.TargetRevision
            FROM MediaWikiImportRevision R
            INNER JOIN MediaWikiImportPage P
                ON P.ImportBatchId = R.ImportBatchId AND P.SourcePageId = R.SourcePageId
            INNER JOIN MediaWikiImportBatch B ON B.ImportBatchId = R.ImportBatchId
            WHERE R.TargetPageId = @TargetPageId
              AND R.TargetRevision = @TargetRevision
              AND B.Status = 'Completed'
            ORDER BY R.ImportBatchId DESC
            LIMIT 1;
            """, new { TargetPageId = targetPageId, TargetRevision = targetRevision }, cancellationToken: cancellationToken));

        return row is null
            ? TwMediaWikiRenderSource.Native
            : new(TwPageSourceFormat.ImportedMediaWiki, row.ImportBatchId, row.SourcePageId,
                row.SourceRevisionId, row.SourceTitle, row.IsRedirect,
                row.RedirectTargetSourceTitle, row.RedirectTargetNavigation,
                row.SourceNamespaceId, row.SourceTimestamp,
                row.SourceWikiIdentity, row.ContentModel, row.TargetPageId, row.TargetRevision,
                row.SourceNamespaceName);
    }

    public async Task<TwMediaWikiPageTarget?> ResolvePageAsync(long importBatchId, string sourceTitle, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeTitle(sourceTitle);
        if (string.IsNullOrWhiteSpace(normalized)) return null;

        await using var session = await connectionFactory.OpenSessionAsync(cancellationToken);
        var row = await session.Connection.QuerySingleOrDefaultAsync<PageTargetRow>(new CommandDefinition("""
            SELECT OriginalTitle AS SourceTitle, TargetNavigation, TargetPageId
            FROM MediaWikiImportPage
            WHERE ImportBatchId = @ImportBatchId
              AND REPLACE(OriginalTitle, '_', ' ') = @SourceTitle
            LIMIT 1;
            """, new { ImportBatchId = importBatchId, SourceTitle = normalized }, cancellationToken: cancellationToken));
        return row is null ? null : new TwMediaWikiPageTarget(row.SourceTitle, row.TargetNavigation, checked((int)row.TargetPageId));
    }

    public async Task<TwMediaWikiMediaTarget?> ResolveMediaAsync(long importBatchId, string sourcePageId, string sourceRevisionId, string sourceFileName, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeTitle(sourceFileName);
        if (string.IsNullOrWhiteSpace(normalized) || HasUnsafeMediaPath(normalized)) return null;

        await using var session = await connectionFactory.OpenSessionAsync(cancellationToken);
        var row = await session.Connection.QuerySingleOrDefaultAsync<MediaTargetRow>(new CommandDefinition("""
            SELECT M.SourceFileName, P.Navigation AS PageNavigation, PF.Navigation AS FileNavigation,
                   COALESCE(PFR.ContentType, M.OriginalMimeType, '') AS ContentType,
                   PFR.Revision AS FileRevision,
                   CASE WHEN MR.TargetPageFileId IS NULL THEN FALSE ELSE TRUE END AS Resolved
            FROM MediaWikiImportMedia M
            INNER JOIN Page P ON P.Id = M.TargetPageId
            INNER JOIN PageFile PF ON PF.Id = M.TargetPageFileId
            LEFT JOIN MediaWikiImportMediaReference MR
                ON MR.ImportBatchId = M.ImportBatchId
               AND MR.SourcePageId = @SourcePageId
               AND MR.SourceRevisionId = @SourceRevisionId
               AND MR.SourceFilePageId = M.SourceFilePageId
            LEFT JOIN PageRevisionAttachment PRA
                ON PRA.PageId = MR.TargetPageId
               AND PRA.PageFileId = MR.TargetPageFileId
               AND PRA.PageRevision = MR.TargetPageRevision
            LEFT JOIN PageFileRevision PFR
                ON PFR.PageFileId = COALESCE(MR.TargetPageFileId, M.TargetPageFileId)
               AND PFR.Revision = COALESCE(PRA.FileRevision, PF.Revision)
            WHERE M.ImportBatchId = @ImportBatchId
              AND REPLACE(M.SourceFileName, '_', ' ') = @SourceTitle
            ORDER BY PRA.FileRevision DESC
            LIMIT 1;
            """, new { ImportBatchId = importBatchId, SourcePageId = sourcePageId, SourceRevisionId = sourceRevisionId, SourceTitle = normalized }, cancellationToken: cancellationToken));
        return row is null ? null : new TwMediaWikiMediaTarget(row.SourceFileName, row.PageNavigation, row.FileNavigation, row.ContentType, row.FileRevision is null ? null : checked((int)row.FileRevision.Value), row.Resolved != 0);
    }

    public async Task<TwMediaWikiTemplateTarget> ResolveTemplateAsync(long importBatchId, string sourceTitle, DateTime? asOfUtc = null, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeTitle(sourceTitle);
        if (string.IsNullOrWhiteSpace(normalized))
            return new(TwMediaWikiTemplateResolutionStatus.Missing, sourceTitle);

        await using var session = await connectionFactory.OpenSessionAsync(cancellationToken);
        var rows = (await session.Connection.QueryAsync<TemplateTargetRow>(new CommandDefinition("""
            SELECT P.OriginalTitle AS SourceTitle, P.SourcePageId, R.SourceRevisionId,
                   R.TargetRevision, PR.Body, R.SourceTimestamp
            FROM MediaWikiImportPage P
            INNER JOIN MediaWikiImportRevision R
                ON R.ImportBatchId = P.ImportBatchId AND R.SourcePageId = P.SourcePageId
            INNER JOIN Page TP ON TP.Id = R.TargetPageId AND TP.IsDeleted = FALSE
            INNER JOIN PageRevision PR ON PR.PageId = R.TargetPageId AND PR.Revision = R.TargetRevision
            INNER JOIN MediaWikiImportBatch B ON B.ImportBatchId = P.ImportBatchId
            WHERE P.ImportBatchId = @ImportBatchId
              AND P.SourceNamespaceId = '10'
              AND REPLACE(P.OriginalTitle, '_', ' ') = @SourceTitle
              AND P.IsRedirect = FALSE
              AND R.IsDeleted = FALSE
              AND B.Status = 'Completed'
              AND (@AsOfUtc IS NULL OR R.SourceTimestamp IS NULL OR R.SourceTimestamp <= @AsOfUtc)
            ORDER BY R.SourceTimestamp DESC, R.TargetRevision DESC;
            """, new { ImportBatchId = importBatchId, SourceTitle = normalized, AsOfUtc = asOfUtc }, cancellationToken: cancellationToken))).AsList();

        var byPage = rows.GroupBy(row => row.SourcePageId, StringComparer.Ordinal).ToArray();
        if (byPage.Length > 1)
            return new(TwMediaWikiTemplateResolutionStatus.Ambiguous, normalized);
        var row = rows.FirstOrDefault();
        if (row is not null)
            return new(TwMediaWikiTemplateResolutionStatus.Resolved, row.SourceTitle, row.SourcePageId,
                row.SourceRevisionId, checked((int)row.TargetRevision), row.Body, row.SourceTimestamp);

        var exists = await session.Connection.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*)
            FROM MediaWikiImportPage P
            INNER JOIN MediaWikiImportBatch B ON B.ImportBatchId = P.ImportBatchId
            WHERE P.ImportBatchId = @ImportBatchId
              AND P.SourceNamespaceId = '10'
              AND REPLACE(P.OriginalTitle, '_', ' ') = @SourceTitle
              AND B.Status = 'Completed';
            """, new { ImportBatchId = importBatchId, SourceTitle = normalized }, cancellationToken: cancellationToken));
        return exists > 0
            ? new(TwMediaWikiTemplateResolutionStatus.Restricted, normalized)
            : new(TwMediaWikiTemplateResolutionStatus.Missing, normalized);
    }

    public async Task<string> GetTemplateCacheVersionAsync(long importBatchId, DateTime? asOfUtc = null, CancellationToken cancellationToken = default)
    {
        await using var session = await connectionFactory.OpenSessionAsync(cancellationToken);
        var rows = (await session.Connection.QueryAsync<TemplateVersionRow>(new CommandDefinition("""
            SELECT P.SourcePageId, P.OriginalTitle AS SourceTitle, R.SourceRevisionId, R.TargetRevision, R.WikitextSha256, R.SourceTimestamp
            FROM MediaWikiImportPage P
            INNER JOIN MediaWikiImportRevision R
                ON R.ImportBatchId = P.ImportBatchId AND R.SourcePageId = P.SourcePageId
            INNER JOIN Page TP ON TP.Id = R.TargetPageId AND TP.IsDeleted = FALSE
            INNER JOIN MediaWikiImportBatch B ON B.ImportBatchId = P.ImportBatchId
            WHERE P.ImportBatchId = @ImportBatchId
              AND P.SourceNamespaceId = '10'
              AND P.IsRedirect = FALSE
              AND R.IsDeleted = FALSE
              AND B.Status = 'Completed'
              AND (@AsOfUtc IS NULL OR R.SourceTimestamp IS NULL OR R.SourceTimestamp <= @AsOfUtc)
            ORDER BY P.SourcePageId, R.SourceTimestamp DESC, R.TargetRevision DESC;
            """, new { ImportBatchId = importBatchId, AsOfUtc = asOfUtc }, cancellationToken: cancellationToken))).AsList();

        var selected = rows.GroupBy(row => row.SourcePageId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(row => row.SourcePageId, StringComparer.Ordinal)
            .Select(row => $"{row.SourcePageId}:{row.SourceTitle}:{row.SourceRevisionId}:{row.TargetRevision}:{row.WikitextSha256}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', selected))));
    }

    public async Task<TwMediaWikiModuleTarget> ResolveModuleAsync(long importBatchId, string sourceTitle, DateTime? asOfUtc = null, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeModuleTitle(sourceTitle);
        if (string.IsNullOrWhiteSpace(normalized)) return new(TwMediaWikiModuleResolutionStatus.Missing, sourceTitle);
        await using var session = await connectionFactory.OpenSessionAsync(cancellationToken);
        var rows = (await session.Connection.QueryAsync<ModuleTargetRow>(new CommandDefinition("""
            SELECT P.OriginalTitle AS SourceTitle, P.SourcePageId, R.SourceRevisionId,
                   R.TargetRevision, PR.Body, R.SourceTimestamp, R.ContentModel
            FROM MediaWikiImportPage P
            INNER JOIN MediaWikiImportRevision R ON R.ImportBatchId = P.ImportBatchId AND R.SourcePageId = P.SourcePageId
            INNER JOIN Page TP ON TP.Id = R.TargetPageId AND TP.IsDeleted = FALSE
            INNER JOIN PageRevision PR ON PR.PageId = R.TargetPageId AND PR.Revision = R.TargetRevision
            INNER JOIN MediaWikiImportBatch B ON B.ImportBatchId = P.ImportBatchId
            WHERE P.ImportBatchId = @ImportBatchId AND P.SourceNamespaceId = '828'
              AND TRIM(SUBSTRING_INDEX(REPLACE(P.OriginalTitle, '_', ' '), ':', -1)) = @SourceTitle AND P.IsRedirect = FALSE
              AND R.IsDeleted = FALSE AND B.Status = 'Completed'
              AND (@AsOfUtc IS NULL OR R.SourceTimestamp IS NULL OR R.SourceTimestamp <= @AsOfUtc)
            ORDER BY R.SourceTimestamp DESC, R.TargetRevision DESC;
            """, new { ImportBatchId = importBatchId, SourceTitle = normalized, AsOfUtc = asOfUtc }, cancellationToken: cancellationToken))).AsList();
        if (rows.GroupBy(row => row.SourcePageId, StringComparer.Ordinal).Count() > 1) return new(TwMediaWikiModuleResolutionStatus.Ambiguous, normalized);
        var row = rows.FirstOrDefault();
        if (row is not null) return new(TwMediaWikiModuleResolutionStatus.Resolved, row.SourceTitle, row.SourcePageId, row.SourceRevisionId, checked((int)row.TargetRevision), row.Body, row.SourceTimestamp, row.ContentModel);
        var exists = await session.Connection.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM MediaWikiImportPage P INNER JOIN MediaWikiImportBatch B ON B.ImportBatchId = P.ImportBatchId
            WHERE P.ImportBatchId = @ImportBatchId AND P.SourceNamespaceId = '828'
              AND TRIM(SUBSTRING_INDEX(REPLACE(P.OriginalTitle, '_', ' '), ':', -1)) = @SourceTitle AND B.Status = 'Completed';
            """, new { ImportBatchId = importBatchId, SourceTitle = normalized }, cancellationToken: cancellationToken));
        return exists > 0 ? new(TwMediaWikiModuleResolutionStatus.Restricted, normalized) : new(TwMediaWikiModuleResolutionStatus.Missing, normalized);
    }

    public async Task<string> GetModuleCacheVersionAsync(long importBatchId, DateTime? asOfUtc = null, CancellationToken cancellationToken = default)
    {
        await using var session = await connectionFactory.OpenSessionAsync(cancellationToken);
        var rows = (await session.Connection.QueryAsync<TemplateVersionRow>(new CommandDefinition("""
            SELECT P.SourcePageId, P.OriginalTitle AS SourceTitle, R.SourceRevisionId, R.TargetRevision, R.WikitextSha256, R.SourceTimestamp
            FROM MediaWikiImportPage P INNER JOIN MediaWikiImportRevision R ON R.ImportBatchId = P.ImportBatchId AND R.SourcePageId = P.SourcePageId
            INNER JOIN Page TP ON TP.Id = R.TargetPageId AND TP.IsDeleted = FALSE
            INNER JOIN MediaWikiImportBatch B ON B.ImportBatchId = P.ImportBatchId
            WHERE P.ImportBatchId = @ImportBatchId AND P.SourceNamespaceId = '828' AND P.IsRedirect = FALSE
              AND R.IsDeleted = FALSE AND B.Status = 'Completed'
              AND (@AsOfUtc IS NULL OR R.SourceTimestamp IS NULL OR R.SourceTimestamp <= @AsOfUtc)
            ORDER BY P.SourcePageId, R.SourceTimestamp DESC, R.TargetRevision DESC;
            """, new { ImportBatchId = importBatchId, AsOfUtc = asOfUtc }, cancellationToken: cancellationToken))).AsList();
        var selected = rows.GroupBy(row => row.SourcePageId, StringComparer.Ordinal).Select(group => group.First()).OrderBy(row => row.SourcePageId, StringComparer.Ordinal)
            .Select(row => $"{row.SourcePageId}:{row.SourceTitle}:{row.SourceRevisionId}:{row.TargetRevision}:{row.WikitextSha256}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', selected))));
    }

    private static string NormalizeTitle(string value)
    {
        try { value = Uri.UnescapeDataString(value); } catch (UriFormatException) { }
        return value.Trim().Replace('_', ' ');
    }

    private static string NormalizeModuleTitle(string value)
    {
        var normalized = NormalizeTitle(value);
        var separator = normalized.IndexOf(':');
        return separator >= 0 && string.Equals(normalized[..separator], "Module", StringComparison.OrdinalIgnoreCase)
            ? normalized[(separator + 1)..].Trim()
            : normalized;
    }

    private static bool HasUnsafeMediaPath(string value)
        => value.Contains('\0') || value.Contains('\\') || value.Split('/').Any(part => part is "." or "..") || value.Contains("../", StringComparison.Ordinal);

    private sealed class RenderSourceRow
    {
        public long ImportBatchId { get; set; }
        public string SourcePageId { get; set; } = string.Empty;
        public string SourceRevisionId { get; set; } = string.Empty;
        public string SourceTitle { get; set; } = string.Empty;
        public string SourceNamespaceId { get; set; } = string.Empty;
        public string? SourceNamespaceName { get; set; }
        public DateTime? SourceTimestamp { get; set; }
        public bool IsRedirect { get; set; }
        public string? RedirectTargetSourceTitle { get; set; }
        public string? RedirectTargetNavigation { get; set; }
        public string? SourceWikiIdentity { get; set; }
        public string? ContentModel { get; set; }
        public int? TargetPageId { get; set; }
        public int? TargetRevision { get; set; }
    }

    private sealed class PageTargetRow
    {
        public string SourceTitle { get; set; } = string.Empty;
        public string TargetNavigation { get; set; } = string.Empty;
        public uint TargetPageId { get; set; }
    }

    private sealed class MediaTargetRow
    {
        public string SourceFileName { get; set; } = string.Empty;
        public string PageNavigation { get; set; } = string.Empty;
        public string FileNavigation { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;
        public uint? FileRevision { get; set; }
        public int Resolved { get; set; }
    }

    private sealed class TemplateTargetRow
    {
        public string SourceTitle { get; set; } = string.Empty;
        public string SourcePageId { get; set; } = string.Empty;
        public string SourceRevisionId { get; set; } = string.Empty;
        public uint TargetRevision { get; set; }
        public string Body { get; set; } = string.Empty;
        public DateTime? SourceTimestamp { get; set; }
    }

    private sealed class TemplateVersionRow
    {
        public string SourcePageId { get; set; } = string.Empty;
        public string SourceTitle { get; set; } = string.Empty;
        public string SourceRevisionId { get; set; } = string.Empty;
        public uint TargetRevision { get; set; }
        public string WikitextSha256 { get; set; } = string.Empty;
        public DateTime? SourceTimestamp { get; set; }
    }

    private sealed class ModuleTargetRow
    {
        public string SourceTitle { get; set; } = string.Empty;
        public string SourcePageId { get; set; } = string.Empty;
        public string SourceRevisionId { get; set; } = string.Empty;
        public uint TargetRevision { get; set; }
        public string Body { get; set; } = string.Empty;
        public DateTime? SourceTimestamp { get; set; }
        public string? ContentModel { get; set; }
    }
}
