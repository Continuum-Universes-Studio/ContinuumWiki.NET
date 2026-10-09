using Dapper;
using TightWiki.Plugin.Models;
using TightWiki.Repository.Helpers;

namespace TightWiki.Tests.Integration;

public sealed class MySqlPageSupportIntegrationTests
{
    [MySqlFact]
    public async Task CommentsFilesAndInstructionsUseThePageTransactionBoundary()
    {
        var connectionString = Environment.GetEnvironmentVariable("CONTINUUMWIKI_MYSQL_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        var factory = new MySqlDatabaseConnectionFactory(connectionString);
        await new MySqlSchemaInitializer().InitializeAsync(factory);
        var page = new TwPage { Name = $"Support::{Guid.NewGuid():N}", Body = "body", CreatedByUserId = Guid.NewGuid(), ModifiedByUserId = Guid.NewGuid() };
        await new MySqlPageRepository(factory).CreatePageAsync(page);
        var repository = new MySqlPageSupportRepository(factory);

        var userId = Guid.NewGuid();
        var commentId = await repository.InsertCommentAsync(page.Id, userId, "comment 世界");
        Assert.Single(await repository.GetCommentsAsync(page.Id));
        Assert.Equal(0, await repository.DeleteCommentAsync(page.Id, commentId, Guid.NewGuid()));
        Assert.Equal(1, await repository.DeleteCommentAsync(page.Id, commentId, userId));

        var fileId = await repository.UpsertFileAsync(page.Id, "世界.png", "world.png", "image/png", [1, 2, 3], userId, 42);
        await repository.ReplaceProcessingInstructionsAsync(page.Id, ["Protect", "NoCache"]);
        await repository.ReplaceSearchTokensAsync(page.Id, [new TwPageToken { Token = "世界", Weight = 1.5, DoubleMetaphone = "SJ" }]);
        await using var session = await factory.OpenSessionAsync();
        Assert.Equal(1, await session.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PageFileRevision WHERE PageFileId = @Id", new { Id = fileId }));
        Assert.Equal(2, await session.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PageProcessingInstruction WHERE PageId = @Id", new { Id = page.Id }));
        Assert.Equal(new[] { page.Id }, await repository.FindPageIdsByTokensAsync(["世界"]));
    }

    [MySqlFact]
    public async Task ProviderNeutralAttachmentsPreserveContentRevisionsAssociationsAndDeletion()
    {
        var connectionString = Environment.GetEnvironmentVariable("CONTINUUMWIKI_MYSQL_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        var factory = new MySqlDatabaseConnectionFactory(connectionString);
        await new MySqlSchemaInitializer().InitializeAsync(factory);
        var pages = new MySqlPageRepository(factory);
        var page = new TwPage { Name = $"Attachment::{Guid.NewGuid():N}", Body = "body", CreatedByUserId = Guid.NewGuid(), ModifiedByUserId = Guid.NewGuid() };
        await pages.CreatePageAsync(page);
        var repository = new MySqlPageSupportRepository(factory);
        var userId = Guid.NewGuid();
        var firstData = new byte[] { 1, 2, 3, 4 };
        var file = new TwPageFileAttachment { PageId = page.Id, Name = "世界.png", FileNavigation = "世界_png", ContentType = "image/png", Size = firstData.LongLength, Data = firstData, CreatedDate = DateTime.UtcNow };

        var fileId = await repository.UpsertPageFileAsync(file, userId);
        Assert.True(fileId > 0);
        var first = await repository.GetPageFileAttachmentByPageNavigationPageRevisionAndFileNavigationAsync(page.Navigation, file.FileNavigation);
        Assert.NotNull(first);
        Assert.Equal(firstData, first.Data);
        Assert.Equal(firstData.LongLength, first.Size);

        var secondData = new byte[] { 9, 8, 7, 6, 5 };
        file.Data = secondData;
        file.Size = secondData.LongLength;
        await repository.UpsertPageFileAsync(file, userId);
        var revisions = await repository.GetPageFileAttachmentRevisionsByPageAndFileNavigationPagedAsync(page.Navigation, file.FileNavigation, 1, 50);
        Assert.Equal(new[] { 1, 0 }, revisions.Select(value => value.FileRevision));
        var current = await repository.GetPageFileAttachmentByPageNavigationFileRevisionAndFileNavigationAsync(page.Navigation, file.FileNavigation);
        Assert.NotNull(current);
        Assert.Equal(secondData, current.Data);

        Assert.Equal(1, await repository.GetCountOfPageAttachmentsAsync(page.Id));
        await repository.DetachPageRevisionAttachmentAsync(page.Navigation, file.FileNavigation, page.Revision);
        Assert.Empty(await repository.GetPageFilesInfoByPageIdAsync(page.Id));
        await repository.DeletePageAttachmentRevisionAsync(fileId, 1);
        var revisionAfterDelete = await repository.GetPageFileAttachmentByPageNavigationFileRevisionAndFileNavigationAsync(page.Navigation, file.FileNavigation, 1);
        Assert.Null(revisionAfterDelete);

        var invalid = new TwPageFileAttachment { PageId = page.Id, Name = "invalid", FileNavigation = "invalid", ContentType = "application/octet-stream", Size = 10, Data = [1] };
        await Assert.ThrowsAsync<ArgumentException>(() => repository.UpsertPageFileAsync(invalid, userId));
        await using var session = await factory.OpenSessionAsync();
        Assert.Equal(0, await session.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PageFile WHERE PageId = @PageId AND Navigation = 'invalid'", new { PageId = page.Id }));
    }

    [MySqlFact]
    public async Task DerivedMetadataAndAttachmentMaintenanceReplaceAtomically()
    {
        var connectionString = Environment.GetEnvironmentVariable("CONTINUUMWIKI_MYSQL_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        var factory = new MySqlDatabaseConnectionFactory(connectionString);
        await new MySqlSchemaInitializer().InitializeAsync(factory);
        var pages = new MySqlPageRepository(factory);
        var page = new TwPage { Name = $"Maintenance::{Guid.NewGuid():N}", Body = "body", CreatedByUserId = Guid.NewGuid(), ModifiedByUserId = Guid.NewGuid() };
        await pages.CreatePageAsync(page);
        var repository = new MySqlPageSupportRepository(factory);
        await repository.ReplaceDerivedMetadataAsync(page.Id, ["lore", "世界"], [new TwPageReference(page.Name, "maintenance-target")], ["Protect"], [new TwPageToken { Token = "maintenance世界", Weight = 2, DoubleMetaphone = "SJ" }]);
        Assert.Equal(0, await repository.GetCountOfPageAttachmentsAsync(page.Id));

        await Assert.ThrowsAnyAsync<Exception>(() => repository.ReplaceDerivedMetadataAsync(999999999, ["should-rollback"], [], [], []));
        await using var session = await factory.OpenSessionAsync();
        Assert.Equal(1, await session.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PageTag WHERE PageId = @PageId AND Tag = 'lore'", new { PageId = page.Id }));
        Assert.Equal(1, await session.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PageProcessingInstruction WHERE PageId = @PageId AND Instruction = 'Protect'", new { PageId = page.Id }));

        var file = new TwPageFileAttachment { PageId = page.Id, Name = "orphan.txt", FileNavigation = "orphan_txt", ContentType = "text/plain", Size = 3, Data = [1, 2, 3], CreatedDate = DateTime.UtcNow };
        var fileId = await repository.UpsertPageFileAsync(file, Guid.NewGuid());
        var listed = await repository.GetAllPageAttachmentsPagedAsync(1, "orphan", 0, 50);
        Assert.Contains(listed, value => value.PageFileId == fileId);
        Assert.True((await repository.GetPageAttachmentTotalsAsync()).TotalCount >= 1);
        await repository.DetachPageRevisionAttachmentAsync(page.Navigation, file.FileNavigation, page.Revision);
        Assert.Contains(await repository.GetOrphanedPageAttachmentsPagedAsync(1), value => value.PageFileId == fileId);
        await repository.PurgeOrphanedPageAttachmentAsync(fileId, 0);
        Assert.DoesNotContain(await repository.GetOrphanedPageAttachmentsPagedAsync(1), value => value.PageFileId == fileId);
    }
}
