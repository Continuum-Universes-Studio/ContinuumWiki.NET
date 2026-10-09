CREATE TABLE IF NOT EXISTS DeletedPageRevision (
    PageId INT UNSIGNED NOT NULL,
    Revision INT UNSIGNED NOT NULL,
    Name VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    Namespace VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL,
    Navigation VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    Description TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    Body LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    DataHash INT NOT NULL,
    ChangeSummary VARCHAR(512) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    ModifiedByUserId CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    ModifiedDate DATETIME(6) NOT NULL,
    DeletedByUserId CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    DeletedDate DATETIME(6) NOT NULL,
    CONSTRAINT PK_DeletedPageRevision PRIMARY KEY (PageId, Revision),
    CONSTRAINT FK_DeletedPageRevision_Page FOREIGN KEY (PageId) REFERENCES Page (Id) ON DELETE CASCADE,
    INDEX IX_DeletedPageRevision_PageDate (PageId, DeletedDate, Revision),
    INDEX IX_DeletedPageRevision_Navigation (Navigation)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS DeletedPageRevisionAttachment (
    PageId INT UNSIGNED NOT NULL,
    PageFileId INT UNSIGNED NOT NULL,
    FileRevision INT UNSIGNED NOT NULL,
    PageRevision INT UNSIGNED NOT NULL,
    CONSTRAINT PK_DeletedPageRevisionAttachment PRIMARY KEY (PageId, PageFileId, FileRevision, PageRevision),
    CONSTRAINT UX_DeletedPageRevisionAttachment_PageFileRevision UNIQUE (PageId, PageFileId, PageRevision),
    CONSTRAINT FK_DeletedPageRevisionAttachment_Revision FOREIGN KEY (PageId, PageRevision) REFERENCES DeletedPageRevision (PageId, Revision) ON DELETE CASCADE,
    CONSTRAINT FK_DeletedPageRevisionAttachment_File FOREIGN KEY (PageFileId) REFERENCES PageFile (Id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT INTO MySqlPageSchemaVersion (Domain, Version, AppliedAtUtc)
VALUES ('deleted-revisions', 3, UTC_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE Version = GREATEST(Version, VALUES(Version)), AppliedAtUtc = VALUES(AppliedAtUtc);
