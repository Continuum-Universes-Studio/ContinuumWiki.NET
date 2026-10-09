CREATE TABLE IF NOT EXISTS MySqlIdentitySchemaVersion (
    Domain VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    Version INT NOT NULL,
    AppliedAtUtc DATETIME(6) NOT NULL,
    CONSTRAINT PK_MySqlIdentitySchemaVersion PRIMARY KEY (Domain)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS AspNetUsers (
    Id VARCHAR(255) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    UserName VARCHAR(256) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL,
    NormalizedUserName VARCHAR(256) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL,
    Email VARCHAR(256) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL,
    NormalizedEmail VARCHAR(256) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL,
    EmailConfirmed BOOLEAN NOT NULL,
    PasswordHash LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL,
    SecurityStamp LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL,
    ConcurrencyStamp LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL,
    PhoneNumber LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL,
    PhoneNumberConfirmed BOOLEAN NOT NULL,
    TwoFactorEnabled BOOLEAN NOT NULL,
    LockoutEnd DATETIME(6) NULL,
    LockoutEnabled BOOLEAN NOT NULL,
    AccessFailedCount INT NOT NULL,
    CONSTRAINT PK_AspNetUsers PRIMARY KEY (Id),
    CONSTRAINT UX_AspNetUsers_NormalizedUserName UNIQUE (NormalizedUserName),
    INDEX IX_AspNetUsers_NormalizedEmail (NormalizedEmail)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS AspNetRoles (
    Id VARCHAR(255) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    Name VARCHAR(256) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL,
    NormalizedName VARCHAR(256) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL,
    ConcurrencyStamp LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL,
    CONSTRAINT PK_AspNetRoles PRIMARY KEY (Id),
    CONSTRAINT UX_AspNetRoles_NormalizedName UNIQUE (NormalizedName)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS AspNetUserClaims (
    Id INT UNSIGNED NOT NULL AUTO_INCREMENT,
    UserId VARCHAR(255) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    ClaimType VARCHAR(256) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL,
    ClaimValue LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL,
    CONSTRAINT PK_AspNetUserClaims PRIMARY KEY (Id),
    CONSTRAINT FK_AspNetUserClaims_User FOREIGN KEY (UserId) REFERENCES AspNetUsers (Id) ON DELETE CASCADE,
    INDEX IX_AspNetUserClaims_UserId (UserId)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS AspNetRoleClaims (
    Id INT UNSIGNED NOT NULL AUTO_INCREMENT,
    RoleId VARCHAR(255) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    ClaimType VARCHAR(256) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL,
    ClaimValue LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL,
    CONSTRAINT PK_AspNetRoleClaims PRIMARY KEY (Id),
    CONSTRAINT FK_AspNetRoleClaims_Role FOREIGN KEY (RoleId) REFERENCES AspNetRoles (Id) ON DELETE CASCADE,
    INDEX IX_AspNetRoleClaims_RoleId (RoleId)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS AspNetUserLogins (
    LoginProvider VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    ProviderKey VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    ProviderDisplayName LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL,
    UserId VARCHAR(255) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    CONSTRAINT PK_AspNetUserLogins PRIMARY KEY (LoginProvider, ProviderKey),
    CONSTRAINT FK_AspNetUserLogins_User FOREIGN KEY (UserId) REFERENCES AspNetUsers (Id) ON DELETE CASCADE,
    INDEX IX_AspNetUserLogins_UserId (UserId)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS AspNetUserRoles (
    UserId VARCHAR(255) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    RoleId VARCHAR(255) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    CONSTRAINT PK_AspNetUserRoles PRIMARY KEY (UserId, RoleId),
    CONSTRAINT FK_AspNetUserRoles_User FOREIGN KEY (UserId) REFERENCES AspNetUsers (Id) ON DELETE CASCADE,
    CONSTRAINT FK_AspNetUserRoles_Role FOREIGN KEY (RoleId) REFERENCES AspNetRoles (Id) ON DELETE CASCADE,
    INDEX IX_AspNetUserRoles_RoleId (RoleId)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS AspNetUserTokens (
    UserId VARCHAR(255) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    LoginProvider VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    Name VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    Value LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL,
    CONSTRAINT PK_AspNetUserTokens PRIMARY KEY (UserId, LoginProvider, Name),
    CONSTRAINT FK_AspNetUserTokens_User FOREIGN KEY (UserId) REFERENCES AspNetUsers (Id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS Profile (
    UserId VARCHAR(255) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    AccountName VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    Navigation VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    Biography TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL,
    Avatar MEDIUMBLOB NULL,
    CreatedDate DATETIME(6) NOT NULL,
    ModifiedDate DATETIME(6) NOT NULL,
    AvatarContentType VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL,
    CONSTRAINT PK_Profile PRIMARY KEY (UserId),
    CONSTRAINT UX_Profile_AccountName UNIQUE (AccountName),
    CONSTRAINT UX_Profile_Navigation UNIQUE (Navigation),
    CONSTRAINT FK_Profile_IdentityUser FOREIGN KEY (UserId) REFERENCES AspNetUsers (Id) ON DELETE CASCADE,
    INDEX IX_Profile_UserId_AccountName (UserId, AccountName)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS Role (
    Id INT UNSIGNED NOT NULL AUTO_INCREMENT,
    Name VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    Description VARCHAR(1000) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL,
    IsBuiltIn BOOLEAN NOT NULL DEFAULT TRUE,
    CONSTRAINT PK_Role PRIMARY KEY (Id),
    CONSTRAINT UX_Role_Name UNIQUE (Name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS PermissionDisposition (
    Id INT UNSIGNED NOT NULL AUTO_INCREMENT,
    Name VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    CONSTRAINT PK_PermissionDisposition PRIMARY KEY (Id),
    CONSTRAINT UX_PermissionDisposition_Name UNIQUE (Name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS Permission (
    Id INT UNSIGNED NOT NULL AUTO_INCREMENT,
    Name VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
    Description VARCHAR(1000) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL,
    CONSTRAINT PK_Permission PRIMARY KEY (Id),
    CONSTRAINT UX_Permission_Name UNIQUE (Name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS AccountRole (
    Id INT UNSIGNED NOT NULL AUTO_INCREMENT,
    UserId VARCHAR(255) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    RoleId INT UNSIGNED NOT NULL,
    CONSTRAINT PK_AccountRole PRIMARY KEY (Id),
    CONSTRAINT UX_AccountRole_UserRole UNIQUE (UserId, RoleId),
    CONSTRAINT FK_AccountRole_Profile FOREIGN KEY (UserId) REFERENCES Profile (UserId) ON DELETE CASCADE,
    CONSTRAINT FK_AccountRole_Role FOREIGN KEY (RoleId) REFERENCES Role (Id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS AccountPermission (
    Id INT UNSIGNED NOT NULL AUTO_INCREMENT,
    UserId VARCHAR(255) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    PermissionId INT UNSIGNED NOT NULL,
    Namespace VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL,
    PageId VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL,
    PermissionDispositionId INT UNSIGNED NOT NULL,
    CONSTRAINT PK_AccountPermission PRIMARY KEY (Id),
    CONSTRAINT UX_AccountPermission_Scope UNIQUE (UserId, PermissionId, Namespace, PageId, PermissionDispositionId),
    CONSTRAINT FK_AccountPermission_Profile FOREIGN KEY (UserId) REFERENCES Profile (UserId) ON DELETE CASCADE,
    CONSTRAINT FK_AccountPermission_Permission FOREIGN KEY (PermissionId) REFERENCES Permission (Id),
    CONSTRAINT FK_AccountPermission_Disposition FOREIGN KEY (PermissionDispositionId) REFERENCES PermissionDisposition (Id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS RolePermission (
    Id INT UNSIGNED NOT NULL AUTO_INCREMENT,
    RoleId INT UNSIGNED NOT NULL,
    PermissionId INT UNSIGNED NOT NULL,
    Namespace VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL,
    PageId VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL,
    PermissionDispositionId INT UNSIGNED NOT NULL,
    CONSTRAINT PK_RolePermission PRIMARY KEY (Id),
    CONSTRAINT UX_RolePermission_Scope UNIQUE (RoleId, PermissionId, Namespace, PageId, PermissionDispositionId),
    CONSTRAINT FK_RolePermission_Role FOREIGN KEY (RoleId) REFERENCES Role (Id) ON DELETE CASCADE,
    CONSTRAINT FK_RolePermission_Permission FOREIGN KEY (PermissionId) REFERENCES Permission (Id),
    CONSTRAINT FK_RolePermission_Disposition FOREIGN KEY (PermissionDispositionId) REFERENCES PermissionDisposition (Id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS AdminPwCheck (
    Id TINYINT UNSIGNED NOT NULL,
    IsChanged BOOLEAN NULL,
    CONSTRAINT PK_AdminPwCheck PRIMARY KEY (Id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT INTO PermissionDisposition (Name) VALUES ('Allow'), ('Deny')
ON DUPLICATE KEY UPDATE Name = VALUES(Name);

INSERT INTO Permission (Name, Description) VALUES
    ('Read', 'User or role can read page or within namespace.'),
    ('Edit', 'User or role can edit unprotected pages.'),
    ('Delete', 'User or role can delete pages.'),
    ('Moderate', 'User or role can moderate protected pages.'),
    ('Create', 'User or role can create pages.')
ON DUPLICATE KEY UPDATE Description = VALUES(Description);

INSERT INTO Role (Name, Description, IsBuiltIn) VALUES
    ('Administrator', 'Administrators can do anything. Add, edit, delete, etc.', TRUE),
    ('Member', 'Read-only user with a profile.', TRUE),
    ('Contributor', 'Contributor can add and edit unprotected pages.', TRUE),
    ('Moderator', 'Moderators can add, edit, and delete pages - including protected pages.', TRUE),
    ('Anonymous', 'Role applied to users who are not logged in.', TRUE)
ON DUPLICATE KEY UPDATE Description = VALUES(Description), IsBuiltIn = VALUES(IsBuiltIn);

INSERT INTO MySqlIdentitySchemaVersion (Domain, Version, AppliedAtUtc)
VALUES ('identity', 1, UTC_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE Version = GREATEST(Version, VALUES(Version)), AppliedAtUtc = VALUES(AppliedAtUtc);
