CREATE TABLE IF NOT EXISTS MySqlPageSchemaVersion (
    Domain VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    Version INT NOT NULL,
    AppliedAtUtc DATETIME(6) NOT NULL,
    CONSTRAINT PK_MySqlPageSchemaVersion PRIMARY KEY (Domain)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS Page (
    Id INT UNSIGNED NOT NULL AUTO_INCREMENT,
    Name VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    Namespace VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL,
    Navigation VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    ActiveNavigation VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL,
    Description TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    Revision INT UNSIGNED NOT NULL,
    CreatedByUserId CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    CreatedDate DATETIME(6) NOT NULL,
    ModifiedByUserId CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    ModifiedDate DATETIME(6) NOT NULL,
    IsDeleted BOOLEAN NOT NULL DEFAULT FALSE,
    DeletedByUserId CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
    DeletedDate DATETIME(6) NULL,
    CONSTRAINT PK_Page PRIMARY KEY (Id),
    CONSTRAINT UX_Page_ActiveNavigation UNIQUE (ActiveNavigation),
    CONSTRAINT CK_Page_DeletedState CHECK ((IsDeleted = FALSE AND ActiveNavigation IS NOT NULL AND DeletedDate IS NULL) OR (IsDeleted = TRUE AND ActiveNavigation IS NULL)),
    INDEX IX_Page_Namespace (Namespace),
    INDEX IX_Page_Deleted (IsDeleted, ModifiedDate)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS PageRevision (
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
    CONSTRAINT PK_PageRevision PRIMARY KEY (PageId, Revision),
    CONSTRAINT FK_PageRevision_Page FOREIGN KEY (PageId) REFERENCES Page (Id) ON DELETE CASCADE,
    CONSTRAINT UX_PageRevision_Navigation UNIQUE (PageId, Revision, Navigation),
    INDEX IX_PageRevision_History (PageId, ModifiedDate, Revision)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS PageTag (
    PageId INT UNSIGNED NOT NULL,
    Tag VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    CONSTRAINT PK_PageTag PRIMARY KEY (PageId, Tag),
    CONSTRAINT FK_PageTag_Page FOREIGN KEY (PageId) REFERENCES Page (Id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS PageReference (
    PageId INT UNSIGNED NOT NULL,
    ReferencesPageName VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    ReferencesPageNavigation VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    ReferencesPageId INT UNSIGNED NULL,
    CONSTRAINT PK_PageReference PRIMARY KEY (PageId, ReferencesPageNavigation),
    CONSTRAINT FK_PageReference_Page FOREIGN KEY (PageId) REFERENCES Page (Id) ON DELETE CASCADE,
    CONSTRAINT FK_PageReference_Target FOREIGN KEY (ReferencesPageId) REFERENCES Page (Id) ON DELETE SET NULL,
    INDEX IX_PageReference_Target (ReferencesPageId)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT INTO MySqlPageSchemaVersion (Domain, Version, AppliedAtUtc)
VALUES ('pages', 1, UTC_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE Version = GREATEST(Version, VALUES(Version)), AppliedAtUtc = VALUES(AppliedAtUtc);
