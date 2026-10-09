UPDATE Page
SET ActiveNavigation = NULL,
    IsDeleted = TRUE,
    DeletedByUserId = @DeletedByUserId,
    DeletedDate = @DeletedDate
WHERE Id = @PageId
  AND Revision = @ExpectedRevision
  AND IsDeleted = FALSE;
