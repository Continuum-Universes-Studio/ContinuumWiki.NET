UPDATE Page
SET Name = @Name,
    Namespace = @Namespace,
    Navigation = @Navigation,
    ActiveNavigation = @Navigation,
    Description = @Description,
    Revision = @Revision,
    ModifiedByUserId = @ModifiedByUserId,
    ModifiedDate = @ModifiedDate
WHERE Id = @PageId
  AND Revision = @ExpectedRevision
  AND IsDeleted = FALSE;
