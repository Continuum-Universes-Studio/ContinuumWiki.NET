INSERT INTO Page (Id, Name, Namespace, Navigation, ActiveNavigation, Description, Revision, CreatedByUserId, CreatedDate, ModifiedByUserId, ModifiedDate)
SELECT 900001, 'Recovery Article 世界', NULL, 'recovery-article', 'recovery-article', 'recovery fixture', 1, Id, UTC_TIMESTAMP(6), Id, UTC_TIMESTAMP(6) FROM AspNetUsers WHERE UserName = 'recovery-admin';
INSERT INTO PageRevision (PageId, Revision, Name, Namespace, Navigation, Description, Body, DataHash, ChangeSummary, ModifiedByUserId, ModifiedDate)
SELECT 900001, 0, 'Recovery Article 世界', NULL, 'recovery-article', 'recovery fixture', 'Historical Recovery 世界', 101, 'initial', Id, UTC_TIMESTAMP(6) FROM AspNetUsers WHERE UserName = 'recovery-admin';
INSERT INTO PageRevision (PageId, Revision, Name, Namespace, Navigation, Description, Body, DataHash, ChangeSummary, ModifiedByUserId, ModifiedDate)
SELECT 900001, 1, 'Recovery Article 世界', NULL, 'recovery-article', 'recovery fixture', 'Current Recovery 世界 [[Home]]', 102, 'current', Id, UTC_TIMESTAMP(6) FROM AspNetUsers WHERE UserName = 'recovery-admin';
INSERT INTO PageTag VALUES (900001, 'RecoveryTag');
INSERT INTO PageReference (PageId, ReferencesPageName, ReferencesPageNavigation, ReferencesPageId)
SELECT 900001, 'Home', 'home', Id FROM Page WHERE ActiveNavigation = 'home' LIMIT 1;
INSERT INTO PageComment (PageId, CreatedDate, UserId, Body)
SELECT 900001, UTC_TIMESTAMP(6), Id, 'Recovered comment 世界' FROM AspNetUsers WHERE UserName = 'recovery-admin';
INSERT INTO PageFile (Id, PageId, Name, Navigation, Revision, CreatedDate)
VALUES (900001, 900001, 'recovery.bin', 'recovery-bin', 1, UTC_TIMESTAMP(6));
INSERT INTO PageFileRevision (PageFileId, ContentType, Size, CreatedByUserId, CreatedDate, Data, Revision, DataHash)
SELECT 900001, 'application/octet-stream', 28, Id, UTC_TIMESTAMP(6), _binary'RECOVERY-ORIGINAL-ATTACHMENT', 0, 201 FROM AspNetUsers WHERE UserName = 'recovery-admin';
INSERT INTO PageFileRevision (PageFileId, ContentType, Size, CreatedByUserId, CreatedDate, Data, Revision, DataHash)
SELECT 900001, 'application/octet-stream', 20, Id, UTC_TIMESTAMP(6), _binary'RECOVERY-REPLACEMENT', 1, 202 FROM AspNetUsers WHERE UserName = 'recovery-admin';
INSERT INTO PageRevisionAttachment VALUES (900001, 900001, 0, 0), (900001, 900001, 1, 1);
INSERT INTO PageToken VALUES (900001, 'Recovery', 1.0, 'REC');
INSERT INTO MenuItem (Name, Link, Ordinal) VALUES ('Recovery Menu', '/recovery-article', 99);
INSERT INTO AspNetUserClaims (UserId, ClaimType, ClaimValue) SELECT Id, 'language', 'fr' FROM AspNetUsers WHERE UserName = 'recovery-admin';
INSERT INTO AspNetUserClaims (UserId, ClaimType, ClaimValue) SELECT Id, 'theme', 'Darkly' FROM AspNetUsers WHERE UserName = 'recovery-admin';
