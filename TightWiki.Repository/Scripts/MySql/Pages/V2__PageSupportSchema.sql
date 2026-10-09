CREATE TABLE IF NOT EXISTS PageComment (
    Id INT UNSIGNED NOT NULL AUTO_INCREMENT,
    PageId INT UNSIGNED NOT NULL,
    CreatedDate DATETIME(6) NOT NULL,
    UserId CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    Body TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    CONSTRAINT PK_PageComment PRIMARY KEY (Id),
    CONSTRAINT FK_PageComment_Page FOREIGN KEY (PageId) REFERENCES Page (Id) ON DELETE CASCADE,
    INDEX IX_PageComment_PageId (PageId, CreatedDate)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS PageFile (
    Id INT UNSIGNED NOT NULL AUTO_INCREMENT,
    PageId INT UNSIGNED NOT NULL,
    Name VARCHAR(255) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    Navigation VARCHAR(255) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    Revision INT UNSIGNED NOT NULL DEFAULT 0,
    CreatedDate DATETIME(6) NOT NULL,
    CONSTRAINT PK_PageFile PRIMARY KEY (Id),
    CONSTRAINT UX_PageFile_PageNameRevision UNIQUE (PageId, Name, Revision),
    CONSTRAINT FK_PageFile_Page FOREIGN KEY (PageId) REFERENCES Page (Id) ON DELETE CASCADE,
    INDEX IX_PageFile_Navigation (PageId, Navigation, Revision)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS PageFileRevision (
    PageFileId INT UNSIGNED NOT NULL,
    ContentType VARCHAR(255) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    Size BIGINT UNSIGNED NOT NULL,
    CreatedByUserId CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    CreatedDate DATETIME(6) NOT NULL,
    Data LONGBLOB NOT NULL,
    Revision INT UNSIGNED NOT NULL,
    DataHash INT NOT NULL,
    CONSTRAINT PK_PageFileRevision PRIMARY KEY (PageFileId, Revision),
    CONSTRAINT FK_PageFileRevision_File FOREIGN KEY (PageFileId) REFERENCES PageFile (Id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS PageRevisionAttachment (
    PageId INT UNSIGNED NOT NULL,
    PageFileId INT UNSIGNED NOT NULL,
    FileRevision INT UNSIGNED NOT NULL,
    PageRevision INT UNSIGNED NOT NULL,
    CONSTRAINT PK_PageRevisionAttachment PRIMARY KEY (PageId, PageFileId, FileRevision, PageRevision),
    CONSTRAINT UX_PageRevisionAttachment_PageFileRevision UNIQUE (PageId, PageFileId, PageRevision),
    CONSTRAINT FK_PageRevisionAttachment_Page FOREIGN KEY (PageId) REFERENCES Page (Id) ON DELETE CASCADE,
    CONSTRAINT FK_PageRevisionAttachment_File FOREIGN KEY (PageFileId) REFERENCES PageFile (Id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS PageProcessingInstruction (
    PageId INT UNSIGNED NOT NULL,
    Instruction VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    CONSTRAINT PK_PageProcessingInstruction PRIMARY KEY (PageId, Instruction),
    CONSTRAINT FK_PageProcessingInstruction_Page FOREIGN KEY (PageId) REFERENCES Page (Id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS PageToken (
    PageId INT UNSIGNED NOT NULL,
    Token VARCHAR(255) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    Weight DOUBLE NOT NULL,
    DoubleMetaphone VARCHAR(255) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    CONSTRAINT PK_PageToken PRIMARY KEY (PageId, Token),
    CONSTRAINT FK_PageToken_Page FOREIGN KEY (PageId) REFERENCES Page (Id) ON DELETE CASCADE,
    INDEX IX_PageToken_Token (Token, PageId, Weight),
    INDEX IX_PageToken_DoubleMetaphone (DoubleMetaphone, PageId, Weight)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS CurrentPageEditors (
    PageId INT UNSIGNED NOT NULL,
    UserId CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    AccountName VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    UTCDate DATETIME(6) NOT NULL,
    CONSTRAINT PK_CurrentPageEditors PRIMARY KEY (PageId, UserId),
    CONSTRAINT FK_CurrentPageEditors_Page FOREIGN KEY (PageId) REFERENCES Page (Id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT INTO MySqlPageSchemaVersion (Domain, Version, AppliedAtUtc)
VALUES ('page-support', 2, UTC_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE Version = GREATEST(Version, VALUES(Version)), AppliedAtUtc = VALUES(AppliedAtUtc);
