# Persistence architecture

CW-M2-001B completes the first provider-neutral connection/session boundary without changing SQLite compatibility. CW-M2-002 through CW-M2-005 add independently testable MySQL configuration, page/revision/navigation, Identity, and operations slices. CW-M2-005H.0 adds an explicitly development-only MySQL runtime branch; SQLite remains the default and production provider.

## M2-001 validation status

The provider-neutral boundary is now implemented in `TightWiki.Library.Database`: `IDatabaseConnectionFactory`, `IDatabaseSession`, and `IDatabaseTransaction` use ADO.NET `DbConnection`/`DbTransaction` and asynchronous ownership methods. `TightWiki.Repository.Helpers.SqliteDatabaseConnectionFactory` is the active SQLite adapter. The legacy SQLite-specific plugin members remain intentionally preserved for compatibility.

## Current architecture

`TightWiki/Program.cs` constructs `DatabaseManager`, which owns separate SQLite databases for configuration, defaults, pages, deleted pages, deleted revisions, users, logging, emoji, and statistics. Repositories use Dapper through `NTDLS.SqliteDapperWrapper.SqliteManagedFactory`; ASP.NET Core Identity uses a separate EF Core SQLite `ApplicationDbContext`. Translation data is opened directly as a read-only SQLite database.

The database manager applies embedded, versioned SQLite SQL scripts and seeds defaults. Repositories retain their existing asynchronous domain methods and cross-database attachment behavior. SQLite-specific SQL includes `ATTACH DATABASE`, SQLite conflict syntax, pragmas, `AUTOINCREMENT`, and SQLite schema/inspection queries.

## Provider boundary

`TightWiki.Library.Database.DatabaseProviderConfiguration` is the provider-selection boundary. `Database:Provider` defaults to `sqlite`, and the active application validates it before constructing repositories. `mysql` is recognized but still fails clearly because the application repositories are not migrated as a whole; unknown values also fail rather than silently falling back to SQLite. `MySqlDatabaseConnectionFactory`, the bounded MySQL configuration/page/users/operations/maintenance slices, and `MySqlApplicationDbContext` are independently testable but are not application-wired. The page, Identity, and full-integration status are documented in `MYSQL-PAGE-DOMAIN.md`, `MYSQL-IDENTITY-MIGRATION.md`, and `MYSQL-FULL-INTEGRATION.md`; none replaces the legacy SQLite-shaped plugin contracts.

This is intentionally a small boundary rather than a parallel ORM or generic repository framework. Existing repository contracts still expose `SqliteManagedFactory` and selected `SqliteManagedInstance` parameters because those are public plugin contracts. Replacing them mechanically would break compiled plugins and would not remove SQLite-specific SQL from the implementation. The provider-neutral application contracts in `TightWiki.Plugin.Interfaces.Persistence` now cover the implemented configuration, page, page-support, identity, operations, defaults, and translation slices; their MySQL implementations are registered by `AddMySqlApplicationPersistence`. Profile/account lookup, session security hydration, email configuration reads, wiki configuration group/theme/menu reads, the base-controller page/instruction/tag/permission reads, and the security-admin role/profile/permission surface now use those neutral services. PageController display/sidebar, comments, templates, search-token display, create/edit/save/rename/revert, and revision history use neutral page/page-support contracts. Reader-facing page-view throttling uses the neutral operations contract, and typed display settings use neutral configuration reads. Parser-derived metadata is built before the save, then page/revision, current attachments, tags, references, processing instructions, and search tokens are committed together by each provider adapter; cache invalidation follows the commit. Administrative event-log and page-statistics reporting/purge now use the neutral operations contract with SQLite and MySQL implementations. Deleted-revision listing, inspection, archive, purge, and restoration now use the neutral page contract; SQLite delegates to its existing archival stores, while MySQL uses the version-3 archive schema with transactional row-locking. SQLite database-file maintenance, seed orchestration, backup/archive/purge, and remaining page-maintenance reporting remain explicit provider-specific services behind the MySQL startup gate.

## SQLite adapter responsibilities and lifecycle

MySQL default initialization reads embedded canonical `Data/config.db` menu
records and `TightWiki/Translations/Translations.db` translation records through
the explicit maintenance seed boundary. These artifacts are seed input only;
they are never used as runtime SQLite fallbacks.

The current wrapper remains the SQLite adapter. `SqliteManagedFactory` creates short-lived managed instances through `Ephemeral`/`EphemeralAsync`; the wrapper owns opening and disposal of those connections. Repository methods that need multiple statements create a transaction from the active managed instance and commit or roll back within that ownership scope. `RepositoryHelpers.AttachSiblingDatabase` attaches a sibling file to the active connection and returns a disposable detach scope.

The adapter owns the `SqliteManagedInstance` and its native connection for the lifetime of `IDatabaseSession`; disposing the session closes the connection. `IDatabaseTransaction` owns and disposes its transaction, while Dapper commands receive the underlying transaction explicitly. Commit, rollback, cancellation, and connection-disposal behavior are covered by isolated `DatabaseAdapterTests`. Existing SQLite-only admin and attachment APIs remain behind the legacy plugin contract until an adapter-compatible replacement exists.

