# MySQL full-integration assessment

CW-M2-005H.0 extends the independently testable MySQL persistence surface with an isolated development runtime. The application remains SQLite by default and rejects MySQL outside Development with `Database:MySql:DevelopmentOnly=true`. Legacy SQLite-shaped consumers are explicitly gated; they do not cause a mixed-provider startup or silent SQLite fallback.

## Coverage matrix

| Domain | SQLite | MySQL | Tests | Remaining issue |
| --- | --- | --- | --- | --- |
| Provider sessions | `SqliteManagedFactory` | Provider-neutral sessions and `MySqlDatabaseConnectionFactory` | Adapter tests | Complete at connection boundary |
| Configuration | `ConfigurationRepository` via neutral SQLite adapter | `MySqlConfigurationRepository` and schema | Group/entry reads, typed flat/nested reads, themes, menus, menu CRUD, and encrypted-value handling | Database metrics and seed/upgrade orchestration remain legacy |
| Pages/revisions/navigation | `PageRepository` | `MySqlPageRepository` and schema | MySQL page integration | Files/comments/search/deleted-store remain |
| Users/Identity | SQLite repositories and EF Identity via neutral adapter | `MySqlApplicationDbContext`, `MySqlUsersRepository` | Profile lookup/update/avatar, roles, permissions, memberships, and effective permission reads; native Identity authentication coverage | Security-admin controller and application-wide provider selection remain gated |
| Roles/permissions | SQLite repository and Identity | MySQL Identity schema/context | Identity integration | Legacy repository contract remains |
| Logging | `LoggingRepository` | `MySqlOperationsRepository` and operations schema | MySQL operations integration | UI repository contract remains |
| Statistics | `StatisticsRepository` | MySQL page-statistics upsert slice | MySQL operations integration | Paged reporting remains |
| Emoji | `EmojiRepository` | MySQL emoji/category upsert slice | MySQL operations integration | Query/reload/cache behavior remains |
| Comments | `PageRepository` | `MySqlPageSupportRepository` and page-support schema | MySQL page-support integration | Public page contract remains SQLite-shaped; runtime identity joins need wiring |
| Files/file revisions | `PageRepository` via SQLite adapter | `MySqlPageSupportRepository` and page-support schema | Core metadata/content/revisions/association/upload/detach/delete plus admin listing/orphan purge covered |
| Search/tokens | `PageRepository` | Page-token schema plus replace/find-token operations | MySQL page-support integration | Full ranking/fuzzy search parity remains |
| Deleted pages/revisions | SQLite sibling databases via neutral adapter | `MySqlPageRepository` soft-delete/restore, deleted-page queries, and version-3 deleted-revision archive tables | Lifecycle and deleted-revision integration | Full application cutover remains gated |
| Defaults/seed/admin/backup | `DefaultsRepository`, `DatabaseManager` | Versioned default-content schema/read repository plus transactional `MySqlDefaultContentSeeder` and `MySqlApplicationMaintenanceRepository` | MySQL maintenance health, future-schema, seed idempotency/customization, page/revision integrity, menu ordering/customization, and unsupported-operation tests | Backup/restore remains unavailable; unrelated legacy maintenance remains |
| Translations | Direct read-only SQLite `TranslationDatabase` | Versioned `Translation` schema, canonical source import, parent-culture fallback, and whitelisted read repository | Fresh/repeat/customized Unicode import and fallback integration coverage | Application startup/localizer wiring remains gated with the provider cutover |
| Plugin-owned storage | Plugin-defined | None | Not applicable | Keep SQLite compatibility until contract versioning |

## Schema, transactions, and security

