SELECT
    P.Id,
    R.Name,
    R.Description,
    R.Body,
    R.Revision,
    R.Navigation,
    P.Revision AS MostCurrentRevision,
    P.CreatedByUserId,
    P.CreatedDate,
    R.ModifiedByUserId,
    R.ModifiedDate,
    R.ChangeSummary,
    R.DataHash,
    P.IsDeleted
FROM Page P
INNER JOIN PageRevision R ON R.PageId = P.Id
    AND R.Revision = COALESCE(@Revision, P.Revision)
WHERE P.Id = @PageId
  AND (@IncludeDeleted = TRUE OR P.IsDeleted = FALSE);
