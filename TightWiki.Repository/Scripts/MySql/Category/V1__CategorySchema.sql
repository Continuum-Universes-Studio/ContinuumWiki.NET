CREATE TABLE IF NOT EXISTS MySqlCategorySchemaVersion (
    Domain VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    Version INT NOT NULL,
    AppliedAtUtc DATETIME(6) NOT NULL,
    CONSTRAINT PK_MySqlCategorySchemaVersion PRIMARY KEY (Domain)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS PageCategoryIndexState (
    PageId INT UNSIGNED NOT NULL,
    IndexedPageRevision INT UNSIGNED NOT NULL,
    IndexState VARCHAR(16) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    IndexGeneration CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    SourcePageId VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
    SourceRevisionId VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
    SourceWikiIdentity VARCHAR(256) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
    IndexedAtUtc DATETIME(6) NOT NULL,
    CONSTRAINT PK_PageCategoryIndexState PRIMARY KEY (PageId),
    CONSTRAINT FK_PageCategoryIndexState_Page FOREIGN KEY (PageId) REFERENCES Page (Id) ON DELETE CASCADE,
    CONSTRAINT CK_PageCategoryIndexState_State CHECK (IndexState IN ('Verified', 'Incomplete', 'Failed', 'Unknown')),
    INDEX IX_PageCategoryIndexState_Revision (IndexedPageRevision, IndexState)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS PageCategoryMembership (
    PageId INT UNSIGNED NOT NULL,
    CategoryNavigation VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    SortKey VARCHAR(512) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
    SourcePageRevision INT UNSIGNED NOT NULL,
    SourcePageId VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
    SourceRevisionId VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
    SourceWikiIdentity VARCHAR(256) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
    IndexGeneration CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    IndexedAtUtc DATETIME(6) NOT NULL,
    IndexState VARCHAR(16) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT 'Verified',
    CONSTRAINT PK_PageCategoryMembership PRIMARY KEY (PageId, CategoryNavigation),
    CONSTRAINT FK_PageCategoryMembership_Page FOREIGN KEY (PageId) REFERENCES Page (Id) ON DELETE CASCADE,
    CONSTRAINT CK_PageCategoryMembership_State CHECK (IndexState = 'Verified'),
    INDEX IX_PageCategoryMembership_CategorySort (CategoryNavigation, SortKey, PageId),
    INDEX IX_PageCategoryMembership_PageRevision (PageId, SourcePageRevision)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT INTO MySqlCategorySchemaVersion (Domain, Version, AppliedAtUtc)
VALUES ('category', 1, UTC_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE Version = GREATEST(Version, VALUES(Version)), AppliedAtUtc = VALUES(AppliedAtUtc);
