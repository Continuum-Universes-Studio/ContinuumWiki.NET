CREATE TABLE IF NOT EXISTS MySqlOperationsSchemaVersion (
    Domain VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    Version INT NOT NULL,
    AppliedAtUtc DATETIME(6) NOT NULL,
    CONSTRAINT PK_MySqlOperationsSchemaVersion PRIMARY KEY (Domain)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS Severity (
    Id INT NOT NULL AUTO_INCREMENT,
    Name VARCHAR(32) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    CONSTRAINT PK_Severity PRIMARY KEY (Id),
    CONSTRAINT UX_Severity_Name UNIQUE (Name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT INTO Severity (Name) VALUES
    ('Trace'), ('Debug'), ('Information'), ('Warning'), ('Error'), ('Critical'), ('None')
ON DUPLICATE KEY UPDATE Name = VALUES(Name);

CREATE TABLE IF NOT EXISTS Log (
    Id INT NOT NULL AUTO_INCREMENT,
    SeverityId INT NOT NULL,
    Text TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL,
    ExceptionText TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL,
    StackTrace LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL,
    CreatedDate DATETIME(6) NOT NULL,
    CONSTRAINT PK_Log PRIMARY KEY (Id),
    CONSTRAINT FK_Log_Severity FOREIGN KEY (SeverityId) REFERENCES Severity (Id),
    INDEX IX_Log_CreatedDate (CreatedDate),
    INDEX IX_Log_Severity (SeverityId, CreatedDate)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS PageStatistics (
    PageId INT UNSIGNED NOT NULL,
    LastCompileDateTime DATETIME(6) NOT NULL,
    TotalCompilationCount INT NOT NULL DEFAULT 0,
    TotalViewCount INT NOT NULL DEFAULT 0,
    LastWikifyTimeMs DECIMAL(18,3) NOT NULL DEFAULT 0,
    TotalWikifyTimeMs DECIMAL(18,3) NOT NULL DEFAULT 0,
    LastMatchCount INT NOT NULL DEFAULT 0,
    LastErrorCount INT NOT NULL DEFAULT 0,
    LastOutgoingLinkCount INT NOT NULL DEFAULT 0,
    LastTagCount INT NOT NULL DEFAULT 0,
    LastProcessedBodySize INT NOT NULL DEFAULT 0,
    LastBodySize INT NOT NULL DEFAULT 0,
    CONSTRAINT PK_PageStatistics PRIMARY KEY (PageId),
    CONSTRAINT FK_PageStatistics_Page FOREIGN KEY (PageId) REFERENCES Page (Id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS Emoji (
    Id INT NOT NULL AUTO_INCREMENT,
    Name VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    Shortcut VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    ImageData MEDIUMBLOB NULL,
    MimeType VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    CONSTRAINT PK_Emoji PRIMARY KEY (Id),
    CONSTRAINT UX_Emoji_Name UNIQUE (Name),
    CONSTRAINT UX_Emoji_Shortcut UNIQUE (Shortcut)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS EmojiCategory (
    EmojiId INT NOT NULL,
    Category VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    CONSTRAINT PK_EmojiCategory PRIMARY KEY (EmojiId, Category),
    CONSTRAINT FK_EmojiCategory_Emoji FOREIGN KEY (EmojiId) REFERENCES Emoji (Id) ON DELETE CASCADE,
    INDEX IX_EmojiCategory_Category (Category)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT INTO MySqlOperationsSchemaVersion (Domain, Version, AppliedAtUtc)
VALUES ('operations', 1, UTC_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE Version = GREATEST(Version, VALUES(Version)), AppliedAtUtc = VALUES(AppliedAtUtc);
