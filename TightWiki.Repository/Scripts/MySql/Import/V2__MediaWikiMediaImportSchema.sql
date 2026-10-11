CREATE TABLE IF NOT EXISTS MediaWikiImportMedia (
    ImportBatchId BIGINT UNSIGNED NOT NULL,
    SourceFilePageId VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    SourceFileName VARCHAR(512) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NOT NULL,
    TargetPageId INT UNSIGNED NOT NULL,
    TargetPageFileId INT UNSIGNED NOT NULL,
    SourceSha1 VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NULL,
    OriginalMimeType VARCHAR(255) CHARACTER SET ascii COLLATE ascii_bin NULL,
    OriginalSize BIGINT UNSIGNED NOT NULL,
    OriginalSha256 CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    ArchiveMetadata TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
    Status VARCHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    CONSTRAINT PK_MediaWikiImportMedia PRIMARY KEY (ImportBatchId, SourceFilePageId),
    CONSTRAINT UX_MediaWikiImportMedia_Target UNIQUE (ImportBatchId, TargetPageFileId),
    CONSTRAINT FK_MediaWikiImportMedia_Batch FOREIGN KEY (ImportBatchId) REFERENCES MediaWikiImportBatch (ImportBatchId) ON DELETE CASCADE,
    CONSTRAINT FK_MediaWikiImportMedia_Page FOREIGN KEY (TargetPageId) REFERENCES Page (Id) ON DELETE CASCADE,
    CONSTRAINT FK_MediaWikiImportMedia_File FOREIGN KEY (TargetPageFileId) REFERENCES PageFile (Id) ON DELETE CASCADE,
    INDEX IX_MediaWikiImportMedia_Name (ImportBatchId, SourceFileName)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS MediaWikiImportMediaRevision (
    ImportBatchId BIGINT UNSIGNED NOT NULL,
    SourceFilePageId VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    SourceMediaRevisionId VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    TargetPageFileId INT UNSIGNED NOT NULL,
    TargetFileRevision INT UNSIGNED NOT NULL,
    SourceTimestamp DATETIME(6) NULL,
    ContributorKind VARCHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    ContributorSourceId VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
    ContributorDisplayName VARCHAR(512) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
    ContributorIpAddress VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NULL,
    SourceSha1 VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NULL,
    MimeType VARCHAR(255) CHARACTER SET ascii COLLATE ascii_bin NULL,
    Size BIGINT UNSIGNED NOT NULL,
    Sha256 CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    RelativeBinaryPath VARCHAR(1024) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NOT NULL,
    ArchiveMetadata TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
    Status VARCHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    CONSTRAINT PK_MediaWikiImportMediaRevision PRIMARY KEY (ImportBatchId, SourceFilePageId, SourceMediaRevisionId),
    CONSTRAINT UX_MediaWikiImportMediaRevision_Target UNIQUE (ImportBatchId, TargetPageFileId, TargetFileRevision),
    CONSTRAINT FK_MediaWikiImportMediaRevision_Media FOREIGN KEY (ImportBatchId, SourceFilePageId) REFERENCES MediaWikiImportMedia (ImportBatchId, SourceFilePageId) ON DELETE CASCADE,
    INDEX IX_MediaWikiImportMediaRevision_Hash (ImportBatchId, Sha256)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS MediaWikiImportMediaReference (
    ImportBatchId BIGINT UNSIGNED NOT NULL,
    SourcePageId VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    SourceRevisionId VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    SourceFileName VARCHAR(512) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NOT NULL,
    SourceFilePageId VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NULL,
    TargetPageId INT UNSIGNED NOT NULL,
    TargetPageRevision INT UNSIGNED NOT NULL,
    TargetPageFileId INT UNSIGNED NULL,
    ResolutionStatus VARCHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    CONSTRAINT PK_MediaWikiImportMediaReference PRIMARY KEY (ImportBatchId, SourcePageId, SourceRevisionId, SourceFileName),
    CONSTRAINT FK_MediaWikiImportMediaReference_Batch FOREIGN KEY (ImportBatchId) REFERENCES MediaWikiImportBatch (ImportBatchId) ON DELETE CASCADE,
    CONSTRAINT FK_MediaWikiImportMediaReference_Page FOREIGN KEY (TargetPageId) REFERENCES Page (Id) ON DELETE CASCADE,
    CONSTRAINT FK_MediaWikiImportMediaReference_Revision FOREIGN KEY (ImportBatchId, SourcePageId, SourceRevisionId) REFERENCES MediaWikiImportRevision (ImportBatchId, SourcePageId, SourceRevisionId) ON DELETE CASCADE,
    CONSTRAINT FK_MediaWikiImportMediaReference_File FOREIGN KEY (TargetPageFileId) REFERENCES PageFile (Id) ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

INSERT INTO MediaWikiImportSchemaVersion (Domain, Version, AppliedAtUtc)
VALUES ('mediawiki-import', 2, UTC_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE Version = GREATEST(Version, VALUES(Version)), AppliedAtUtc = VALUES(AppliedAtUtc);
