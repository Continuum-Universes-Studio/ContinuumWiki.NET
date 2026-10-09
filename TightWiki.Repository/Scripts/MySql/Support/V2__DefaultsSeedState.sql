CREATE TABLE IF NOT EXISTS MySqlSeedState (
    SeedKey VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    SourceVersion VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    AppliedAtUtc DATETIME(6) NOT NULL,
    CONSTRAINT PK_MySqlSeedState PRIMARY KEY (SeedKey)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS MySqlDefaultsSchemaVersion (
    Domain VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    Version INT NOT NULL,
    AppliedAtUtc DATETIME(6) NOT NULL,
    CONSTRAINT PK_MySqlDefaultsSchemaVersion PRIMARY KEY (Domain)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT INTO MySqlDefaultsSchemaVersion (Domain, Version, AppliedAtUtc)
VALUES ('defaults', 2, UTC_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE Version = GREATEST(Version, VALUES(Version)), AppliedAtUtc = VALUES(AppliedAtUtc);
