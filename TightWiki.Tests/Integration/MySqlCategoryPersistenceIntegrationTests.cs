using Dapper;
using MySqlConnector;
using TightWiki.Plugin.Models;
using TightWiki.Repository.Helpers;

namespace TightWiki.Tests.Integration;

public sealed class MySqlCategoryPersistenceIntegrationTests
{
    [CategoryPersistenceAuthorizationFact]
    public async Task ReplacementPersistsEmptyIndexAndReadBackIsCurrent()
    {
        var (_, pageRepository, repository) = await PrepareAsync();
        var page = NewPage($"CategoryPersistence::{Guid.NewGuid():N}");
        await pageRepository.CreatePageAsync(page);
        try
        {
            var request = Request(page.Id, 1, []);
            var applied = await repository.ReplaceMembershipsAsync(request);
            Assert.Equal(TwCategoryMembershipReplacementDisposition.Applied, applied.Disposition);

            var state = await repository.GetIndexConsistencyAsync(page.Id);
            Assert.Equal(TwCategoryIndexConsistencyState.Current, state.State);
            Assert.Equal(1, state.CurrentPageRevision);
            Assert.Equal(1, state.IndexedPageRevision);
            Assert.Empty(await repository.GetMembershipsByPageIdAsync(page.Id));

            var repeated = await repository.ReplaceMembershipsAsync(request with { IndexGeneration = Guid.NewGuid(), IndexedAtUtc = DateTime.UtcNow });
            Assert.Equal(TwCategoryMembershipReplacementDisposition.NoChange, repeated.Disposition);
        }
        finally
        {
            await pageRepository.DeletePageAsync(page.Id, 1, Guid.NewGuid());
        }
    }

    [CategoryPersistenceAuthorizationFact]
    public async Task StaleRevisionCannotOverwriteNewerPageAndIndexState()
    {
        var (_, pageRepository, repository) = await PrepareAsync();
        var page = NewPage($"CategoryPersistenceStale::{Guid.NewGuid():N}");
        await pageRepository.CreatePageAsync(page);
        try
        {
            var first = Request(page.Id, 1, [Membership(page.Id, 1, "A")]);
            Assert.Equal(TwCategoryMembershipReplacementDisposition.Applied,
                (await repository.ReplaceMembershipsAsync(first)).Disposition);

            page.Body = "new revision";
            await pageRepository.UpdatePageAsync(page, 1);
            var stale = await repository.ReplaceMembershipsAsync(first);

            Assert.Equal(TwCategoryMembershipReplacementDisposition.RevisionConflict, stale.Disposition);
            Assert.Equal(2, stale.AppliedPageRevision);
            Assert.Equal(TwCategoryIndexConsistencyState.Stale, (await repository.GetIndexConsistencyAsync(page.Id)).State);
        }
        finally
        {
            await pageRepository.DeletePageAsync(page.Id, 2, Guid.NewGuid());
        }
    }

    [CategoryPersistenceAuthorizationFact]
    public async Task FailedReplacementRollsBackAfterMembershipInsertFailure()
    {
        var (_, pageRepository, repository) = await PrepareAsync();
        var page = NewPage($"CategoryPersistenceRollback::{Guid.NewGuid():N}");
        await pageRepository.CreatePageAsync(page);
        try
        {
            var initial = Request(page.Id, 1, [Membership(page.Id, 1, "A")]);
            Assert.Equal(TwCategoryMembershipReplacementDisposition.Applied,
                (await repository.ReplaceMembershipsAsync(initial)).Disposition);

            var duplicateCategory = Request(page.Id, 1,
            [
                Membership(page.Id, 1, "B"),
                Membership(page.Id, 1, "C")
            ]) with { IndexGeneration = Guid.NewGuid(), IndexedAtUtc = DateTime.UtcNow };
            var failed = await repository.ReplaceMembershipsAsync(duplicateCategory);

            Assert.Equal(TwCategoryMembershipReplacementDisposition.PersistenceFailure, failed.Disposition);
            var remaining = Assert.Single(await repository.GetMembershipsByPageIdAsync(page.Id));
            Assert.Equal("A", remaining.SortKey);
            Assert.Equal(TwCategoryIndexConsistencyState.Current, (await repository.GetIndexConsistencyAsync(page.Id)).State);
        }
        finally
        {
            await pageRepository.DeletePageAsync(page.Id, 1, Guid.NewGuid());
        }
    }

