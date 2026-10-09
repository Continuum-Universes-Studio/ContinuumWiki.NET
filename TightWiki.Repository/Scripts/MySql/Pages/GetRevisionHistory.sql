SELECT
    R.PageId,
    R.Name,
    R.Description,
    R.Revision,
    R.Navigation,
    R.ModifiedByUserId,
    R.ModifiedDate,
    P.CreatedByUserId,
    P.CreatedDate,
    P.Revision AS HighestRevision,
    (P.Revision - R.Revision) AS HigherRevisionCount,
    R.ChangeSummary,
    @PageSize AS PaginationPageSize,
    CEILING(COUNT(*) OVER () / @PageSize) AS PaginationPageCount
FROM Page P
INNER JOIN PageRevision R ON R.PageId = P.Id
WHERE P.ActiveNavigation = @Navigation
  AND P.IsDeleted = FALSE
ORDER BY R.Revision DESC
LIMIT @PageSize OFFSET @Offset;
