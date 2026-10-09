INSERT INTO PageRevision
    (PageId, Revision, Name, Namespace, Navigation, Description, Body, DataHash,
     ChangeSummary, ModifiedByUserId, ModifiedDate)
VALUES
    (@PageId, @Revision, @Name, @Namespace, @Navigation, @Description, @Body, @DataHash,
     @ChangeSummary, @ModifiedByUserId, @ModifiedDate);
