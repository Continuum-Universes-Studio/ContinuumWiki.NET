INSERT INTO Page
    (Name, Namespace, Navigation, ActiveNavigation, Description, Revision,
     CreatedByUserId, CreatedDate, ModifiedByUserId, ModifiedDate)
VALUES
    (@Name, @Namespace, @Navigation, @Navigation, @Description, 1,
     @CreatedByUserId, @CreatedDate, @ModifiedByUserId, @ModifiedDate);

SELECT LAST_INSERT_ID();
