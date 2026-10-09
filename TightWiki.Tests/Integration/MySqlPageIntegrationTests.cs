using Dapper;
using TightWiki.Plugin.Models;
using TightWiki.Repository.Helpers;

namespace TightWiki.Tests.Integration;

public sealed class MySqlPageIntegrationTests
{
    [MySqlFact]
    public async Task PageLifecyclePreservesUnicodeHistoryNavigationAndDeletionState()
    {
        var connectionString = RequireConnectionString();
        var factory = new MySqlDatabaseConnectionFactory(connectionString);
        await new MySqlPageSchemaInitializer().InitializeAsync(factory);
        var repository = new MySqlPageRepository(factory);
        var suffix = Guid.NewGuid().ToString("N");
        var page = new TwPage
        {
            Name = $"Tests::{suffix} 世界",
            Description = "初版 description",
            Body = "SELECT '; -- page content 世界 🌌",
            CreatedByUserId = Guid.NewGuid(),
            ModifiedByUserId = Guid.NewGuid(),
            ChangeSummary = "initial"
        };

        var initialToken = new TwPageToken { Token = $"initial-{suffix}", DoubleMetaphone = "INIT", Weight = 1.5 };
        var pageId = await repository.CreatePageAsync(page,
            tags: [$"initial-tag-{suffix}"],
            references: [new TwPageReference("Missing reference", $"Missing-{suffix}")],
            instructions: ["NoCache"],
            tokens: [initialToken]);
        var first = await repository.GetPageByNavigationAsync(page.Navigation);
        Assert.NotNull(first);
        Assert.Equal(pageId, first.Id);
        Assert.Equal(page.Body, first.Body);
        Assert.Equal(page.Navigation, first.Navigation);
        await using (var metadataSession = await factory.OpenSessionAsync())
        {
            Assert.Equal(1, await metadataSession.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PageTag WHERE PageId = @PageId", new { PageId = pageId }));
            Assert.Equal(1, await metadataSession.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PageReference WHERE PageId = @PageId", new { PageId = pageId }));
            Assert.Equal(1, await metadataSession.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PageProcessingInstruction WHERE PageId = @PageId", new { PageId = pageId }));
            Assert.Equal(1, await metadataSession.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PageToken WHERE PageId = @PageId", new { PageId = pageId }));
        }

        page.Name = $"Tests::{suffix} Renamed 世界";
        page.Description = "第二版";
        page.Body = "edited body with apostrophe ' and SQL-looking text; 世界";
        page.ChangeSummary = "rename";
        var revision = await repository.UpdatePageAsync(page, 1,
            tags: [$"updated-tag-{suffix}"],
            references: [new TwPageReference("Updated reference", $"Updated-{suffix}")],
            instructions: ["Protect"],
            tokens: [new TwPageToken { Token = $"updated-{suffix}", DoubleMetaphone = "UPD", Weight = 2.5 }]);
        Assert.Equal(2, revision);
        await using (var updatedMetadataSession = await factory.OpenSessionAsync())
        {
            Assert.Equal(1, await updatedMetadataSession.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PageTag WHERE PageId = @PageId AND Tag = @Tag", new { PageId = pageId, Tag = $"updated-tag-{suffix}" }));
            Assert.Equal(0, await updatedMetadataSession.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PageTag WHERE PageId = @PageId AND Tag = @Tag", new { PageId = pageId, Tag = $"initial-tag-{suffix}" }));
            Assert.Equal(1, await updatedMetadataSession.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PageToken WHERE PageId = @PageId AND Token = @Token", new { PageId = pageId, Token = $"updated-{suffix}" }));
        }

        var history = await repository.GetRevisionHistoryAsync(page.Navigation);
        Assert.Equal(new[] { 2, 1 }, history.Select(o => o.Revision));
        var oldRevision = await repository.GetPageByIdAsync(pageId, 1);
        Assert.NotNull(oldRevision);
        Assert.Equal("SELECT '; -- page content 世界 🌌", oldRevision.Body);
        Assert.NotEqual(oldRevision.Navigation, page.Navigation);

        Assert.False(await repository.DeletePageAsync(pageId, 1, page.ModifiedByUserId));
        Assert.True(await repository.DeletePageAsync(pageId, 2, page.ModifiedByUserId));
        Assert.Null(await repository.GetPageByNavigationAsync(page.Navigation));
        var deleted = await repository.GetPageByIdAsync(pageId, includeDeleted: true);
        Assert.NotNull(deleted);
        var deletedById = await repository.GetDeletedPageByIdAsync(pageId);
        Assert.NotNull(deletedById);
        Assert.Equal(pageId, deletedById.Id);
        Assert.Contains(await repository.GetDeletedPagesAsync(1, 50), value => value.Id == pageId);
        Assert.True(await repository.RestorePageAsync(pageId));
        Assert.NotNull(await repository.GetPageByNavigationAsync(page.Navigation));
        Assert.Equal(new[] { 2, 1 }, (await repository.GetRevisionHistoryAsync(page.Navigation)).Select(value => value.Revision));
    }

    [MySqlFact]
    public async Task NavigationUniquenessConcurrencyAndRollbackAreEnforced()
    {
        var connectionString = RequireConnectionString();
        var factory = new MySqlDatabaseConnectionFactory(connectionString);
        await new MySqlPageSchemaInitializer().InitializeAsync(factory);
        var repository = new MySqlPageRepository(factory);
        var navigation = $"Tests::unique-{Guid.NewGuid():N}";
        var first = NewPage(navigation);
        await repository.CreatePageAsync(first);

        var duplicate = NewPage(navigation.ToUpperInvariant());
        await Assert.ThrowsAnyAsync<Exception>(() => repository.CreatePageAsync(duplicate));
        Assert.NotNull(await repository.GetPageByNavigationAsync(navigation));

        var stale = NewPage(first.Name);
        stale.Id = first.Id;
        stale.Revision = 1;
        stale.Body = "competing edit";
        await repository.UpdatePageAsync(stale, 1);
        await Assert.ThrowsAsync<MySqlPageConcurrencyException>(() => repository.UpdatePageAsync(first, 1));

        await repository.ReplaceTagsAsync(first.Id, ["世界", "tag"]);
        await repository.ReplaceReferencesAsync(first.Id, [new TwPageReference("self", navigation)], CancellationToken.None);
        await Assert.ThrowsAnyAsync<Exception>(() => repository.ReplaceReferencesAsync(999999999, [new TwPageReference("invalid", "missing")], CancellationToken.None));
        await using var session = await factory.OpenSessionAsync();
        var tagCount = await session.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PageTag WHERE PageId = @PageId", new { PageId = first.Id });
        Assert.Equal(2, tagCount);
        var referenceCount = await session.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PageReference WHERE PageId = @PageId", new { PageId = first.Id });
        Assert.Equal(1, referenceCount);
    }

    [MySqlFact]
    public async Task RestorationRejectsNavigationConflictWithoutOverwritingLivePage()
    {
        var connectionString = RequireConnectionString();
        var factory = new MySqlDatabaseConnectionFactory(connectionString);
        await new MySqlPageSchemaInitializer().InitializeAsync(factory);
        var repository = new MySqlPageRepository(factory);
        var navigation = $"Tests::restore-conflict-{Guid.NewGuid():N}";
        var deleted = NewPage(navigation);
        await repository.CreatePageAsync(deleted);
        Assert.True(await repository.DeletePageAsync(deleted.Id, deleted.Revision, Guid.NewGuid()));

        var live = NewPage(navigation.ToUpperInvariant());
        await repository.CreatePageAsync(live);
        await Assert.ThrowsAnyAsync<Exception>(() => repository.RestorePageAsync(deleted.Id));

        var stillLive = await repository.GetPageByNavigationAsync(live.Navigation);
        Assert.NotNull(stillLive);
        Assert.Equal(live.Id, stillLive.Id);
        Assert.NotNull(await repository.GetDeletedPageByIdAsync(deleted.Id));
    }

    [MySqlFact]
    public async Task ProviderNeutralSearchAutocompleteAndTagQueriesPreserveUnicode()
    {
        var connectionString = RequireConnectionString();
        var factory = new MySqlDatabaseConnectionFactory(connectionString);
        await new MySqlSchemaInitializer().InitializeAsync(factory);
        var repository = new MySqlPageRepository(factory);
        var suffix = Guid.NewGuid().ToString("N");
        var searchToken = $"search-{suffix}";
        var page = NewPage($"Search::{suffix} 世界");
        page.Description = "検索 description";
        await repository.CreatePageAsync(page);
        var support = new MySqlPageSupportRepository(factory);
        await support.ReplaceSearchTokensAsync(page.Id, [new TwPageToken { Token = searchToken, Weight = 2, DoubleMetaphone = "SJ" }]);
        await repository.ReplaceTagsAsync(page.Id, [$"lore-{suffix}"]);

        var autocomplete = await repository.AutoCompletePageAsync(suffix);
        Assert.Contains(autocomplete, value => value.Id == page.Id);
        var search = await repository.SearchPagesAsync([searchToken], 1);
        Assert.Contains(search, value => value.Id == page.Id);
        var tagged = await repository.GetPageInfoByTagAsync($"lore-{suffix}");
        Assert.Contains(tagged, value => value.Id == page.Id);
        var associated = await repository.GetAssociatedTagsAsync($"lore-{suffix}");
        Assert.Contains(associated, value => value.Tag == $"lore-{suffix}");
    }

    private static TwPage NewPage(string name) => new()
    {
        Name = name,
        Description = "test",
        Body = "body",
        CreatedByUserId = Guid.NewGuid(),
        ModifiedByUserId = Guid.NewGuid()
    };

    private static string RequireConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("CONTINUUMWIKI_MYSQL_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("MySQL test connection is missing.");
        return value;
    }
}