MySQL schemas use InnoDB, utf8mb4, explicit keys/indexes, and UTC `DATETIME(6)`. `MySqlSchemaInitializer` provides an explicit non-destructive order: configuration, Identity, pages/page-support/deleted revisions, support/default-seed state, then operations. The deleted-revision version-3 migration adds `DeletedPageRevision` and `DeletedPageRevisionAttachment`; it preserves original revision identifiers, content, author/timestamp/hash fields, deletion metadata, and attachment links. The defaults version-2 migration tracks seed source application state. `MySqlDefaultContentSeeder` reads typed records from canonical embedded `TightWiki.Repository/Defaults/defaults.db`, `SeedSources/config.db`, and `SeedSources/Translations.db`, then imports configurations, themes, feature templates, Help/Include/Builtin pages, menu items, and all 932 translation rows with parameterized MySQL statements in one transaction. It uses insert-if-absent behavior, preserves customized configuration values, page bodies, translations, and menu rows, requires an existing Administrator for page authorship, and never runs automatically at application startup. `MySqlTranslationRepository` preserves the existing localizer contract: English falls back to the source phrase, regional cultures walk supported parent cultures, and missing translations return null for the localizer's established English fallback. PageController parser-derived metadata is built before the neutral save; page creation/editing, revisions, current attachment associations, tags/references, processing instructions, and search tokens are committed in one MySQL transaction. Deleted-revision archive, restore, and purge operations lock affected rows and commit related changes together; cache invalidation happens only after successful commit. Identity profile/permission changes, emoji/category upserts, and schema initialization use one MySQL session and transaction where required. No distributed transaction is introduced between SQLite and MySQL. New SQL is parameterized; test credentials are environment-only.

## Runtime and smoke results

The MySQL branch is wired for disposable development startup. On 2026-10-09,
fresh Oracle MySQL 8.4 disposable hosts started the application and exercised
login/logout, admin authorization, page create/edit/display/rename, revision
history/specific revision/compare, stale-revision rejection, Unicode and
internal-link rendering, search, comments, attachment upload/replacement/
download/revisions, theme selection, localization, and explicit backup
rejection over HTTP. The root route's existing redirect completed with HTTP
200. No production data was accessed. Disposable MySQL integration tests
cover only the bounded slices in the matrix; they are not full application
coverage.

The final configured MySQL test run used
`CONTINUUMWIKI_RUN_MYSQL_TESTS=1` and a fresh dedicated connection to the
disposable port 33334: 25 discovered, 25 executed, 25 passed, 0 failed, and
0 skipped. The default suite remained 155 discovered, 136 passed, 19 explicit
MySQL skips, and 0 failed because it was intentionally run without the MySQL
environment variables.

The current verification is 136 passing SQLite tests with 18 explicit MySQL skips, and 25 passing MySQL tests when run against disposable Oracle MySQL 8.4. The published validation artifact contains `TightWiki.dll` and `Translations/Translations.db`; no production data was accessed.

## Provider-neutral contracts and plugin compatibility

`TightWiki.Plugin.Interfaces.Persistence` defines application-facing contracts
without SQLite or MySQL connection types. The existing MySQL repositories
implement those contracts directly, and `AddMySqlApplicationPersistence`
registers them as a provider-specific service graph. The original `ITw*`
interfaces remain unchanged for legacy plugins.

`ITwPluginPersistenceCompatibility` lets a plugin declare supported providers
and whether it still requires legacy SQLite repositories. An undeclared legacy
plugin remains supported in SQLite mode and is rejected with an actionable
diagnostic in MySQL mode. No SQLite fallback is performed.

The MVC and administrative audit is recorded in
`MVC-CONSUMER-MIGRATION.md`. Translation localizer access, profile/account
lookup, session security hydration, email configuration reads, and wiki
configuration group/theme/menu reads, the security-admin role/profile/
permission surface, PageController display/sidebar/comments/templates,
attachment page lookups, page-view statistics, typed display configuration,
save/revision persistence, event-log reporting/detail/purge, and
page-statistics reporting/purge now use neutral contracts with SQLite adapters.
Deleted-revision calls are neutral at the application boundary for both
providers. SQLite delegates to its existing archive databases; MySQL uses the
version-3 archive schema and parameterized Dapper lifecycle operations. Database
status, schema health, maintenance-action dispatch, default-content seed, translation/menu import, and
backup routes now resolve through `ITwApplicationMaintenanceRepository`;
SQLite delegates to the existing manager, while MySQL health/status is
read-only, supported default-content seed import is explicit/idempotent, and
optimize/vacuum and backup/restore return explicit unsupported results.
Unrelated administrative maintenance still requires legacy provider-specific
services, so the startup gate remains enabled.

