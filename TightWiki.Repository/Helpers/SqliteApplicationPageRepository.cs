using TightWiki.Plugin.Interfaces.Persistence;
using TightWiki.Plugin.Interfaces.Repository;
using TightWiki.Plugin.Models;
using TightWiki.Library.Security;

namespace TightWiki.Repository.Helpers;

/// <summary>Provider-neutral page adapter over the unchanged legacy SQLite repository.</summary>
public sealed class SqliteApplicationPageRepository(ITwPageRepository repository) : ITwApplicationPageRepository
{
    public Task<List<TwPage>> AutoCompletePageAsync(string? searchText, CancellationToken cancellationToken = default) => WithCancellation(repository.AutoCompletePage(searchText), cancellationToken);
    public Task<List<string>> AutoCompleteNamespaceAsync(string? searchText, CancellationToken cancellationToken = default) => WithCancellation(repository.AutoCompleteNamespace(searchText), cancellationToken);
    public Task<List<TwPage>> SearchPagesAsync(IReadOnlyList<string> searchTerms, int pageNumber, int? pageSize = null, bool? allowFuzzyMatching = null, CancellationToken cancellationToken = default) => WithCancellation(repository.PageSearchPaged(searchTerms.ToList(), pageNumber, pageSize, allowFuzzyMatching), cancellationToken);
    public Task<List<TwPage>> GetPageInfoByTagAsync(string tag, CancellationToken cancellationToken = default) => WithCancellation(repository.GetPageInfoByTag(tag), cancellationToken);
    public Task<List<TwTagAssociation>> GetAssociatedTagsAsync(string tag, CancellationToken cancellationToken = default) => WithCancellation(repository.GetAssociatedTags(tag), cancellationToken);
    public Task<List<TwPageToken>> GetSearchTokensByPageIdAsync(int pageId, CancellationToken cancellationToken = default) => WithCancellation(repository.GetSearchTokensByPageId(pageId), cancellationToken);
    public async Task<IReadOnlyList<string>> GetTagsByPageIdAsync(int pageId, CancellationToken cancellationToken = default)
        => (await WithCancellation(repository.GetPageTagsById(pageId), cancellationToken)).Select(value => value.Tag).ToList();
    public Task<int> GetCurrentPageRevisionAsync(int pageId, CancellationToken cancellationToken = default) => WithCancellation(repository.GetCurrentPageRevision(pageId), cancellationToken);
    public Task<IReadOnlyList<TwPage>> GetTemplatePagesAsync(CancellationToken cancellationToken = default) => Convert(repository.GetAllTemplatePages(), cancellationToken);
    public Task<IReadOnlyList<TwFeatureTemplate>> GetFeatureTemplatesAsync(CancellationToken cancellationToken = default) => Convert(repository.GetAllFeatureTemplates(), cancellationToken);
    public Task<int> CreatePageAsync(TwPage page, IEnumerable<string>? tags = null, IEnumerable<TwPageReference>? references = null, IEnumerable<string>? instructions = null, IEnumerable<TwPageToken>? tokens = null, CancellationToken cancellationToken = default)
        => SaveAsync(page, null, tags, references, instructions, tokens, cancellationToken);
    public Task<int> UpdatePageAsync(TwPage page, int expectedRevision, IEnumerable<string>? tags = null, IEnumerable<TwPageReference>? references = null, IEnumerable<string>? instructions = null, IEnumerable<TwPageToken>? tokens = null, CancellationToken cancellationToken = default)
        => SaveAsync(page, expectedRevision, tags, references, instructions, tokens, cancellationToken);
    public Task<TwPage?> GetPageByIdAsync(int pageId, int? revision = null, bool includeDeleted = false, CancellationToken cancellationToken = default) => includeDeleted
        ? WithCancellation(repository.GetDeletedPageById(pageId), cancellationToken)
        : WithCancellation(repository.GetPageRevisionById(pageId, revision), cancellationToken);
    public Task<TwPage?> GetPageByNavigationAsync(string navigation, int? revision = null, bool includeDeleted = false, CancellationToken cancellationToken = default) => WithCancellation(repository.GetPageRevisionByNavigation(navigation, revision), cancellationToken);
    public Task<IReadOnlyList<TwPageRevision>> GetRevisionHistoryAsync(string navigation, int pageNumber = 1, int pageSize = 50, CancellationToken cancellationToken = default) => Convert(repository.GetPageRevisionsInfoByNavigationPaged(navigation, pageNumber, pageSize: pageSize), cancellationToken);
    public Task<IReadOnlyList<TwPage>> GetAllPagesAsync(CancellationToken cancellationToken = default) => Convert(repository.GetAllPages(), cancellationToken);
    public async Task<TwPage?> GetDeletedPageByIdAsync(int pageId, CancellationToken cancellationToken = default) => await WithCancellation(repository.GetDeletedPageById(pageId), cancellationToken);
    public Task<IReadOnlyList<TwDeletedPageRevision>> GetDeletedPageRevisionsAsync(int pageId, int pageNumber = 1, int pageSize = 50, CancellationToken cancellationToken = default) => Convert(repository.GetDeletedPageRevisionsByIdPaged(pageId, pageNumber), cancellationToken);
    public Task<TwDeletedPageRevision?> GetDeletedPageRevisionAsync(int pageId, int revision, CancellationToken cancellationToken = default) => WithCancellation(repository.GetDeletedPageRevisionById(pageId, revision), cancellationToken);
    public Task ArchivePageRevisionAsync(int pageId, int revision, Guid deletedByUserId, CancellationToken cancellationToken = default) => WithCancellation(repository.MovePageRevisionToDeletedById(pageId, revision, deletedByUserId), cancellationToken);
    public Task PurgeDeletedPageRevisionAsync(int pageId, int revision, CancellationToken cancellationToken = default) => WithCancellation(repository.PurgeDeletedPageRevisionByPageIdAndRevision(pageId, revision), cancellationToken);
    public Task PurgeDeletedPageRevisionsAsync(int pageId, CancellationToken cancellationToken = default) => WithCancellation(repository.PurgeDeletedPageRevisionsByPageId(pageId), cancellationToken);
    public Task RestoreDeletedPageRevisionAsync(int pageId, int revision, CancellationToken cancellationToken = default) => WithCancellation(repository.RestoreDeletedPageRevisionByPageIdAndRevision(pageId, revision), cancellationToken);
    public async Task<List<TwPage>> GetDeletedPagesAsync(int pageNumber, int? pageSize = null, IReadOnlyList<string>? searchTerms = null, CancellationToken cancellationToken = default)
    {
        // The legacy repository obtains the configured pagination size itself.
        return await WithCancellation(repository.GetAllDeletedPagesPaged(pageNumber, searchTerms: searchTerms?.ToList()), cancellationToken);
    }
    public async Task<bool> DeletePageAsync(int pageId, int expectedRevision, Guid deletedByUserId, CancellationToken cancellationToken = default)
    {
        var page = await WithCancellation(repository.GetPageRevisionById(pageId), cancellationToken);
        if (page is null || page.Revision != expectedRevision) return false;
        await WithCancellation(repository.MovePageToDeletedById(pageId, deletedByUserId), cancellationToken);
        return true;
    }
    public async Task<bool> RestorePageAsync(int pageId, CancellationToken cancellationToken = default)
    {
        var page = await WithCancellation(repository.GetDeletedPageById(pageId), cancellationToken);
        if (page is null) return false;
        await WithCancellation(repository.RestoreDeletedPageByPageId(pageId), cancellationToken);
        return true;
    }
    public Task ReplaceTagsAsync(int pageId, IEnumerable<string> tags, CancellationToken cancellationToken = default) => WithCancellation(repository.UpdatePageTags(pageId, tags.ToList()), cancellationToken);
    public Task ReplaceReferencesAsync(int pageId, IEnumerable<TwPageReference> references, CancellationToken cancellationToken = default) => WithCancellation(repository.UpdatePageReferences(pageId, references.ToList()), cancellationToken);
    public Task UpsertCurrentPageEditorAsync(int pageId, Guid userId, string accountName, CancellationToken cancellationToken = default) => WithCancellation(repository.UpsertCurrentPageEditor(pageId, userId, accountName), cancellationToken);
    public Task DeleteCurrentPageEditorAsync(int pageId, Guid userId, CancellationToken cancellationToken = default) => WithCancellation(repository.DeleteCurrentPageEditor(pageId, userId), cancellationToken);
    public Task<IReadOnlyList<string>> GetCurrentPageEditorsAsync(int pageId, int windowMinutes = 5, CancellationToken cancellationToken = default) => Convert(repository.GetCurrentPageEditors(pageId, windowMinutes), cancellationToken);
    public Task<TwProcessingInstructionCollection> GetProcessingInstructionsAsync(int pageId, CancellationToken cancellationToken = default) => WithCancellation(repository.GetPageProcessingInstructionsByPageId(pageId), cancellationToken);

