-- Disposable-only CW-M2-006C migrated-application fixture.
-- The hash is the ASP.NET Identity hash for RecoveryPass!123.
INSERT IGNORE INTO AspNetUsers (Id, UserName, NormalizedUserName, EmailConfirmed, PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled, AccessFailedCount, PasswordHash, SecurityStamp, ConcurrencyStamp)
VALUES ('f834bbb1-04e6-4d82-907b-b6d8cf0d0c55', 'recovery-admin', 'RECOVERY-ADMIN', 0, 0, 0, 1, 0, 'AQAAAAIAAYagAAAAEH1IJgRE0joG5wK9BHpIzsmGWL3nwSlkpITME5YngcEWKECdUCTquVTxwyJD2ulELg==', 'PB5UNJEGMVJVFGBZS4MBLNZAU54BO3AT', 'dccb8f0a-7309-40e8-84fb-dd080b002394');
INSERT IGNORE INTO Profile (UserId, AccountName, Navigation, CreatedDate, ModifiedDate)
VALUES ('f834bbb1-04e6-4d82-907b-b6d8cf0d0c55', 'recovery-admin', 'recovery-admin', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
INSERT IGNORE INTO AccountRole (UserId, RoleId)
SELECT 'f834bbb1-04e6-4d82-907b-b6d8cf0d0c55', Id FROM Role WHERE Name = 'Administrator';

INSERT INTO Page (Id, Name, Namespace, Navigation, ActiveNavigation, Description, Revision, CreatedByUserId, CreatedDate, ModifiedByUserId, ModifiedDate)
VALUES (900001, 'Recovery Article 世界', NULL, 'recovery-article', 'recovery-article', 'recovery fixture', 1, 'f834bbb1-04e6-4d82-907b-b6d8cf0d0c55', UTC_TIMESTAMP(6), 'f834bbb1-04e6-4d82-907b-b6d8cf0d0c55', UTC_TIMESTAMP(6));
INSERT INTO PageRevision (PageId, Revision, Name, Namespace, Navigation, Description, Body, DataHash, ChangeSummary, ModifiedByUserId, ModifiedDate)
VALUES (900001, 0, 'Recovery Article 世界', NULL, 'recovery-article', 'recovery fixture', 'Historical Recovery 世界', 101, 'initial', 'f834bbb1-04e6-4d82-907b-b6d8cf0d0c55', UTC_TIMESTAMP(6)),
       (900001, 1, 'Recovery Article 世界', NULL, 'recovery-article', 'recovery fixture', 'Current Recovery 世界 [[Home]]', 102, 'current', 'f834bbb1-04e6-4d82-907b-b6d8cf0d0c55', UTC_TIMESTAMP(6));
INSERT INTO PageTag VALUES (900001, 'RecoveryTag');
INSERT INTO PageReference (PageId, ReferencesPageName, ReferencesPageNavigation, ReferencesPageId)
SELECT 900001, 'Home', 'home', Id FROM Page WHERE ActiveNavigation = 'home' LIMIT 1;
INSERT INTO PageComment (PageId, CreatedDate, UserId, Body)
VALUES (900001, UTC_TIMESTAMP(6), 'f834bbb1-04e6-4d82-907b-b6d8cf0d0c55', 'Recovered comment 世界');
INSERT INTO PageFile (Id, PageId, Name, Navigation, Revision, CreatedDate)
VALUES (900001, 900001, 'recovery.bin', 'recovery-bin', 1, UTC_TIMESTAMP(6));
INSERT INTO PageFileRevision (PageFileId, ContentType, Size, CreatedByUserId, CreatedDate, Data, Revision, DataHash)
VALUES (900001, 'application/octet-stream', 28, 'f834bbb1-04e6-4d82-907b-b6d8cf0d0c55', UTC_TIMESTAMP(6), _binary'RECOVERY-ORIGINAL-ATTACHMENT', 0, 201),
       (900001, 'application/octet-stream', 20, 'f834bbb1-04e6-4d82-907b-b6d8cf0d0c55', UTC_TIMESTAMP(6), _binary'RECOVERY-REPLACEMENT', 1, 202);
INSERT INTO PageRevisionAttachment VALUES (900001, 900001, 0, 0), (900001, 900001, 1, 1);
INSERT INTO PageToken VALUES (900001, 'Recovery', 1.0, 'REC');
INSERT INTO MenuItem (Name, Link, Ordinal) VALUES ('Migration Recovery Menu', '/recovery-article', 99);
INSERT INTO AspNetUserClaims (UserId, ClaimType, ClaimValue) VALUES
  ('f834bbb1-04e6-4d82-907b-b6d8cf0d0c55', 'language', 'fr'),
  ('f834bbb1-04e6-4d82-907b-b6d8cf0d0c55', 'theme', 'Darkly');