    [CategoryPersistenceAuthorizationFact]
    public async Task MemberQueriesFilterPageSpecificDenyBeforeStablePagination()
    {
        var (factory, pageRepository, repository) = await PrepareAsync();
        var pages = new[]
        {
            NewPage($"CategoryPersistenceQuery::{Guid.NewGuid():N}-a"),
            NewPage($"CategoryPersistenceQuery::{Guid.NewGuid():N}-b"),
            NewPage($"CategoryPersistenceQuery::{Guid.NewGuid():N}-c")
        };
        var deniedPageId = 0;
        try
        {
            foreach (var page in pages)
            {
                await pageRepository.CreatePageAsync(page);
                var request = Request(page.Id, 1, [Membership(page.Id, 1, page.Name)]);
                Assert.Equal(TwCategoryMembershipReplacementDisposition.Applied,
                    (await repository.ReplaceMembershipsAsync(request)).Disposition);
            }

            deniedPageId = pages[1].Id;
            await using (var session = await factory.OpenSessionAsync())
            {
                var roleId = await session.Connection.ExecuteScalarAsync<int>("SELECT Id FROM Role WHERE Name = 'Anonymous';");
                var permissionId = await session.Connection.ExecuteScalarAsync<int>("SELECT Id FROM Permission WHERE Name = 'Read';");
                var denyId = await session.Connection.ExecuteScalarAsync<int>("SELECT Id FROM PermissionDisposition WHERE Name = 'Deny';");
                await session.Connection.ExecuteAsync("""
                    INSERT INTO RolePermission (RoleId, PermissionId, Namespace, PageId, PermissionDispositionId)
                    VALUES (@RoleId, @PermissionId, NULL, @PageId, @DenyId);
                    """, new { RoleId = roleId, PermissionId = permissionId, PageId = deniedPageId.ToString(), DenyId = denyId });
            }

            var category = AssertCategory("Divines");
            var result = await repository.GetMembersAsync(new TwCategoryMemberQuery(category, null, 1, 2));
            Assert.Equal(2, result.Members.Count);
            Assert.False(result.HasMore);
            Assert.DoesNotContain(result.Members, member => member.PageId == deniedPageId);
            Assert.Equal(result.Members.OrderBy(member => member.PageName, StringComparer.Ordinal).Select(member => member.PageId), result.Members.Select(member => member.PageId));
        }
        finally
        {
            await using var cleanup = await factory.OpenSessionAsync();
            if (deniedPageId > 0)
                await cleanup.Connection.ExecuteAsync("DELETE FROM RolePermission WHERE PageId = @PageId;", new { PageId = deniedPageId.ToString() });
            foreach (var page in pages)
                if (page.Id > 0)
                    await pageRepository.DeletePageAsync(page.Id, 1, Guid.NewGuid());
        }
    }

    private static async Task<(MySqlDatabaseConnectionFactory Factory, MySqlPageRepository Pages, MySqlCategoryMembershipRepository Categories)> PrepareAsync()
    {
        var connectionString = RequireApprovedConnectionString();
        var factory = new MySqlDatabaseConnectionFactory(connectionString);
        await new MySqlSchemaInitializer().InitializeAsync(factory);
        await new MySqlCategorySchemaInitializer().InitializeAsync(factory);
        return (factory, new MySqlPageRepository(factory), new MySqlCategoryMembershipRepository(factory));
    }

    private static TwCategoryMembershipReplacementRequest Request(int pageId, int revision, IReadOnlyList<TwCategoryMembershipRecord> memberships)
    {
        var generation = Guid.NewGuid();
        var indexedAt = DateTime.UtcNow;
        var normalized = memberships.Select(membership => membership with
        {
            IndexGeneration = generation,
            IndexedAtUtc = indexedAt
        }).ToList();
        return new(pageId, revision, normalized, true, generation, indexedAt, "source-page", "source-revision", "test-wiki");
    }

    private static TwCategoryMembershipRecord Membership(int pageId, int revision, string sortKey)
    {
        Assert.True(TwCategoryIdentity.TryCreate("Divines", out var category));
        return new(pageId, category, sortKey, revision, "source-page", "source-revision", "test-wiki", Guid.NewGuid(), DateTime.UtcNow);
    }

    private static TwCategoryIdentity AssertCategory(string title)
    {
        Assert.True(TwCategoryIdentity.TryCreate(title, out var category));
        return category;
    }

    private static TwPage NewPage(string name)
        => new()
        {
            Name = name,
            Body = "body",
            Description = "category persistence test",
            CreatedByUserId = Guid.NewGuid(),
            ModifiedByUserId = Guid.NewGuid()
        };

    private static string RequireApprovedConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("CONTINUUMWIKI_MYSQL_TEST_CONNECTION");
        var target = Environment.GetEnvironmentVariable("CONTINUUMWIKI_CATEGORY_PERSISTENCE_TARGET_DATABASE");
        var approvalPath = Environment.GetEnvironmentVariable("CONTINUUMWIKI_CATEGORY_PERSISTENCE_APPROVAL_PATH");
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(approvalPath))
            throw new InvalidOperationException("Category persistence authorization inputs are missing.");

        var builder = new MySqlConnectionStringBuilder(value);
        if (!string.Equals(builder.Database, target, StringComparison.Ordinal))
            throw new InvalidOperationException("The category persistence connection does not target the approved database.");
        if (!File.Exists(approvalPath) || !File.ReadAllText(approvalPath).Contains(target, StringComparison.Ordinal))
            throw new InvalidOperationException("The category persistence approval scope does not identify the approved database.");
        return value;
    }
}
