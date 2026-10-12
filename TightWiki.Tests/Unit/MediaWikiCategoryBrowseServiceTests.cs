using Microsoft.AspNetCore.Mvc;
using TightWiki.Controllers;
using TightWiki.Engine.MediaWiki;
using TightWiki.Plugin.Interfaces.Persistence;
using TightWiki.Plugin.Models;

namespace TightWiki.Tests.Unit;

public sealed class MediaWikiCategoryBrowseServiceTests
{
    [Fact]
    public void NativeCategoryRouteIsCatchAllGetOnly()
    {
        var controllerRoute = Assert.Single(typeof(CategoryController).GetCustomAttributes(typeof(RouteAttribute), inherit: true));
        var browse = typeof(CategoryController).GetMethod(nameof(CategoryController.Browse));
        Assert.NotNull(browse);
        Assert.Equal("Category", ((RouteAttribute)controllerRoute).Template);
        Assert.Equal("{*categoryTitle}", ((HttpGetAttribute)Assert.Single(browse!.GetCustomAttributes(typeof(HttpGetAttribute), inherit: true))).Template);
        Assert.Empty(browse.GetCustomAttributes(typeof(HttpPostAttribute), inherit: true));
    }

    [Fact]
    public async Task InvalidCategoryAndPagingAreRejectedBeforeRepositoryAccess()
    {
        var repository = new FakeRepository();
        var service = new TwCategoryBrowseService(repository);

        var invalidCategory = await service.BrowseAsync("!!!", null);
        var invalidPage = await service.BrowseAsync("Divines", null, 0);
        var oversizedPage = await service.BrowseAsync("Divines", null, pageSize: TwCategoryMembershipLimits.MaxCategoryMemberPageSize + 1);

        Assert.Equal(TwCategoryBrowseDisposition.InvalidCategory, invalidCategory.Disposition);
        Assert.Equal(TwCategoryBrowseDisposition.InvalidRequest, invalidPage.Disposition);
        Assert.Equal(TwCategoryBrowseDisposition.InvalidRequest, oversizedPage.Disposition);
        Assert.Equal(0, repository.ConsistencyCalls);
    }

    [Fact]
    public async Task RepositoryUnavailableIsNotFlattenedIntoEmptyCategory()
    {
        var service = new TwCategoryBrowseService(new FakeRepository(throwUnavailable: true));

        var result = await service.BrowseAsync("Divines", null);

        Assert.Equal(TwCategoryBrowseDisposition.RepositoryUnavailable, result.Disposition);
        Assert.Empty(result.VisibleMembers);
    }

    [Fact]
    public async Task UnindexedAndInconsistentStatesDoNotReadOrExposeMembers()
    {
        var unindexedRepository = new FakeRepository(new(TwCategoryIndexConsistencyState.Unindexed, 0));
        var staleRepository = new FakeRepository(new(TwCategoryIndexConsistencyState.Stale, 0));

        var unindexed = await new TwCategoryBrowseService(unindexedRepository).BrowseAsync("Divines", null);
        var stale = await new TwCategoryBrowseService(staleRepository).BrowseAsync("Divines", null);

        Assert.Equal(TwCategoryBrowseDisposition.Unindexed, unindexed.Disposition);
        Assert.Equal(TwCategoryBrowseDisposition.Inconsistent, stale.Disposition);
        Assert.Equal(TwCategoryIndexConsistencyState.Stale, stale.Consistency?.State);
        Assert.Equal(0, unindexedRepository.MemberCalls);
        Assert.Equal(0, staleRepository.MemberCalls);
    }

    [Fact]
    public async Task CurrentEmptyAndPopulatedStatesRemainDistinctWithoutTotalCounts()
    {
        Assert.True(TwCategoryIdentity.TryCreate("Divines", out var category));
        var emptyRepository = new FakeRepository(new(TwCategoryIndexConsistencyState.Current, 0), new([], false));
        var populatedRepository = new FakeRepository(
            new(TwCategoryIndexConsistencyState.Current, 0),
            new([new(7, "article::visible", "Visible", "", "A")], true));

        var empty = await new TwCategoryBrowseService(emptyRepository).BrowseAsync("Divines", null);
        var populated = await new TwCategoryBrowseService(populatedRepository).BrowseAsync("Divines", null);

        Assert.Equal(TwCategoryBrowseDisposition.IndexedEmpty, empty.Disposition);
        Assert.Equal(TwCategoryBrowseDisposition.Populated, populated.Disposition);
        Assert.Single(populated.VisibleMembers);
        Assert.True(populated.HasMore);
        Assert.DoesNotContain("Total", typeof(TwCategoryBrowseResult).GetProperties().Select(property => property.Name), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(category, empty.Category);
    }

    private sealed class FakeRepository(
        TwCategoryIndexConsistencyResult? consistency = null,
        TwCategoryMemberPage? members = null,
        bool throwUnavailable = false) : ITwCategoryMembershipRepository
    {
        private readonly TwCategoryIndexConsistencyResult? _consistency = consistency;
        private readonly TwCategoryMemberPage _members = members ?? new([], false);
        private readonly bool _throwUnavailable = throwUnavailable;
        public int ConsistencyCalls { get; private set; }
        public int MemberCalls { get; private set; }

        public Task<TwCategoryIndexConsistencyResult> GetIndexConsistencyAsync(int pageId, CancellationToken cancellationToken = default)
            => Task.FromException<TwCategoryIndexConsistencyResult>(new NotSupportedException());

        public Task<TwCategoryIndexConsistencyResult> GetCategoryIndexConsistencyAsync(TwCategoryIdentity category, CancellationToken cancellationToken = default)
        {
            ConsistencyCalls++;
            if (_throwUnavailable) return Task.FromException<TwCategoryIndexConsistencyResult>(new NotSupportedException());
            return Task.FromResult(_consistency ?? new(TwCategoryIndexConsistencyState.Unindexed, 0));
        }

        public Task<IReadOnlyList<TwCategoryMembershipRecord>> GetMembershipsByPageIdAsync(int pageId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TwCategoryMembershipRecord>>([]);

        public Task<TwCategoryMembershipReplacementResult> ReplaceMembershipsAsync(TwCategoryMembershipReplacementRequest request, CancellationToken cancellationToken = default)
            => Task.FromException<TwCategoryMembershipReplacementResult>(new NotSupportedException());

        public Task<TwCategoryMemberPage> GetMembersAsync(TwCategoryMemberQuery query, CancellationToken cancellationToken = default)
        {
            MemberCalls++;
            return Task.FromResult(_members);
        }

        public Task<TwCategoryMembershipReplacementResult> InvalidateMembershipsAsync(int pageId, int expectedPageRevision, CancellationToken cancellationToken = default)
            => Task.FromException<TwCategoryMembershipReplacementResult>(new NotSupportedException());
    }
}
