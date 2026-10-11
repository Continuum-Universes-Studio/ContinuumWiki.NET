using TightWiki.Plugin.Models;

namespace TightWiki.Tests.Unit;

public sealed class MediaWikiCategoryPersistenceContractTests
{
    [Fact]
    public void ReapplyingTheSameVerifiedRevisionAndMembershipsIsNoChange()
    {
        var generation = Guid.NewGuid();
        var indexedAt = DateTime.UtcNow;
        var membership = Membership(17, 4, generation, indexedAt, "Jovan");
        var request = new TwCategoryMembershipReplacementRequest(17, 4, [membership], true, generation, indexedAt);

        var result = TwCategoryMembershipReplacementPolicy.Classify(request, 4, [membership]);

        Assert.Equal(TwCategoryMembershipReplacementDisposition.NoChange, result);
    }

    [Fact]
    public void VerifiedRevisionWithChangedSortKeyIsApplied()
    {
        var generation = Guid.NewGuid();
        var indexedAt = DateTime.UtcNow;
        var request = new TwCategoryMembershipReplacementRequest(
            17,
            4,
            [Membership(17, 4, generation, indexedAt, "A")],
            true,
            generation,
            indexedAt);

        var result = TwCategoryMembershipReplacementPolicy.Classify(
            request,
            4,
            [Membership(17, 4, generation, indexedAt, "B")]);

        Assert.Equal(TwCategoryMembershipReplacementDisposition.Applied, result);
    }

    [Fact]
    public void IncompleteExtractionCannotBeInterpretedAsMembershipRemoval()
    {
        var generation = Guid.NewGuid();
        var indexedAt = DateTime.UtcNow;
        var request = new TwCategoryMembershipReplacementRequest(17, 4, [], false, generation, indexedAt);

        var result = TwCategoryMembershipReplacementPolicy.Classify(request, 4, [Membership(17, 4, generation, indexedAt, null)]);

        Assert.Equal(TwCategoryMembershipReplacementDisposition.IncompleteExtraction, result);
    }

    [Fact]
    public void StaleRevisionCannotOverwriteNewerIndexedMemberships()
    {
        var generation = Guid.NewGuid();
        var indexedAt = DateTime.UtcNow;
        var request = new TwCategoryMembershipReplacementRequest(
            17,
            4,
            [Membership(17, 4, generation, indexedAt, "A")],
            true,
            generation,
            indexedAt);

        var result = TwCategoryMembershipReplacementPolicy.Classify(request, 5, []);

        Assert.Equal(TwCategoryMembershipReplacementDisposition.RevisionConflict, result);
    }

    [Fact]
    public void DuplicateMembershipsAndOversizedSortKeysAreInvalid()
    {
        var generation = Guid.NewGuid();
        var indexedAt = DateTime.UtcNow;
        var duplicate = Membership(17, 4, generation, indexedAt, "A");
        var duplicateRequest = new TwCategoryMembershipReplacementRequest(17, 4, [duplicate, duplicate], true, generation, indexedAt);
        var oversizedRequest = new TwCategoryMembershipReplacementRequest(
            17,
            4,
            [Membership(17, 4, generation, indexedAt, new string('x', TwCategoryMembershipLimits.MaxSortKeyCharacters + 1))],
            true,
            generation,
            indexedAt);

        Assert.Equal("DuplicateMembership", TwCategoryMembershipReplacementPolicy.Validate(duplicateRequest));
        Assert.Equal("SortKeyLimitExceeded", TwCategoryMembershipReplacementPolicy.Validate(oversizedRequest));
    }

    [Fact]
    public void ForgedCategoryIdentityIsRejected()
    {
        var generation = Guid.NewGuid();
        var indexedAt = DateTime.UtcNow;
        var forged = new TwCategoryMembershipRecord(
            17,
            new TwCategoryIdentity("category::divines", "not-divines"),
            null,
            4,
            "source-page",
            "source-revision",
            "source-wiki",
            generation,
            indexedAt);
        var request = new TwCategoryMembershipReplacementRequest(17, 4, [forged], true, generation, indexedAt);

        Assert.Equal("CategoryNotCanonical", TwCategoryMembershipReplacementPolicy.Validate(request));
    }

    [Fact]
    public void MemberQueriesAreBoundedAndDoNotCarryAnUnfilteredTotalCount()
    {
        var category = AssertCategory("Divines");

        Assert.Null(TwCategoryMembershipReplacementPolicy.Validate(new TwCategoryMemberQuery(category, null, 1, 100)));
        Assert.Equal("PageSizeLimitExceeded", TwCategoryMembershipReplacementPolicy.Validate(new TwCategoryMemberQuery(category, null, 1, 101)));
        Assert.Equal("InvalidPageNumber", TwCategoryMembershipReplacementPolicy.Validate(new TwCategoryMemberQuery(category, null, 0, 50)));
        Assert.DoesNotContain(typeof(TwCategoryMemberPage).GetProperties(), property => property.Name.Contains("Total", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CategoryIdentityUsesTheExistingNamespaceNavigationRules()
    {
        var identity = AssertCategory("  Divines  ");

        Assert.Equal("category::divines", identity.CanonicalNavigation);
        Assert.Equal("divines", identity.NormalizedTitle);
        Assert.False(TwCategoryIdentity.TryCreate("!!!", out _));
    }

    private static TwCategoryIdentity AssertCategory(string title)
    {
        Assert.True(TwCategoryIdentity.TryCreate(title, out var category));
        return category;
    }

    private static TwCategoryMembershipRecord Membership(int pageId, int revision, Guid generation, DateTime indexedAt, string? sortKey)
        => new(pageId, AssertCategory("Divines"), sortKey, revision, "source-page", "source-revision", "source-wiki", generation, indexedAt);
}
