CREATE TABLE IF NOT EXISTS TwCategoryRecoveryOperation (
    OperationId CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    ScopeFingerprint CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    ScopeJson TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    ActorUserId CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
    PolicyVersion VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    OperationStatus VARCHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    TerminalReason VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NULL,
    CommittedPosition INT UNSIGNED NOT NULL,
    CheckpointGeneration BIGINT UNSIGNED NOT NULL,
    LastOutcomeSequence BIGINT UNSIGNED NOT NULL,
    LastAuditSequence BIGINT UNSIGNED NOT NULL,
    SchemaVersion INT UNSIGNED NOT NULL,
    CreatedAtUtc DATETIME(6) NOT NULL,
    UpdatedAtUtc DATETIME(6) NOT NULL,
    CONSTRAINT PK_TwCategoryRecoveryOperation PRIMARY KEY (OperationId),
    CONSTRAINT CK_TwCategoryRecoveryOperation_Position CHECK (CommittedPosition >= 0),
    CONSTRAINT CK_TwCategoryRecoveryOperation_Status CHECK (OperationStatus IN ('Planned', 'Running', 'Interrupted', 'Recoverable', 'Completed', 'PartiallyCompleted', 'Failed', 'Cancelled')),
    INDEX IX_TwCategoryRecoveryOperation_Status (OperationStatus, UpdatedAtUtc),
    INDEX IX_TwCategoryRecoveryOperation_Scope (ScopeFingerprint)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS TwCategoryRecoveryAuditEvent (
    OperationId CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    EventSequence BIGINT UNSIGNED NOT NULL,
    EventId CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    EventKey VARCHAR(192) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    Position INT UNSIGNED NOT NULL,
    AttemptId CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    EventKind VARCHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    Confidence VARCHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    PageId INT UNSIGNED NULL,
    ObservedRevision INT UNSIGNED NULL,
    Outcome VARCHAR(32) CHARACTER SET ascii COLLATE ascii_bin NULL,
    ActorUserId CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
    OccurredAtUtc DATETIME(6) NOT NULL,
    DiagnosticCode VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NULL,
    SchemaVersion INT UNSIGNED NOT NULL,
    CONSTRAINT PK_TwCategoryRecoveryAuditEvent PRIMARY KEY (OperationId, EventSequence),
    CONSTRAINT UQ_TwCategoryRecoveryAuditEvent_EventId UNIQUE (EventId),
    CONSTRAINT UQ_TwCategoryRecoveryAuditEvent_EventKey UNIQUE (OperationId, EventKey),
    CONSTRAINT FK_TwCategoryRecoveryAuditEvent_Operation FOREIGN KEY (OperationId) REFERENCES TwCategoryRecoveryOperation (OperationId) ON DELETE CASCADE,
    INDEX IX_TwCategoryRecoveryAuditEvent_Position (OperationId, Position, EventSequence),
    INDEX IX_TwCategoryRecoveryAuditEvent_Outcome (OperationId, Outcome, EventSequence)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT INTO MySqlCategorySchemaVersion (Domain, Version, AppliedAtUtc)
VALUES ('category-recovery', 2, UTC_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE Version = GREATEST(Version, VALUES(Version)), AppliedAtUtc = VALUES(AppliedAtUtc);
