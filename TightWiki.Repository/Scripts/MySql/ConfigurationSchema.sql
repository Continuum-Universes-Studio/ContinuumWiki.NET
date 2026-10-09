CREATE TABLE IF NOT EXISTS SchemaVersion (
    VersionNumber INT NOT NULL,
    AppliedUtc DATETIME(6) NOT NULL,
    PRIMARY KEY (VersionNumber)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS ConfigurationGroup (
    Id INT NOT NULL AUTO_INCREMENT,
    Name VARCHAR(255) NOT NULL,
    Description TEXT NULL,
    PRIMARY KEY (Id),
    UNIQUE KEY UX_ConfigurationGroup_Name (Name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS ConfigurationEntry (
    Id INT NOT NULL AUTO_INCREMENT,
    ConfigurationGroupId INT NOT NULL,
    Name VARCHAR(255) NOT NULL,
    Value TEXT NULL,
    DataTypeId INT NOT NULL,
    Description TEXT NULL,
    IsEncrypted BOOLEAN NOT NULL,
    IsRequired BOOLEAN NOT NULL,
    PRIMARY KEY (Id),
    UNIQUE KEY UX_ConfigurationEntry_Group_Name (ConfigurationGroupId, Name),
    CONSTRAINT FK_ConfigurationEntry_Group FOREIGN KEY (ConfigurationGroupId)
        REFERENCES ConfigurationGroup (Id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT IGNORE INTO SchemaVersion (VersionNumber, AppliedUtc)
VALUES (1, UTC_TIMESTAMP(6));