    private async Task<int> SaveAsync(TwPage page, int? expectedRevision, IEnumerable<string>? tags, IEnumerable<TwPageReference>? references, IEnumerable<string>? instructions, IEnumerable<TwPageToken>? tokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        var current = page.Id == 0 ? null : await WithCancellation(repository.GetPageRevisionById(page.Id), cancellationToken);
        if (page.Id != 0 && (current is null || (expectedRevision.HasValue && current.Revision != expectedRevision.Value)))
            throw new InvalidOperationException($"Page {page.Id} changed since revision {expectedRevision}; reload it before saving.");

        var newDataHash = SecurityUtility.Crc32(page.Body ?? string.Empty);
        var changed = page.Id == 0 || current!.Name != page.Name || current.Namespace != page.Namespace || current.Description != page.Description || current.ChangeSummary != page.ChangeSummary || current.DataHash != newDataHash;
        if (!changed) return page.Id;

        await WithCancellation(repository.PagesFactory.EphemeralAsync(async instance =>
        {
            var transaction = instance.BeginTransaction();
            try
            {
                var now = DateTime.UtcNow;
                var revision = page.Id == 0 ? 1 : current!.Revision + 1;
                if (page.Id == 0)
                {
                    page.Id = await instance.ExecuteScalarAsync<int>("CreatePage.sql", new
                    {
                        page.Name, page.Navigation, Namespace = page.Namespace, page.Description,
                        page.CreatedByUserId, CreatedDate = page.CreatedDate == default ? now : page.CreatedDate,
                        page.ModifiedByUserId, ModifiedDate = now
                    });
                }
                else
                {
                    await instance.ExecuteAsync("UpdatePage.sql", new
                    {
                        PageId = page.Id, page.Name, page.Navigation, Namespace = page.Namespace, page.Description,
                        page.ModifiedByUserId, ModifiedDate = now
                    });
                }

                await instance.ExecuteAsync("UpdatePageRevisionNumber.sql", new { PageId = page.Id, PageRevision = revision });
                await instance.ExecuteAsync("InsertPageRevision.sql", new
                {
                    PageId = page.Id, page.Name, Namespace = page.Namespace, page.Description, page.Body,
                    DataHash = newDataHash, PageRevision = revision, ChangeSummary = page.ChangeSummary ?? string.Empty,
                    page.ModifiedByUserId, ModifiedDate = now
                });

                if (page.Id != 0 && revision > 1)
                    await instance.ExecuteAsync("ReassociateAllPageAttachments.sql", new { PageId = page.Id, PageRevision = revision });

                if (tags is not null && references is not null && instructions is not null && tokens is not null)
                {
                    using var tagTable = instance.CreateTempTableFrom("TempTags", tags.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => new { Tag = value, Navigation = TightWiki.Plugin.TwNavigation.Clean(value) }).DistinctBy(value => value.Navigation).ToList());
                    using var referenceTable = instance.CreateTempTableFrom("TempReferences", references.ToList());
                    using var instructionTable = instance.CreateTempTableFrom("TempInstructions", instructions.ToList());
                    using var tokenTable = instance.CreateTempTableFrom("TempTokens", tokens.ToList());
                    await instance.ExecuteAsync("UpdatePageTags.sql", new { PageId = page.Id });
                    await instance.ExecuteAsync("UpdatePageReferences.sql", new { PageId = page.Id });
                    await instance.ExecuteAsync("UpdatePageProcessingInstructions.sql", new { PageId = page.Id });
                    await instance.ExecuteAsync("SavePageSearchTokens.sql");
                }

                transaction.Commit();
                page.Revision = revision;
                page.MostCurrentRevision = revision;
                return page.Id;
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }), cancellationToken);
        return page.Id;
    }

    private static async Task<T> WithCancellation<T>(Task<T> task, CancellationToken cancellationToken) => await task.WaitAsync(cancellationToken);
    private static async Task WithCancellation(Task task, CancellationToken cancellationToken) => await task.WaitAsync(cancellationToken);
    private static async Task<IReadOnlyList<T>> Convert<T>(Task<List<T>> task, CancellationToken cancellationToken) => await WithCancellation(task, cancellationToken);
    private static Task<T> Unsupported<T>() => Task.FromException<T>(new NotSupportedException("This provider-neutral operation is not available through the SQLite compatibility adapter yet."));
}
