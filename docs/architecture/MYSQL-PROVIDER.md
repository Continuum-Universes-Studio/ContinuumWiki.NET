# MySQL provider foundation

CW-M2-002 through CW-M2-005 add independently testable Oracle MySQL configuration, page-domain, Identity/security, and operations slices. CW-M2-005H.0 wires those slices for an isolated development-only runtime; SQLite remains the default and production provider.

## Provider architecture

`MySqlDatabaseConnectionFactory` implements the provider-neutral `IDatabaseConnectionFactory` from `TightWiki.Library.Database` using `MySqlConnector 2.6.2`. It creates pooled `MySqlConnection` instances asynchronously and exposes them only through `DbConnection`/`DbTransaction` contracts. Sessions and transactions own and dispose their resources explicitly.

`MySqlConfigurationRepository` is the bounded configuration slice. It uses Dapper and parameterized SQL for group lookup, group creation, and entry upsert. It is wired through the provider-neutral graph in the isolated development runtime.

`MySqlPageRepository` is the bounded page slice. It uses the same provider-neutral connection/session/transaction contracts, Dapper, and parameterized SQL. It supports page/revision CRUD, historical navigation, optimistic concurrency, soft-delete/restore, tags, and outbound references. It is independently constructible and is not wired into the application while Identity, files, search, statistics, and the legacy SQLite page contract remain.

`MySqlApplicationDbContext` is an EF Core 10 Identity context backed by the official `MySql.EntityFrameworkCore` 10.0.9 provider. `MySqlIdentityServiceCollectionExtensions.AddMySqlIdentity` is the registration seam. `MySqlUsersRepository` provides the Dapper profile/custom-permission slice. These services are selected only by the isolated development runtime; production activation remains gated.

`MySqlOperationsSchemaInitializer` and `MySqlOperationsRepository` add provider-neutral logging, page-statistics upsert, and emoji/category persistence. The provider-neutral application contracts in `TightWiki.Plugin.Interfaces.Persistence` are now registered through `AddMySqlApplicationPersistence`; the legacy SQLite-shaped plugin interfaces remain separately available for SQLite compatibility.

Selecting `Database:Provider=mysql` outside the explicitly gated development runtime still fails during startup. Within development, unresolved legacy consumers receive explicit unsupported-operation gates; no SQLite repositories are substituted. The registration seam remains available for incremental consumer migration.

## Schema and initialization

The reviewed schemas are `TightWiki.Repository/Scripts/MySql/ConfigurationSchema.sql`, `TightWiki.Repository/Scripts/MySql/Pages/V1__PageSchema.sql`, and `TightWiki.Repository/Scripts/MySql/Identity/V1__IdentityAndUsersSchema.sql`. They use InnoDB, UTF8MB4, explicit primary/unique/foreign keys, indexes, boolean flags, and UTC `DATETIME(6)` timestamps. Their initializers execute repeatable non-destructive statements inside transactions and record domain schema version 1.

The initializer is an explicit disposable-database operation; it is not called during application startup and does not perform destructive migrations. Future versions must check the existing `SchemaVersion` state and fail clearly when the state is unexpected.

## Configuration and tests

Normal application configuration continues to use `Database:Provider=sqlite`. MySQL integration tests require both:

```bash
export CONTINUUMWIKI_RUN_MYSQL_TESTS=1
export CONTINUUMWIKI_MYSQL_TEST_CONNECTION='Server=...;Database=...;User ID=...;Password=...'
```

The database must be explicitly disposable and dedicated to the test run. These variables are never committed. Without them, the integration test is skipped and must be reported separately from SQLite results.

## Supported and unsupported functionality

Supported in this foundation:

- MySqlConnector pooled asynchronous connection creation.
- Provider-neutral session and transaction lifecycle.
- Configuration group creation, Unicode entry upsert, and group lookup.
- Repeatable configuration schema initialization.
- Page create/retrieve/edit, immutable revision history, specific revision retrieval, rename/navigation uniqueness, soft-delete/restore, tags, outbound references, and optimistic concurrency.
- Identity password hashing/verification through `UserManager`, reset tokens, security stamps, lockout, claims, roles, external-login mappings, profiles, custom roles/permissions, and Identity concurrency behavior.

Not yet supported:

- Application-wide MySQL provider selection.
- Full application provider cutover, production account migration, deleted-page stores, fuzzy/ranked search, attachment association/query, paged statistics, cache invalidation, translation localizer wiring, and remaining admin/backup paths on MySQL. Canonical shipped-default, menu, and translation import now have bounded transactional coverage; logging, basic statistics upserts, emoji/category upserts, page-support CRUD, exact-token operations, defaults tables, and translation lookup remain bounded rather than application-wired.
- SQLite cross-database `ATTACH` behavior on MySQL.
- Production migrations or data export.

## Security and rollback

Never commit a credential-bearing connection string or production export. Use a secret store or environment variable for test credentials. The current application cannot start in a mixed MySQL/SQLite mode. To roll back this foundation, remove the unused MySQL adapter/schema package and retain `Database:Provider=sqlite`; no production data or SQLite schema is changed by these files.

## Compatibility differences

The MySQL schema uses InnoDB foreign keys and explicit UTF8MB4 collation rather than SQLite's `COLLATE NOCASE` and file-level foreign-key behavior. MySQL identifier, Unicode comparison, `BOOLEAN`, auto-increment, transaction isolation, and timestamp semantics require parity tests before any application repository is switched.

## M2-005 result and recommended M2-006

The remaining source inventory and bounded operations slice are documented in `MYSQL-FULL-INTEGRATION.md`. Keep application startup SQLite-only until the whole repository graph can use one provider without silent cross-provider reads. M2-006 should be a migration rehearsal and cutover-readiness milestone, not an immediate production switch.