The page/search/file consumer inventory is maintained in
`MVC-CONSUMER-MIGRATION.md`. Existing neutral MySQL coverage now includes page
CRUD/revisions, exact-token search, autocomplete, tag-page and associated-tag
queries, bounded comments, processing instructions, and tag/reference
replacement, parser-driven derived-metadata replacement, and core attachment
metadata/content/revision/association operations including attachment
administration listing and orphan purge. MySQL fuzzy search uses token-prefix
matching rather than SQLite's Double Metaphone ranking. Deleted-page purge and
unrelated database-manager maintenance remain unsupported for application
consumers; deleted-revision archive/list/retrieve/restore/purge operations use
the version-3 MySQL archive schema.
PageController create/edit/save/rename/revert and parser-derived metadata
are now neutral. Attachment metadata,
content, revision lookup, association, upload/replacement, detach, and
revision deletion are now neutral.
Deleted-page lookup, listing, revision-checked deletion, restoration, and
page-cache invalidation are covered by the neutral page contract; permanent
purge operations remain legacy-only; editor presence now uses the neutral page contract.

The dedicated deleted-revision lifecycle, translation fallback, and menu seed tests and the complete MySQL integration
selection pass against disposable Oracle MySQL 8.4. The normal SQLite suite
remains green; MySQL-specific tests are skipped only when the explicit disposable
test connection is not configured.

## Plugin compatibility and cutover readiness

Existing plugin interfaces are unchanged. A broad static ban on SQLite would be incorrect because SQLite remains supported; active MySQL application mode is limited to the isolated development branch and explicitly rejects unsupported legacy operations. CW-M2-006 is not cutover-ready: it must first rehearse export/import, preserve Identity IDs and revision history, compare row counts/checksums, validate foreign keys, run full-application disposable smoke tests, verify backup/restore, and define rollback. No production cutover was performed here.

## CW-M2-006 verification checklist

The source inventory for the rehearsal must include these current SQLite stores and their owning code:

| Source | Owning area | Required migration evidence |
| --- | --- | --- |
| `Data/config.db` | `ConfigurationRepository`, `DatabaseManager` | Configuration groups/entries, data types, themes, menu items, crypto/version state; row counts and encrypted-value verification |
| `Data/pages.db` | `PageRepository` | Pages, revisions, comments, files/file revisions, attachments, instructions, references, tags, tokens, editors; per-table counts and content hashes |
| `Data/deletedpages.db` | `PageRepository` | Deleted page snapshots and related content; page/revision/file attachment counts and restore rehearsal |
| `Data/deletedpagerevisions.db` | `PageRepository` | Deleted revision rows and bodies; revision-number and checksum preservation |
| `Data/users.db` | `UsersRepository`, EF Identity | Identity IDs, normalized names, password/security stamps, claims, roles, logins/tokens, profiles and custom permissions |
| `Data/logging.db` | `LoggingRepository` | Severity and log counts, timestamps, exception text, retention behavior |
| `Data/statistics.db` | `StatisticsRepository` | Page statistics counts and numeric totals; page foreign-key validation |
| `Data/emoji.db` | `EmojiRepository` | Emoji/category counts and binary image hashes |
| `TightWiki/Translations/Translations.db` | `TranslationDatabase` | English keys and every supported-language value; localizer fallback parity |
| `TightWiki.Repository/Defaults/defaults.db` | `DefaultsRepository` | Default configuration, themes, templates, and wiki-page seed counts/checksums |

The rehearsal should export each source using a read-only SQLite connection, import into a disposable MySQL database through versioned migrations, and record `(table, primary-key projection, row count, SHA-256 of canonical ordered rows)`. Identity and page IDs must be preserved before foreign-key validation. Revision bodies, file bytes, emoji bytes, and encrypted configuration values require byte/content hashes, not only row counts.

Required gates before cutover are: two successful dry runs from untouched source copies; zero orphaned foreign keys; matching counts and canonical checksums; successful admin/password and permission checks; page create/edit/revision/restore/search/comments/files smoke flows; translation fallback checks; backup/restore rehearsal; and a timed rollback rehearsal. Rollback must leave the original SQLite files untouched, stop writes, restore the last verified MySQL snapshot or switch the provider setting back to SQLite, and reconcile any post-cutover write journal. Production cutover is explicitly outside CW-M2-005.

`MDD.md` and `WISHLIST.md` were requested by the objective but are not present in this checkout.
