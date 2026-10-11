CREATE TABLE IF NOT EXISTS MediaWikiImportSchemaVersion (
    Domain VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NOT NULL,
    Version INT NOT NULL,
    AppliedAtUtc DATETIME(6) NOT NULL,
    CONSTRAINT PK_MediaWikiImportSchemaVersion PRIMARY KEY (Domain)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS MediaWikiImportBatch (
    ImportBatchId BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    SourceWikiIdentity VARCHAR(512) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NOT NULL,
    SourceExportSha256 CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    SourceExportByteLength BIGINT UNSIGNED NOT NULL,
    ExportVersion VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
    PlannerVersion VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    PlanSchemaVersion VARCHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    MappingConfigurationIdentity CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    Status VARCHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    PlannedPageCount INT UNSIGNED NOT NULL,
    PlannedRevisionCount INT UNSIGNED NOT NULL,
    ImportedPageCount INT UNSIGNED NOT NULL DEFAULT 0,
    ImportedRevisionCount INT UNSIGNED NOT NULL DEFAULT 0,
    StartedAtUtc DATETIME(6) NOT NULL,
    CompletedAtUtc DATETIME(6) NULL,
    DiagnosticsJson LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
    ReconciliationJson LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
    CONSTRAINT PK_MediaWikiImportBatch PRIMARY KEY (ImportBatchId),
    INDEX IX_MediaWikiImportBatch_Source (SourceWikiIdentity, SourceExportSha256, MappingConfigurationIdentity),
    INDEX IX_MediaWikiImportBatch_Status (Status, StartedAtUtc)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS MediaWikiImportPage (
    ImportBatchId BIGINT UNSIGNED NOT NULL,
    SourcePageId VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    TargetPageId INT UNSIGNED NOT NULL,
    SourceNamespaceId VARCHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    SourceNamespaceName VARCHAR(256) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
    OriginalTitle VARCHAR(512) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NOT NULL,
    TargetNamespace VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
    TargetNavigation VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NOT NULL,
    IsRedirect BOOLEAN NOT NULL,
    RedirectTargetSourceTitle VARCHAR(512) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
    RedirectTargetNavigation VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
    Status VARCHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    DiagnosticsJson LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
    CONSTRAINT PK_MediaWikiImportPage PRIMARY KEY (ImportBatchId, SourcePageId),
    CONSTRAINT UX_MediaWikiImportPage_Target UNIQUE (ImportBatchId, TargetPageId),
    CONSTRAINT FK_MediaWikiImportPage_Batch FOREIGN KEY (ImportBatchId) REFERENCES MediaWikiImportBatch (ImportBatchId) ON DELETE CASCADE,
    CONSTRAINT FK_MediaWikiImportPage_Page FOREIGN KEY (TargetPageId) REFERENCES Page (Id) ON DELETE CASCADE,
    INDEX IX_MediaWikiImportPage_Navigation (ImportBatchId, TargetNavigation)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS MediaWikiImportRevision (
    ImportBatchId BIGINT UNSIGNED NOT NULL,
    SourcePageId VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    SourceRevisionId VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    TargetPageId INT UNSIGNED NOT NULL,
    TargetRevision INT UNSIGNED NOT NULL,
    ParentSourceRevisionId VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NULL,
    SourceTimestamp DATETIME(6) NULL,
    SourceComment TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
    ContributorKind VARCHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    ContributorSourceId VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
    ContributorDisplayName VARCHAR(512) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
    ContributorIpAddress VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NULL,
    ContributorResolution VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    ContentModel VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
    ContentFormat VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
    SourceSha1 VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NULL,
    IsMinor BOOLEAN NOT NULL,
    IsDeleted BOOLEAN NOT NULL,
    IsSuppressed BOOLEAN NOT NULL,
    SourceTextPresent BOOLEAN NOT NULL,
    SourceTextState VARCHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    WikitextSha256 CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    DependenciesJson LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NOT NULL,
    Status VARCHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    DiagnosticsJson LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
    CONSTRAINT PK_MediaWikiImportRevision PRIMARY KEY (ImportBatchId, SourcePageId, SourceRevisionId),
    CONSTRAINT UX_MediaWikiImportRevision_Target UNIQUE (ImportBatchId, TargetPageId, TargetRevision),
    CONSTRAINT FK_MediaWikiImportRevision_Page FOREIGN KEY (ImportBatchId, SourcePageId) REFERENCES MediaWikiImportPage (ImportBatchId, SourcePageId) ON DELETE CASCADE,
    INDEX IX_MediaWikiImportRevision_Target (TargetPageId, TargetRevision),
    INDEX IX_MediaWikiImportRevision_Hash (ImportBatchId, WikitextSha256)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS MediaWikiImportDependency (
    ImportBatchId BIGINT UNSIGNED NOT NULL,
    SourcePageId VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    SourceRevisionId VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    DependencyType VARCHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    DependencyName VARCHAR(512) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NOT NULL,
    ResolutionStatus VARCHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    CONSTRAINT PK_MediaWikiImportDependency PRIMARY KEY (ImportBatchId, SourcePageId, SourceRevisionId, DependencyType, DependencyName),
    CONSTRAINT FK_MediaWikiImportDependency_Revision FOREIGN KEY (ImportBatchId, SourcePageId, SourceRevisionId) REFERENCES MediaWikiImportRevision (ImportBatchId, SourcePageId, SourceRevisionId) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

INSERT INTO MediaWikiImportSchemaVersion (Domain, Version, AppliedAtUtc)
VALUES ('mediawiki-import', 1, UTC_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE Version = GREATEST(Version, VALUES(Version)), AppliedAtUtc = VALUES(AppliedAtUtc);
