using TightWiki.Plugin.Interfaces.Persistence;
using TightWiki.Plugin.Interfaces.Repository;
using TightWiki.Plugin.Models;

namespace TightWiki.Repository.Helpers;

/// <summary>Provider-neutral attachment adapter over the unchanged SQLite page repository.</summary>
public sealed class SqliteApplicationPageSupportRepository(ITwPageRepository repository) : ITwApplicationPageSupportRepository
{
    public async Task<int> InsertCommentAsync(int pageId, Guid userId, string body, CancellationToken cancellationToken = default)
    {
        await WithCancellation(repository.InsertPageComment(pageId, userId, body), cancellationToken);
        return 1;
    }
    public async Task<int> DeleteCommentAsync(int pageId, int commentId, Guid? userId = null, CancellationToken cancellationToken = default)
    {
        if (userId.HasValue)
            await WithCancellation(repository.DeletePageCommentByUserAndId(pageId, userId.Value, commentId), cancellationToken);
        else
            await WithCancellation(repository.DeletePageCommentById(pageId, commentId), cancellationToken);
        return 1;
    }
    public async Task<IReadOnlyList<TwPageComment>> GetCommentsAsync(int pageId, int pageNumber = 1, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        var navigation = await WithCancellation(repository.GetPageNavigationByPageId(pageId), cancellationToken);
        return navigation is null ? [] : await Convert(repository.GetPageCommentsPaged(navigation, pageNumber), cancellationToken);
    }
    public Task ReplaceProcessingInstructionsAsync(int pageId, IEnumerable<string> instructions, CancellationToken cancellationToken = default) => Unsupported();
    public Task ReplaceSearchTokensAsync(int pageId, IEnumerable<TwPageToken> tokens, CancellationToken cancellationToken = default) => Unsupported();
    public Task<IReadOnlyList<int>> FindPageIdsByTokensAsync(IEnumerable<string> tokens, CancellationToken cancellationToken = default) => Unsupported<IReadOnlyList<int>>();
    public async Task ReplaceDerivedMetadataAsync(int pageId, IEnumerable<string> tags, IEnumerable<TwPageReference> references, IEnumerable<string> instructions, IEnumerable<TwPageToken> tokens, CancellationToken cancellationToken = default)
    {
        await WithCancellation(repository.UpdatePageTags(pageId, tags.ToList()), cancellationToken);
        await WithCancellation(repository.UpdatePageReferences(pageId, references.ToList()), cancellationToken);
        await WithCancellation(repository.UpdatePageProcessingInstructions(pageId, instructions.ToList()), cancellationToken);
        await WithCancellation(repository.SavePageSearchTokens(tokens.Select(value => new TwPageToken { PageId = pageId, Token = value.Token, DoubleMetaphone = value.DoubleMetaphone, Weight = value.Weight }).ToList()), cancellationToken);
    }

    public Task<int> GetCountOfPageAttachmentsAsync(int pageId, CancellationToken cancellationToken = default) => WithCancellation(repository.GetCountOfPageAttachmentsById(pageId), cancellationToken);
    public Task<IReadOnlyList<TwPageFileAttachmentInfo>> GetPageFilesInfoByPageIdAsync(int pageId, CancellationToken cancellationToken = default) => Convert(repository.GetPageFilesInfoByPageId(pageId), cancellationToken);
    public Task<IReadOnlyList<TwPageFileAttachmentInfo>> GetPageFilesInfoByPageNavigationAndPageRevisionPagedAsync(string pageNavigation, int pageNumber, int? pageSize = null, int? pageRevision = null, CancellationToken cancellationToken = default) => Convert(repository.GetPageFilesInfoByPageNavigationAndPageRevisionPaged(pageNavigation, pageNumber, pageSize, pageRevision), cancellationToken);
    public Task<TwPageFileAttachment?> GetPageFileAttachmentByPageNavigationFileRevisionAndFileNavigationAsync(string pageNavigation, string fileNavigation, int? fileRevision = null, CancellationToken cancellationToken = default) => WithCancellation(repository.GetPageFileAttachmentByPageNavigationFileRevisionAndFileNavigation(pageNavigation, fileNavigation, fileRevision), cancellationToken);
    public Task<TwPageFileAttachment?> GetPageFileAttachmentByPageNavigationPageRevisionAndFileNavigationAsync(string pageNavigation, string fileNavigation, int? pageRevision = null, CancellationToken cancellationToken = default) => WithCancellation(repository.GetPageFileAttachmentByPageNavigationPageRevisionAndFileNavigation(pageNavigation, fileNavigation, pageRevision), cancellationToken);
    public Task<IReadOnlyList<TwPageFileAttachmentInfo>> GetPageFileAttachmentRevisionsByPageAndFileNavigationPagedAsync(string pageNavigation, string fileNavigation, int pageNumber, int? pageSize = null, CancellationToken cancellationToken = default) => Convert(repository.GetPageFileAttachmentRevisionsByPageAndFileNavigationPaged(pageNavigation, fileNavigation, pageNumber), cancellationToken);

