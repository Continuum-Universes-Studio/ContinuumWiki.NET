# CW-M5-014 — Authorized Category Persistence

## Classification

`CATEGORY_PERSISTENCE_SOURCE_IMPLEMENTED_AUTHORIZATION_BLOCKED`

Track A is implemented and source-tested. Track B remains
`DATABASE_AUTHORIZATION_NOT_ESTABLISHED`: no disposable connection, schema
provisioning, migration, or category database mutation was performed during
this milestone.

## Provider boundary

SQLite remains the default application provider. The existing MySQL path uses
`MySqlConnector` 2.6.2, Dapper, and the provider-neutral
`IDatabaseConnectionFactory`/`IDatabaseSession` transaction boundary. The
runtime readiness guard requires MariaDB 11.8.x. The existing MySQL startup
path still registers `RejectingTwCategoryMembershipRepository`; this
milestone does not replace it.

The category schema initializer is separate from `MySqlSchemaInitializer` and
is never called by application startup. An operator or gated integration test
must explicitly call `MySqlCategorySchemaInitializer` after target approval.
`MySqlCategoryPersistenceReadiness` performs read-only MariaDB, schema-version,
table, and column checks and returns a readiness proof before an explicitly
activated provider is used.

## Schema

The embedded migration
`MySql.Category.V1__CategorySchema.sql` creates two tables:

- `PageCategoryIndexState` has one row per indexed page, including the
  authoritative indexed page revision, durable state, generation, source
  provenance, and UTC timestamp. It exists independently of membership rows,
  so a verified empty category set is represented explicitly.
- `PageCategoryMembership` stores canonical category navigation, bounded sort
  keys, source revision/provenance, generation, timestamp, and a verified-row
  marker. Its primary key prevents duplicate page/category memberships. The
  category-sort and page-revision indexes support bounded deterministic reads.

Both tables reference the existing `Page` identity with `ON DELETE CASCADE`.
No `PageTag` rows are used and no second category-page entity is introduced.

## Repository and transaction contract

`MySqlCategoryMembershipRepository` implements the existing typed
`ITwCategoryMembershipRepository` boundary. Replacement:

1. validates page, revision, provenance, completeness, generation, and bounds;
2. opens one session and transaction;
3. locks the authoritative `Page` row and rejects deleted, missing, or stale
   revisions;
4. locks the page's index-state row when present and rejects a newer indexed
   revision;
5. compares a verified same-revision set for idempotent `NoChange`;
6. deletes and reinserts memberships inside the same transaction;
7. upserts the independent `Verified` index-state row; and
8. commits all changes together, rolling back on cancellation or failure.

An empty desired set still writes a verified index-state row. Incomplete
extraction is rejected before mutation. Database constraint failures after a
partial replacement are returned as `PersistenceFailure` after rollback.
Revision identifiers are compared to the authoritative `Page.Revision`; no
ordering is inferred from opaque MediaWiki identifiers.

The application service now consults durable consistency read-back before
using an existing membership read to classify `Current`, and reads it again
after an applied replacement. A membership read alone cannot make an
`Incomplete`, `Stale`, `Unindexed`, or `Unknown` durable index appear current.

## Consistency read-back

`GetIndexConsistencyAsync` compares `Page.Revision` with the independent state
row and maps stored evidence to `Current`, `Stale`, `Unindexed`, `Incomplete`,
`Failed`, or `Unknown`. Missing infrastructure still propagates as unavailable
through the existing fail-closed boundary. Stored source page/revision/wiki
identity, generation, and timestamp are returned only through the typed
authorized repository boundary; member listing projections do not expose
source revision metadata.

## Visibility-safe member queries

`GetMembersAsync` requires a canonical category identity and bounded page size.
The SQL query:

- joins only active pages and `Verified` index state matching the membership
  source revision;
- evaluates the existing `Read` permission model in SQL for the anonymous or
  supplied user, including page, namespace, wildcard, deny-before-allow, and
  administrator behavior;
- applies visibility before `LIMIT`/`OFFSET`;
- orders by null placement, binary sort key, and stable page ID; and
- returns only authorized page projections plus `HasMore`, with no total count
  or source revision field.

If the required permission tables are unavailable, the query fails rather than
falling back to unfiltered members. Category membership is never treated as
page-read authorization.

## Activation and authorization

`AddAuthorizedMySqlCategoryPersistence` is an explicit opt-in extension that
requires the `MySqlCategoryPersistenceReadinessProof` returned by the
readiness check. It also requires `Database:Provider=mysql`,
`Database:CategoryPersistence:Enabled=true`, schema version `1`, and an
existing provider connection-factory registration. No startup path calls it;
the rejecting repository remains the production and development default.

The integration-test gate additionally requires:

- `CONTINUUMWIKI_RUN_MYSQL_TESTS=1`;
- `CONTINUUMWIKI_CATEGORY_PERSISTENCE_AUTHORIZED=1`;
- an approval scope path;
- a target database matching `^cw_m5_014_[A-Za-z0-9_-]+$`; and
- a connection string whose database matches that target.

The test suite remains skipped unless all inputs are present. Credentials are
not written to source, reports, or diagnostics.

## Verification

Source-only focused tests passed, including provider activation safety,
consistency-readback behavior, incomplete-index protection, revision handling,
and existing category application tests. The new MariaDB integration tests
cover empty verified indexes, idempotent replay, stale revision rejection,
transaction rollback after an insert failure, and visibility-safe pagination
with a page-specific deny. They were skipped because authorization was not
established; they did not open a connection.

No production database, schema, service, worker, Lua, Cargo, or security
setting was changed. This milestone does not certify MariaDB execution,
transaction behavior, concurrent CAS, or production activation.
