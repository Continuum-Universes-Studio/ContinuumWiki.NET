using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using TightWiki.Plugin.Interfaces;
using TightWiki.Plugin.Models;
using TightWiki.Plugin.Models.Defaults;
using ModelPermission = TightWiki.Plugin.Models.TwPermission;
using ModelPermissionDisposition = TightWiki.Plugin.Models.TwPermissionDisposition;

namespace TightWiki.Plugin.Interfaces.Persistence;

#pragma warning disable CS1591

/// <summary>
/// Provider-neutral application contracts. These contracts intentionally expose
/// domain operations only; connection factories and provider-specific connection
/// types remain implementation details.
/// </summary>
public interface ITwApplicationConfigurationRepository
{
    /// <summary>Gets configuration entries for a group.</summary>
    Task<IReadOnlyList<TwConfigurationEntry>> GetEntriesByGroupNameAsync(string groupName, CancellationToken cancellationToken = default);
    /// <summary>Ensures a configuration group exists.</summary>
    Task EnsureGroupAsync(string groupName, string? description = null, CancellationToken cancellationToken = default);
    /// <summary>Saves a configuration value.</summary>
    Task SaveEntryAsync(string groupName, string entryName, string value, CancellationToken cancellationToken = default);
    /// <summary>Gets available themes.</summary>
    Task<IReadOnlyList<TwTheme>> GetThemesAsync(CancellationToken cancellationToken = default);
    /// <summary>Gets configured menu items.</summary>
    Task<IReadOnlyList<TwMenuItem>> GetMenuItemsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TwConfigurationFlat>> GetFlatConfigurationAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TwConfigurationNest>> GetConfigurationNestAsync(CancellationToken cancellationToken = default);
    Task<TwMenuItem?> GetMenuItemByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<int> InsertMenuItemAsync(TwMenuItem menuItem, CancellationToken cancellationToken = default);
    Task<int> UpdateMenuItemAsync(TwMenuItem menuItem, CancellationToken cancellationToken = default);
    Task DeleteMenuItemAsync(int id, CancellationToken cancellationToken = default);
    Task<string?> GetEntryValueAsync(string groupName, string entryName, CancellationToken cancellationToken = default);
    Task<TwWikiDatabaseStatistics?> GetDatabaseMetricsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Provider-neutral page, revision, navigation, tag, reference, and deletion operations.</summary>
public interface ITwApplicationPageRepository
{
    /// <summary>Returns active page autocomplete matches, honoring cancellation.</summary>
    Task<List<TwPage>> AutoCompletePageAsync(string? searchText, CancellationToken cancellationToken = default);
    /// <summary>Returns active namespace autocomplete matches, honoring cancellation.</summary>
    Task<List<string>> AutoCompleteNamespaceAsync(string? searchText, CancellationToken cancellationToken = default);
    /// <summary>Searches active pages using the provider's documented token matching semantics.</summary>
    Task<List<TwPage>> SearchPagesAsync(IReadOnlyList<string> searchTerms, int pageNumber, int? pageSize = null, bool? allowFuzzyMatching = null, CancellationToken cancellationToken = default);
    /// <summary>Returns active pages associated with a tag.</summary>
    Task<List<TwPage>> GetPageInfoByTagAsync(string tag, CancellationToken cancellationToken = default);
    /// <summary>Returns tags associated with a tag and their page counts.</summary>
    Task<List<TwTagAssociation>> GetAssociatedTagsAsync(string tag, CancellationToken cancellationToken = default);
    /// <summary>Returns persisted search tokens for a page.</summary>
    Task<List<TwPageToken>> GetSearchTokensByPageIdAsync(int pageId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetTagsByPageIdAsync(int pageId, CancellationToken cancellationToken = default);
    Task<int> GetCurrentPageRevisionAsync(int pageId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TwPage>> GetTemplatePagesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TwFeatureTemplate>> GetFeatureTemplatesAsync(CancellationToken cancellationToken = default);
    /// <summary>Creates a page and its initial revision atomically.</summary>
    Task<int> CreatePageAsync(TwPage page, IEnumerable<string>? tags = null, IEnumerable<TwPageReference>? references = null, IEnumerable<string>? instructions = null, IEnumerable<TwPageToken>? tokens = null, CancellationToken cancellationToken = default);
    /// <summary>Updates a page only when the supplied revision is still current.</summary>
    Task<int> UpdatePageAsync(TwPage page, int expectedRevision, IEnumerable<string>? tags = null, IEnumerable<TwPageReference>? references = null, IEnumerable<string>? instructions = null, IEnumerable<TwPageToken>? tokens = null, CancellationToken cancellationToken = default);
    /// <summary>Gets a page revision; deleted rows are included only when requested.</summary>
    Task<TwPage?> GetPageByIdAsync(int pageId, int? revision = null, bool includeDeleted = false, CancellationToken cancellationToken = default);
    /// <summary>Gets a page revision by navigation; deleted rows are included only when requested.</summary>
    Task<TwPage?> GetPageByNavigationAsync(string navigation, int? revision = null, bool includeDeleted = false, CancellationToken cancellationToken = default);
    /// <summary>Returns immutable revision metadata in descending revision order.</summary>
    Task<IReadOnlyList<TwPageRevision>> GetRevisionHistoryAsync(string navigation, int pageNumber = 1, int pageSize = 50, CancellationToken cancellationToken = default);
    /// <summary>Gets all active pages with their current revision content.</summary>
    Task<IReadOnlyList<TwPage>> GetAllPagesAsync(CancellationToken cancellationToken = default);
    /// <summary>Gets the current deleted snapshot for a page, or null when it is not deleted.</summary>
    Task<TwPage?> GetDeletedPageByIdAsync(int pageId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TwDeletedPageRevision>> GetDeletedPageRevisionsAsync(int pageId, int pageNumber = 1, int pageSize = 50, CancellationToken cancellationToken = default);
    Task<TwDeletedPageRevision?> GetDeletedPageRevisionAsync(int pageId, int revision, CancellationToken cancellationToken = default);
    Task ArchivePageRevisionAsync(int pageId, int revision, Guid deletedByUserId, CancellationToken cancellationToken = default);
    Task PurgeDeletedPageRevisionAsync(int pageId, int revision, CancellationToken cancellationToken = default);
    Task PurgeDeletedPageRevisionsAsync(int pageId, CancellationToken cancellationToken = default);
    Task RestoreDeletedPageRevisionAsync(int pageId, int revision, CancellationToken cancellationToken = default);
    /// <summary>Returns a paged, optionally filtered list of deleted page snapshots.</summary>
    Task<List<TwPage>> GetDeletedPagesAsync(int pageNumber, int? pageSize = null, IReadOnlyList<string>? searchTerms = null, CancellationToken cancellationToken = default);
    /// <summary>Moves a page to the deletion state when its expected revision matches.</summary>
    Task<bool> DeletePageAsync(int pageId, int expectedRevision, Guid deletedByUserId, CancellationToken cancellationToken = default);
    /// <summary>Restores a deleted page atomically; a navigation uniqueness conflict fails the transaction.</summary>
    Task<bool> RestorePageAsync(int pageId, CancellationToken cancellationToken = default);
    /// <summary>Replaces all tags for a page atomically.</summary>
    Task ReplaceTagsAsync(int pageId, IEnumerable<string> tags, CancellationToken cancellationToken = default);
    /// <summary>Replaces all outbound references for a page atomically.</summary>
    Task ReplaceReferencesAsync(int pageId, IEnumerable<TwPageReference> references, CancellationToken cancellationToken = default);
    Task UpsertCurrentPageEditorAsync(int pageId, Guid userId, string accountName, CancellationToken cancellationToken = default);
    Task DeleteCurrentPageEditorAsync(int pageId, Guid userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetCurrentPageEditorsAsync(int pageId, int windowMinutes = 5, CancellationToken cancellationToken = default);
    Task<TwProcessingInstructionCollection> GetProcessingInstructionsAsync(int pageId, CancellationToken cancellationToken = default);
}

/// <summary>Provider-neutral page comments, processing instructions, and search-token operations.</summary>
public interface ITwApplicationPageSupportRepository
{
    /// <summary>Inserts a page comment.</summary>
    Task<int> InsertCommentAsync(int pageId, Guid userId, string body, CancellationToken cancellationToken = default);
    /// <summary>Deletes a page comment subject to the provider's ownership rules.</summary>
    Task<int> DeleteCommentAsync(int pageId, int commentId, Guid? userId = null, CancellationToken cancellationToken = default);
    /// <summary>Gets a page of comments.</summary>
    Task<IReadOnlyList<TwPageComment>> GetCommentsAsync(int pageId, int pageNumber = 1, int pageSize = 50, CancellationToken cancellationToken = default);
    /// <summary>Replaces processing instructions for a page.</summary>
    Task ReplaceProcessingInstructionsAsync(int pageId, IEnumerable<string> instructions, CancellationToken cancellationToken = default);
    /// <summary>Replaces persisted search tokens for a page.</summary>
    Task ReplaceSearchTokensAsync(int pageId, IEnumerable<TwPageToken> tokens, CancellationToken cancellationToken = default);
    /// <summary>Finds page IDs matching all supplied tokens.</summary>
    Task<IReadOnlyList<int>> FindPageIdsByTokensAsync(IEnumerable<string> tokens, CancellationToken cancellationToken = default);
    /// <summary>Replaces all parser-derived page metadata in one provider transaction where supported.</summary>
    Task ReplaceDerivedMetadataAsync(int pageId, IEnumerable<string> tags, IEnumerable<TwPageReference> references, IEnumerable<string> instructions, IEnumerable<TwPageToken> tokens, CancellationToken cancellationToken = default);
    /// <summary>Gets the number of attachments associated with the current page revision.</summary>
    Task<int> GetCountOfPageAttachmentsAsync(int pageId, CancellationToken cancellationToken = default);
    /// <summary>Gets attachment metadata associated with a page's current revision.</summary>
    Task<IReadOnlyList<TwPageFileAttachmentInfo>> GetPageFilesInfoByPageIdAsync(int pageId, CancellationToken cancellationToken = default);
    /// <summary>Gets attachment metadata associated with a selected page revision.</summary>
    Task<IReadOnlyList<TwPageFileAttachmentInfo>> GetPageFilesInfoByPageNavigationAndPageRevisionPagedAsync(string pageNavigation, int pageNumber, int? pageSize = null, int? pageRevision = null, CancellationToken cancellationToken = default);
    /// <summary>Gets a binary attachment revision by file navigation.</summary>
    Task<TwPageFileAttachment?> GetPageFileAttachmentByPageNavigationFileRevisionAndFileNavigationAsync(string pageNavigation, string fileNavigation, int? fileRevision = null, CancellationToken cancellationToken = default);
    /// <summary>Gets the attachment revision associated with a selected page revision.</summary>
    Task<TwPageFileAttachment?> GetPageFileAttachmentByPageNavigationPageRevisionAndFileNavigationAsync(string pageNavigation, string fileNavigation, int? pageRevision = null, CancellationToken cancellationToken = default);
    /// <summary>Gets all persisted revisions for an attachment.</summary>
    Task<IReadOnlyList<TwPageFileAttachmentInfo>> GetPageFileAttachmentRevisionsByPageAndFileNavigationPagedAsync(string pageNavigation, string fileNavigation, int pageNumber, int? pageSize = null, CancellationToken cancellationToken = default);
    /// <summary>Creates or updates an attachment and atomically associates its current revision with the page revision.</summary>
    Task<int> UpsertPageFileAsync(TwPageFileAttachment item, Guid userId, CancellationToken cancellationToken = default);
    /// <summary>Detaches an attachment from one page revision.</summary>
    Task DetachPageRevisionAttachmentAsync(string pageNavigation, string fileNavigation, int pageRevision, CancellationToken cancellationToken = default);
    /// <summary>Deletes an attachment revision and removes the attachment record when no revisions remain.</summary>
    Task DeletePageAttachmentRevisionAsync(int pageFileId, int revision, CancellationToken cancellationToken = default);
    /// <summary>Gets paged attachment administration records with usage filtering.</summary>
    Task<IReadOnlyList<TwPageAttachmentSummary>> GetAllPageAttachmentsPagedAsync(int pageNumber, string? searchString = null, int status = 0, int pageSize = 50, CancellationToken cancellationToken = default);
    /// <summary>Gets aggregate attachment and orphan counts.</summary>
    Task<TwPageAttachmentTotals> GetPageAttachmentTotalsAsync(CancellationToken cancellationToken = default);
    /// <summary>Gets paged attachment revisions that have no page-revision association.</summary>
    Task<IReadOnlyList<TwOrphanedPageAttachment>> GetOrphanedPageAttachmentsPagedAsync(int pageNumber, int pageSize = 50, CancellationToken cancellationToken = default);
    /// <summary>Purges all orphaned attachment revisions and empty file records.</summary>
    Task PurgeOrphanedPageAttachmentsAsync(CancellationToken cancellationToken = default);
    /// <summary>Purges one orphaned attachment revision and its empty file record.</summary>
    Task PurgeOrphanedPageAttachmentAsync(int pageFileId, int revision, CancellationToken cancellationToken = default);
}

/// <summary>Provider-neutral profile, role, permission, and membership operations.</summary>
public interface ITwApplicationIdentityRepository
{
    /// <summary>Creates a profile for an account.</summary>
    Task CreateProfileAsync(Guid userId, string accountName, CancellationToken cancellationToken = default);
    /// <summary>Gets a profile by account identifier.</summary>
    Task<TwAccountProfile?> GetProfileByUserIdAsync(Guid userId, CancellationToken cancellationToken = default);
    /// <summary>Updates an account profile.</summary>
    Task UpdateProfileAsync(TwAccountProfile profile, CancellationToken cancellationToken = default);
    /// <summary>Updates an account avatar and content type.</summary>
    Task UpdateProfileAvatarAsync(Guid userId, byte[] imageData, string contentType, CancellationToken cancellationToken = default);
    /// <summary>Ensures a role exists and returns its identifier.</summary>
    Task<int> EnsureRoleAsync(string name, string? description = null, bool isBuiltIn = false, CancellationToken cancellationToken = default);
    /// <summary>Ensures a permission exists and returns its identifier.</summary>
    Task<int> EnsurePermissionAsync(string name, string? description = null, CancellationToken cancellationToken = default);
    /// <summary>Gets the disposition identifier for a permission.</summary>
    Task<int> GetPermissionDispositionIdAsync(string name, CancellationToken cancellationToken = default);
    /// <summary>Adds a user to a role.</summary>
    Task AddRoleMembershipAsync(Guid userId, int roleId, CancellationToken cancellationToken = default);
    /// <summary>Adds an account-specific permission rule.</summary>
    Task<int> AddAccountPermissionAsync(Guid userId, int permissionId, string disposition, string? ns = null, string? pageId = null, CancellationToken cancellationToken = default);
    /// <summary>Adds a role-specific permission rule.</summary>
    Task<int> AddRolePermissionAsync(int roleId, int permissionId, string disposition, string? ns = null, string? pageId = null, CancellationToken cancellationToken = default);
    /// <summary>Gets the effective permission rules for a user.</summary>
    Task<IReadOnlyList<TwApparentPermission>> GetEffectivePermissionsAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TwApparentPermission>> GetEffectiveRolePermissionsAsync(string roleName, CancellationToken cancellationToken = default);
    Task<TwAccountProfile?> GetBasicProfileByUserIdAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<TwAccountProfile?> GetProfileByNavigationAsync(string? navigation, CancellationToken cancellationToken = default);
    Task<TwProfileAvatar?> GetProfileAvatarByNavigationAsync(string navigation, CancellationToken cancellationToken = default);
    Task<bool> ProfileAccountExistsAsync(string navigation, CancellationToken cancellationToken = default);
    Task<bool> EmailAddressExistsAsync(string? emailAddress, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TwRole>> GetRolesAsync(CancellationToken cancellationToken = default);
    Task<TwRole?> GetRoleByNameAsync(string name, CancellationToken cancellationToken = default);
    Task<bool> RoleExistsAsync(string name, CancellationToken cancellationToken = default);
    Task<bool> IsUserAdministratorAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<TwAdminPasswordChangeState> GetAdminPasswordStatusAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ModelPermission>> GetPermissionsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ModelPermissionDisposition>> GetPermissionDispositionsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TwAccountProfile>> GetUsersPagedAsync(int pageNumber, int pageSize, string? searchToken = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TwAccountProfile>> GetRoleMembersPagedAsync(int roleId, int pageNumber, int pageSize, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TwAccountPermission>> GetAccountPermissionsPagedAsync(Guid userId, int pageNumber, int pageSize, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TwAccountRoleMembership>> GetAccountRoleMembershipPagedAsync(Guid userId, int pageNumber, int pageSize, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TwRolePermission>> GetRolePermissionsPagedAsync(int roleId, int pageNumber, int pageSize, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TwRole>> AutoCompleteRolesAsync(string? searchText, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TwAccountProfile>> AutoCompleteAccountsAsync(string? searchText, CancellationToken cancellationToken = default);
    Task<TwAddRoleMemberResult?> AddRoleMemberAsync(Guid userId, int roleId, CancellationToken cancellationToken = default);
    Task<TwAddAccountMembershipResult?> AddAccountMembershipAsync(Guid userId, int roleId, CancellationToken cancellationToken = default);
    Task<TwAddRoleMemberResult?> AddRoleMemberByNameAsync(Guid userId, string roleName, CancellationToken cancellationToken = default);
    Task RemoveRoleMemberAsync(int roleId, Guid userId, CancellationToken cancellationToken = default);
    Task RemoveRolePermissionAsync(int id, CancellationToken cancellationToken = default);
    Task RemoveAccountPermissionAsync(int id, CancellationToken cancellationToken = default);
    Task<TwInsertRolePermissionResult?> InsertRolePermissionAsync(int roleId, int permissionId, string disposition, string? ns, string? pageId, CancellationToken cancellationToken = default);
    Task<TwInsertAccountPermissionResult?> InsertAccountPermissionAsync(Guid userId, int permissionId, string disposition, string? ns, string? pageId, CancellationToken cancellationToken = default);
    Task<bool> IsAccountMemberOfRoleAsync(Guid userId, int roleId, CancellationToken cancellationToken = default);
    Task<bool> IsAccountPermissionDefinedAsync(Guid userId, int permissionId, string disposition, string? ns, string? pageId, CancellationToken cancellationToken = default);
    Task<bool> IsRolePermissionDefinedAsync(int roleId, int permissionId, string disposition, string? ns, string? pageId, CancellationToken cancellationToken = default);
    Task AnonymizeProfileAsync(Guid userId, CancellationToken cancellationToken = default);
    Task DeleteRoleAsync(int roleId, CancellationToken cancellationToken = default);
    Task<bool> InsertRoleAsync(string name, string? description, CancellationToken cancellationToken = default);
    Task SetAdminPasswordChangedAsync(CancellationToken cancellationToken = default);
}

/// <summary>Provider-neutral logging, statistics, and emoji operations.</summary>
public interface ITwApplicationOperationsRepository
{
    /// <summary>Writes an operational log entry.</summary>
    Task WriteLogAsync(LogLevel severity, string? text = null, string? exceptionText = null, string? stackTrace = null, CancellationToken cancellationToken = default);
    /// <summary>Gets the exception count.</summary>
    Task<int> GetExceptionCountAsync(CancellationToken cancellationToken = default);
    /// <summary>Permanently removes eligible log entries.</summary>
    Task PurgeLogsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TwEventLogSeverity>> GetSeveritiesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TwLogEntry>> GetLogEntriesPagedAsync(int pageNumber, string? orderBy = null, string? orderByDirection = null, string? severity = null, int? pageSize = null, CancellationToken cancellationToken = default);
    Task<TwLogEntry> GetLogEntryByIdAsync(int id, CancellationToken cancellationToken = default);
    /// <summary>Increments page view statistics.</summary>
    Task<int> IncrementPageViewCountAsync(int pageId, CancellationToken cancellationToken = default);
    /// <summary>Merges page compilation statistics.</summary>
    Task MergePageCompilationStatisticsAsync(int pageId, double wikifyTimeMs, int matchCount, int errorCount, int outgoingLinkCount, int tagCount, int processedBodySize, int bodySize, CancellationToken cancellationToken = default);
    /// <summary>Gets the total view count for a page.</summary>
    Task<int> GetPageTotalViewCountAsync(int pageId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TwPageStatistics>> GetPageStatisticsPagedAsync(int pageNumber, string? orderBy = null, string? orderByDirection = null, int? pageSize = null, CancellationToken cancellationToken = default);
    Task PurgePageStatisticsAsync(CancellationToken cancellationToken = default);
    /// <summary>Creates or updates an emoji record.</summary>
    Task<int> UpsertEmojiAsync(TwUpsertEmoji emoji, CancellationToken cancellationToken = default);
}

/// <summary>Provider-neutral access to versioned default content.</summary>
public interface ITwApplicationDefaultsRepository
{
    /// <summary>Gets default configuration records.</summary>
    Task<IReadOnlyList<TwDefaultConfiguration>> GetConfigurationsAsync(CancellationToken cancellationToken = default);
    /// <summary>Gets default themes.</summary>
    Task<IReadOnlyList<TwDefaultTheme>> GetThemesAsync(CancellationToken cancellationToken = default);
    /// <summary>Gets default feature templates.</summary>
    Task<IReadOnlyList<TwDefaultFeatureTemplate>> GetFeatureTemplatesAsync(CancellationToken cancellationToken = default);
    /// <summary>Gets default wiki pages.</summary>
    Task<IReadOnlyList<TwDefaultWikiPage>> GetWikiPagesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Provider-aware administrative database maintenance and status operations.</summary>
public interface ITwApplicationMaintenanceRepository
{
    Task<IReadOnlyList<TwDatabaseInfo>> GetDatabaseInfoAsync(CancellationToken cancellationToken = default);
    Task<TwDatabaseMaintenanceStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<TwMaintenanceResult> ExecuteAsync(TwMaintenanceAction action, string databaseName, CancellationToken cancellationToken = default);
    string BackupPath { get; }
    Task<TwDatabaseBackup> CreateBackupAsync(CancellationToken cancellationToken = default);
    IReadOnlyList<TwDatabaseBackup> GetBackups();
    void DeleteBackup(string fileName);
    Task<TwMaintenanceResult> ApplySeedDataAsync(ITwSharedLocalizationText localizer, UserManager<IdentityUser> userManager, ITwEngine tightEngine, TwDefaultDataType[] defaultDataTypes, CancellationToken cancellationToken = default);
}

/// <summary>Provider-neutral read access to localized application text.</summary>
public interface ITwApplicationTranslationRepository
{
    /// <summary>Gets a localized translation or null when no value exists.</summary>
    Task<string?> GetTranslationAsync(string language, string english, CancellationToken cancellationToken = default);
}
