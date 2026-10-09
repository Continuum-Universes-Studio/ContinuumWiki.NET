# MySQL Identity and users migration

CW-M2-004 adds an independently constructible Oracle MySQL security slice. CW-M2-005F additionally routes application-owned profile, session-security, and bounded permission consumers through `ITwApplicationIdentityRepository`, while preserving the existing ASP.NET Core Identity model and the public SQLite-shaped `ITwUsersRepository` contract. It does not perform production account migration or enable an unsafe mixed-provider application.

## Provider choice

The application targets .NET 10 and EF Core/ASP.NET Core Identity 10.0.12. `MySql.EntityFrameworkCore` 10.0.9 is referenced by the library, web, and test projects; its net10.0 assembly exposes the official `UseMySQL` EF Core integration. `MySqlIdentityServiceCollectionExtensions.AddMySqlIdentity` is the explicit registration seam for a future coordinated provider plan. The Dapper repositories continue using `MySqlConnector 2.6.2`. These providers have separate responsibilities and do not share DbContext or connection ownership. The package metadata reports GPL-2.0-only with the Universal-FOSS-exception; licensing must be approved before production adoption.

The current application still rejects `Database:Provider=mysql` in `DatabaseProviderConfiguration`: page lifecycle, logging, statistics, emoji, defaults, files, comments, search, the security-admin controller, and legacy plugin repositories remain SQLite-only. This explicit gate prevents MySQL Identity from being combined silently with SQLite user or content repositories. A later coordinated cutover can register `MySqlApplicationDbContext` with `UseMySQL` in the application startup path.

## Schema

`TightWiki.Repository/Scripts/MySql/Identity/V1__IdentityAndUsersSchema.sql` is a repeatable, non-destructive version-one initializer. It creates InnoDB/utf8mb4 tables for:

- `AspNetUsers`, `AspNetRoles`, `AspNetUserRoles`, `AspNetUserClaims`, `AspNetRoleClaims`, `AspNetUserLogins`, and `AspNetUserTokens`;
- TightWiki `Profile` rows with a foreign key to `AspNetUsers`;
- custom `Role`, `Permission`, `PermissionDisposition`, `AccountRole`, `AccountPermission`, `RolePermission`, and `AdminPwCheck` tables.

Identity IDs remain string values so existing IDs can be imported unchanged; the current application uses GUID-formatted IDs. Normalized username and role-name uniqueness is retained. Profile account names and navigation identifiers are unique and Unicode-capable. Foreign keys cascade account-owned claims, logins, tokens, profiles, and custom permissions; permission scope intentionally remains a string page identifier because the page repository is a separate migration boundary.

`MySqlIdentitySchemaInitializer` is explicit and test-controlled. It is not called from application startup and does not drop, truncate, or reset tables.

## Behavior and coverage

`MySqlApplicationDbContext` remains an `IdentityDbContext<IdentityUser>`, so `UserManager`, `SignInManager`, `RoleManager`, default token providers, password hashing, security stamps, lockout, claims, external-login mappings, and Identity concurrency stamps retain framework semantics. No custom password hashing or plaintext password storage is introduced.

`MySqlUsersRepository` is the bounded Dapper slice for profiles and TightWiki custom authorization data. It supports profile creation/read/update/avatar storage, custom role and permission lookup, account-role membership, account/role permission assignment, and effective permission resolution. It deliberately does not implement the legacy SQLite-shaped interface or read SQLite when used with MySQL.

The application-owned profile/account consumers and session security hydration now
use the neutral repository. `UserManager`, `SignInManager`, the Identity EF
stores, password hashing, security stamps, lockout, claims, external-login
records, and cookie authentication remain native ASP.NET Core Identity. No
custom authentication or password verification path was introduced.

The opt-in integration tests cover password hash compatibility through `UserManager`, valid/invalid password checks, reset-token verification, security-stamp changes, lockout, claims, external login rows, Identity role membership, Unicode profile data, custom roles/permissions, duplicate usernames, optimistic Identity concurrency, foreign-key relationships, parameterized values, and profile uniqueness/rollback behavior. The existing Identity username policy is characterized: the default validator rejects non-ASCII usernames; Unicode profile values remain supported.

## Authentication and authorization compatibility

No routes, Razor pages, cookie names, cookie paths, external-provider credentials, callbacks, or authorization checks were changed. Existing Google, Microsoft, and OIDC registration remains in `Program.cs`; their linked-login records are represented by standard `AspNetUserLogins` when the MySQL context is used. The existing password policy and token-provider configuration remain owned by ASP.NET Identity. `AdminSecurityController` now uses neutral configuration and identity contracts for role, membership, permission, profile, autocomplete, and administrative account operations; the startup gate remains enabled for remaining page-lifecycle and database-manager dependencies.

## Migration and rollback limits

This milestone provides schema and testable persistence, not account export/import. Production accounts, password hashes, cookies, data-protection keys, and external-provider configuration are untouched. A future cutover must rehearse row counts, IDs, normalized uniqueness, hashes, role/claim/login/token relationships, profile foreign keys, and rollback on a dedicated copy before selecting MySQL in production.

## Test configuration

Set `CONTINUUMWIKI_RUN_MYSQL_TESTS=1` and `CONTINUUMWIKI_MYSQL_TEST_CONNECTION` to a dedicated disposable Oracle MySQL database. Credentials are environment-only. Without both values, MySQL tests are skipped and reported separately from the SQLite suite.