## Boundary compatibility matrix

| Repository/consumer | Provider-neutral boundary | Legacy SQLite contract | Status |
| --- | --- | --- | --- |
| ConfigurationRepository group lookup | `IDatabaseConnectionFactory` / `IDatabaseSession` | `ConfigFactory` remains public | Implemented incrementally |
| ConfigurationRepository other operations | Not yet | `SqliteManagedFactory` | Legacy path |
| Pages, users, logging, emoji, statistics, defaults | `ITwApplication*` contracts for implemented MySQL slices | `SqliteManagedFactory`; selected page methods use `SqliteManagedInstance` | Search/autocomplete/tag, deleted-page lifecycle, and core attachment consumers migrated; metadata/admin remainder pending |
| DatabaseManager admin/initialization | `ITwApplicationMaintenanceRepository` for status/actions/backups/seed dispatch | SQLite factories and scripts | Provider-aware boundary; MySQL health/schema status and supported default-content import are implemented; backup/restore remains explicitly unavailable |
| Compiled plugins | Not applicable | Existing `ITw*` signatures unchanged | Compatibility preserved |

## Identity and authentication

ASP.NET Core Identity uses SQLite in the default branch and `MySqlApplicationDbContext` in the isolated development MySQL branch. Password, role, claim, confirmation, external-login, cookie, and security-stamp behavior remain owned by ASP.NET Core Identity. See `MYSQL-RUNTIME-READINESS.md` for the activation boundary and production prerequisites.

## Data grouping and migration sequence

The target should use one MySQL database with explicit table grouping and foreign-key planning rather than pretending SQLite's multiple files are independent schemas:

| Current area | Future MySQL grouping | Migration note |
| --- | --- | --- |
| Pages, revisions, deleted pages, files | Content tables | CW-M2-003 covers page/revision/navigation plus bounded tags/references; files and deleted-store export/import remain. |
| Users, profiles, roles, permissions, Identity | Identity/security tables | CW-M2-004 covers a bounded schema/context/profile/permission slice; production export/import and application cutover remain. |
| Configuration, menu, themes | Configuration tables | Migrate seed/default ordering and encrypted values carefully. |
| Logging, statistics, emoji | Operations/support tables | Preserve timestamps, retention, and binary/image metadata. |
| Translation and defaults | Support/seed tables or versioned artifacts | Keep translation read-only lookup semantics, parent-culture fallback, and seed reproducibility. |

The migration order should begin with characterization and an export/import rehearsal, then one bounded subsystem at a time. SQLite remains the read-only oracle until row counts, checksums, foreign keys, attachment hashes, and rollback are proven.

## Plugin compatibility

`ITwDatabaseManager`, `ITwPageRepository`, `ITwUsersRepository`, `ITwConfigurationRepository`, `ITwLoggingRepository`, `ITwEmojiRepository`, `ITwStatisticsRepository`, and `ITwDefaultsRepository` expose SQLite factories; page methods also expose SQLite managed instances. These contracts are preserved in this milestone. Plugins continue to load through the existing convention-based loader and continue to receive the same repository interfaces.

New provider selection is additive and does not change plugin loading, routes, domain models, or legacy repository method signatures. `ITwPluginPersistenceCompatibility` gives plugins an explicit provider declaration. Undeclared legacy plugins remain accepted in SQLite mode and are rejected in MySQL mode; there is no implicit SQLite fallback.

## Risks and limitations

- SQLite SQL and file attachments cannot be translated mechanically to MySQL.
- Deleted-page lookup/listing, revision-checked deletion, restoration, and page-cache invalidation are implemented in the neutral page contract for SQLite and MySQL. MySQL retains rows and immutable revisions with `IsDeleted`; SQLite retains its existing deleted-page databases. Deleted revisions use the version-3 MySQL archive tables and preserve revision bodies, authors, timestamps, hashes, and attachment associations.
- Attachment metadata, binary content, page-revision association, revision history, upload/replacement, detach, and revision deletion are implemented in the neutral page-support contract. Both providers retain binary content in their existing page-file revision tables; no external filesystem storage was introduced.
- SQLite's case/Unicode behavior, paging, collations, and conflict semantics need characterization before migration.
- Cross-file `ATTACH` queries require an explicit MySQL replacement strategy.
- Identity and repository security data have separate current implementations that must be cut over together. Authentication remains owned by ASP.NET Core Identity; neutral profile and permission services do not replace `UserManager` or `SignInManager`.
- No MySQL provider dependency, connection string, schema, migration, or startup path is included in CW-M2-001B.
- CW-M2-005H.0 does not migrate production accounts or enable production MySQL while remaining repositories, backup/recovery, and direct SQLite compatibility paths are incomplete; it must not be presented as a completed provider cutover.

## Recommended CW-M2-005

M2-005 should complete the remaining provider-coupled repositories and administrative paths: logging UI lifecycle, full statistics/emoji consumers, remaining files/comments/search consumers, backup/recovery, and startup/provider wiring. Build on the existing provider-neutral adapters, preserve the public SQLite plugin contracts, and only then plan a coordinated application-wide MySQL mode and production data rehearsal.
