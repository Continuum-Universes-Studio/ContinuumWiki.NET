using TightWiki.Plugin.Models;
using TightWiki.Repository.Helpers;

namespace TightWiki.Tests.Integration;

public sealed class MySqlDeletedRevisionIntegrationTests
{
    [MySqlFact]
    public async Task DeletedRevisionLifecyclePreservesContentAuthorUnicodeAndIsolation()
    {
        var connectionString = RequireConnectionString();
        var factory = new MySqlDatabaseConnectionFactory(connectionString);
        await new MySqlSchemaInitializer().InitializeAsync(factory);
        var repository = new MySqlPageRepository(factory);
        var author = Guid.NewGuid();
        var page = new TwPage
        {
            Name = $"DeletedRevision::{Guid.NewGuid():N}",
            Body = "初版 世界 🌌",
            CreatedByUserId = author,
            ModifiedByUserId = author,
            ChangeSummary = "initial"
        };
        await repository.CreatePageAsync(page);

        page.Body = "第二版 archived 世界 🚀";
        page.ModifiedByUserId = author;
        page.ChangeSummary = "unicode revision";
        await repository.UpdatePageAsync(page, 1);

        var deletedBy = Guid.NewGuid();
        await repository.ArchivePageRevisionAsync(page.Id, 1, deletedBy);
        var listed = await repository.GetDeletedPageRevisionsAsync(page.Id, 1, 50);
        var archived = Assert.Single(listed);
        Assert.Equal(1, archived.Revision);
        Assert.Equal("初版 世界 🌌", archived.Body);
        Assert.Equal(author, archived.ModifiedByUserId);
        Assert.Equal(deletedBy, archived.DeletedByUserId);
        Assert.Equal(2, (await repository.GetRevisionHistoryAsync(page.Navigation)).Single().Revision);

        var retrieved = await repository.GetDeletedPageRevisionAsync(page.Id, 1);
        Assert.NotNull(retrieved);
        Assert.Equal(archived.Body, retrieved.Body);

        var other = new TwPage
        {
            Name = $"DeletedRevisionOther::{Guid.NewGuid():N}",
            Body = "other",
            CreatedByUserId = Guid.NewGuid(),
            ModifiedByUserId = Guid.NewGuid()
        };
        await repository.CreatePageAsync(other);
        other.Body = "other updated";
        await repository.UpdatePageAsync(other, 1);
        await repository.ArchivePageRevisionAsync(other.Id, 1, Guid.NewGuid());

        await repository.PurgeDeletedPageRevisionAsync(page.Id, 1);
        Assert.Null(await repository.GetDeletedPageRevisionAsync(page.Id, 1));
        Assert.NotNull(await repository.GetDeletedPageRevisionAsync(other.Id, 1));
    }

    [MySqlFact]
    public async Task DeletedRevisionRestoreReinstatesHistoryAndRejectsCurrentRevisionArchive()
    {
        var connectionString = RequireConnectionString();
        var factory = new MySqlDatabaseConnectionFactory(connectionString);
        await new MySqlSchemaInitializer().InitializeAsync(factory);
        var repository = new MySqlPageRepository(factory);
        var page = new TwPage
        {
            Name = $"DeletedRevisionRestore::{Guid.NewGuid():N}",
            Body = "first",
            CreatedByUserId = Guid.NewGuid(),
            ModifiedByUserId = Guid.NewGuid()
        };
        await repository.CreatePageAsync(page);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ArchivePageRevisionAsync(page.Id, 1, Guid.NewGuid()));

        page.Body = "second";
        await repository.UpdatePageAsync(page, 1);
        await repository.ArchivePageRevisionAsync(page.Id, 1, Guid.NewGuid());
        Assert.Equal("second", (await repository.GetPageByIdAsync(page.Id))!.Body);

        await repository.RestoreDeletedPageRevisionAsync(page.Id, 1);
        var restored = await repository.GetPageByIdAsync(page.Id, 1);
        Assert.NotNull(restored);
        Assert.Equal("first", restored.Body);
        Assert.Equal(new[] { 2, 1 }, (await repository.GetRevisionHistoryAsync(page.Navigation)).Select(value => value.Revision));
        Assert.Null(await repository.GetDeletedPageRevisionAsync(page.Id, 1));
    }

    private static string RequireConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("CONTINUUMWIKI_MYSQL_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("MySQL test connection is missing.");
        return value;
    }
}