    public async Task<int> UpsertPageFileAsync(TwPageFileAttachment item, Guid userId, CancellationToken cancellationToken = default)
    {
        await WithCancellation(repository.UpsertPageFile(item, userId), cancellationToken);
        var files = await WithCancellation(repository.GetPageFilesInfoByPageId(item.PageId), cancellationToken);
        return files.FirstOrDefault(o => string.Equals(o.FileNavigation, item.FileNavigation, StringComparison.OrdinalIgnoreCase))?.Id ?? 0;
    }

    public Task DetachPageRevisionAttachmentAsync(string pageNavigation, string fileNavigation, int pageRevision, CancellationToken cancellationToken = default) => WithCancellation(repository.DetachPageRevisionAttachment(pageNavigation, fileNavigation, pageRevision), cancellationToken);
    public Task DeletePageAttachmentRevisionAsync(int pageFileId, int revision, CancellationToken cancellationToken = default) => WithCancellation(repository.DeletePageAttachmentRevision(pageFileId, revision), cancellationToken);
    public Task<IReadOnlyList<TwPageAttachmentSummary>> GetAllPageAttachmentsPagedAsync(int pageNumber, string? searchString = null, int status = 0, int pageSize = 50, CancellationToken cancellationToken = default) => Convert(repository.GetAllPageAttachmentsPaged(pageNumber, searchString: searchString, status: status), cancellationToken);
    public Task<TwPageAttachmentTotals> GetPageAttachmentTotalsAsync(CancellationToken cancellationToken = default) => WithCancellation(repository.GetPageAttachmentTotals(), cancellationToken);
    public Task<IReadOnlyList<TwOrphanedPageAttachment>> GetOrphanedPageAttachmentsPagedAsync(int pageNumber, int pageSize = 50, CancellationToken cancellationToken = default) => Convert(repository.GetOrphanedPageAttachmentsPaged(pageNumber), cancellationToken);
    public Task PurgeOrphanedPageAttachmentsAsync(CancellationToken cancellationToken = default) => WithCancellation(repository.PurgeOrphanedPageAttachments(), cancellationToken);
    public Task PurgeOrphanedPageAttachmentAsync(int pageFileId, int revision, CancellationToken cancellationToken = default) => WithCancellation(repository.PurgeOrphanedPageAttachment(pageFileId, revision), cancellationToken);

    private static async Task<T> WithCancellation<T>(Task<T> task, CancellationToken cancellationToken) => await task.WaitAsync(cancellationToken);
    private static async Task WithCancellation(Task task, CancellationToken cancellationToken) => await task.WaitAsync(cancellationToken);
    private static async Task<IReadOnlyList<T>> Convert<T>(Task<List<T>> task, CancellationToken cancellationToken) => await WithCancellation(task, cancellationToken);
    private static Task<T> Unsupported<T>() => Task.FromException<T>(new NotSupportedException("This provider-neutral operation is not available through the SQLite compatibility adapter."));
    private static Task Unsupported() => Task.FromException(new NotSupportedException("This provider-neutral operation is not available through the SQLite compatibility adapter."));
}
