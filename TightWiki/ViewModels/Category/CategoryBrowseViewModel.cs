using TightWiki.Plugin.Models;
using TightWiki.ViewModels;

namespace TightWiki.ViewModels.Category;

public sealed class CategoryBrowseViewModel : TwViewModel
{
    public TwCategoryBrowseDisposition Disposition { get; init; }
    public string? CategoryTitle { get; init; }
    public string? CategoryNavigation { get; init; }
    public IReadOnlyList<TwCategoryMember> Members { get; init; } = [];
    public bool HasMore { get; init; }
    public int PageNumber { get; init; }
    public int PageSize { get; init; }
    public TwCategoryIndexConsistencyState? ConsistencyState { get; init; }
    public string? DiagnosticCode { get; init; }

    public static CategoryBrowseViewModel From(TwCategoryBrowseResult result, int pageNumber, int pageSize)
        => new()
        {
            Disposition = result.Disposition,
            CategoryTitle = result.Category?.NormalizedTitle,
            CategoryNavigation = result.Category?.CanonicalNavigation,
            Members = result.VisibleMembers,
            HasMore = result.HasMore,
            PageNumber = pageNumber,
            PageSize = pageSize,
            ConsistencyState = result.Consistency?.State,
            DiagnosticCode = result.DiagnosticCode
        };
}
