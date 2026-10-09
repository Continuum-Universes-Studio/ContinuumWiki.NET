UPDATE Page
SET ActiveNavigation = Navigation,
    IsDeleted = FALSE,
    DeletedByUserId = NULL,
    DeletedDate = NULL
WHERE Id = @PageId
  AND IsDeleted = TRUE;
