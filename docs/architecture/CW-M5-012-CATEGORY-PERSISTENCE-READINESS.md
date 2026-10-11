# CW-M5-012 — Category Persistence Readiness

## Classification

`CATEGORY_PERSISTENCE_PREPARED_AUTHORIZATION_BLOCKED`

The persistence design, typed repository boundary, source-only replacement
policy, and explicit authorization gate are prepared. No category schema,
provider implementation, database connection, or database mutation was
authorized or executed.

## Actual persistence architecture

The application has two deliberately separate persistence paths:

- SQLite remains the default application provider.
- The provider-neutral application contracts are backed by a bounded MySQL
  adapter selected only by the isolated development runtime.

The MySQL adapter uses `MySqlConnector 2.6.2` through
`MySqlDatabaseConnectionFactory`, Dapper provider-neutral sessions, and
transactions. ASP.NET Identity uses `MySql.EntityFrameworkCore 10.0.9`.
Page persistence is represented by `Page`, `PageRevision`, `PageTag`,
`PageReference`, and related support tables. Page creation and revision update
already use one transaction and optimistic revision checks.

The runtime guard in `MySqlRuntimeReadiness.ValidateServerAsync` requires a
MariaDB server whose version is `11.8.x`; it rejects non-MariaDB servers and
other MariaDB versions. This is stricter than the generic `MySqlFact` test
attribute, which currently checks only explicit test opt-in and a connection
string. A green direct repository test against another MySQL-compatible engine
must not be promoted to application runtime readiness.

`MySqlSchemaInitializer` is explicit and non-destructive. It validates schema
versions before initializing configuration, identity, pages, operations,
translations, and defaults. No category schema version exists.

## Proposed smallest schema

No migration was created. The design uses the existing `Page` row as the page
identity, including for an optional `Category::title` page. It does not create
a second category-page entity.

### `PageCategoryIndexState`

One row per indexed source page:

| Field | Proposed type/constraint | Purpose |
|---|---|---|
| `PageId` | `INT UNSIGNED`, PK/FK `Page(Id)` | Source page identity; cascade on permanent page deletion |
| `IndexedPageRevision` | `INT UNSIGNED` | Application revision whose source was evaluated |
| `IndexState` | bounded numeric state | `Verified`, `Incomplete`, or `Unknown` |
| `IndexGeneration` | `CHAR(36)` ASCII binary | One replacement generation |
| `SourcePageId` | bounded UTF-8 string, nullable | Original MediaWiki source identity when imported |
| `SourceRevisionId` | bounded UTF-8 string, nullable | Original source revision identity |
| `SourceWikiIdentity` | bounded UTF-8 string, nullable | Source wiki provenance |
| `IndexedAtUtc` | `DATETIME(6)` UTC | Audit timestamp |

### `PageCategoryMembership`

Verified membership rows only:

| Field | Proposed type/constraint | Purpose |
|---|---|---|
| `PageId` | `INT UNSIGNED`, PK/FK `Page(Id)` | Source page |
| `CategoryNavigation` | `VARCHAR(128)` UTF-8, PK component | Canonical `category::title` identity |
| `SortKey` | `VARCHAR(512)` UTF-8 binary collation, nullable | Preserved category sort key |
| `SourcePageRevision` | `INT UNSIGNED` | Revision that produced the row |
| `SourcePageId` | bounded UTF-8 string, nullable | Historical source provenance |
| `SourceRevisionId` | bounded UTF-8 string, nullable | Historical source provenance |
| `SourceWikiIdentity` | bounded UTF-8 string, nullable | Historical source provenance |
| `IndexGeneration` | `CHAR(36)` ASCII binary | Replacement generation |
| `IndexedAtUtc` | `DATETIME(6)` UTC | Audit timestamp |

Constraints and indexes:

- primary key `(PageId, CategoryNavigation)` prevents duplicate active
  memberships;
- foreign key to `Page(Id)` uses `ON DELETE CASCADE` for permanent page
  deletion;
- lookup index `(CategoryNavigation, SortKey, PageId)` supports deterministic
  category ordering and bounded page retrieval;
- lookup index `(PageId, SourcePageRevision)` supports page lifecycle checks;
- existing UTF-8/UTC conventions remain in force;
- `CategoryNavigation` is canonicalized by `TwNamespaceNavigation`, not a new
  normalization algorithm;
- no foreign key to a Category page is required because a category may have
  membership without an explicitly created category-page row.

Retaining old membership rows while `PageCategoryIndexState` is `Incomplete`
or `Unknown` permits recovery without falsely presenting stale rows as the
current verified index. Member queries must require `Verified` state.

## Typed contracts

Preparation-only types were added to `TightWiki.Plugin`:

- `TwCategoryIdentity` — canonical namespace identity;
- `TwCategoryMembershipRecord` — page/revision/provenance membership;
- `TwCategoryMembershipReplacementRequest` and typed dispositions;
- `TwCategoryMemberQuery` and bounded `TwCategoryMemberPage`; and
- `ITwCategoryMembershipRepository`.

The contract has no durable SQLite/MySQL implementation. The later CW-M5-013A
source-only seam registers an explicit rejecting adapter, never a live
provider, so the contract remains fail-closed in both application DI paths.
The mutation result distinguishes `Applied`, `NoChange`,
`IncompleteExtraction`, `RevisionConflict`, `AuthorizationRejected`,
`PersistenceFailure`, and `InvalidRequest`.

The member result contains only authorized page projections and `HasMore`; it
does not expose an unfiltered total count. This avoids count disclosure until
the repository can apply the existing page visibility policy inside the query.

## Transactional replacement

The future authorized mutation service must execute the following in one
transaction:

1. validate mutation authorization;
2. lock/read the authoritative `Page.Revision`;
3. reject a stale `ExpectedPageRevision`;
4. reject incomplete extraction without deleting existing memberships;
5. calculate additions, removals, and sort-key changes;
6. replace `PageCategoryMembership` rows;
7. set `PageCategoryIndexState` to `Verified` with source provenance;
8. commit, or roll back all changes on error/cancellation.

Reapplying the same verified revision and semantic membership set returns
`NoChange`. A job for an older revision returns `RevisionConflict` and cannot
overwrite a newer verified index. Authorization and persistence failures do
not become an empty category set.

## Completeness and provenance

`MediaWikiCategoryIndexPlanner` remains the extraction boundary. It marks a
plan incomplete for missing/restricted/unavailable/ambiguous templates,
unsupported or rejected Lua/extensions, recursive expansion, and resource
limits. Sort keys beyond the proposed 512-character storage bound also block
replacement. Incomplete results preserve old membership and must mark the
page index `Incomplete` or `Unknown`, not `Verified`.

Directives reached through resolved template expansion and selected supported
conditional branches are eligible. Unselected branches, unavailable Lua,
missing Cargo, unsupported extensions, and missing historical source bodies are
not reconstructed or fabricated. Historical rendering remains read-only.

## Page lifecycle integration

The existing safe insertion point is the authorized page-save/import command
boundary, alongside the current page/revision transaction—not inside the
renderer. The future integration must cover:

- create: index the published revision only after complete extraction;
- revision publication: replace using optimistic revision checks;
- move: preserve `PageId`, reindex the published body, and do not infer old
  category state from a historical read;
- deletion: cascade durable rows, with no disclosure from listings;
- restoration: require a fresh authorized reindex of the restored current
  body; and
- controlled import: retain original source IDs/revisions and only replace
  when the imported plan is complete.

No global event bus or renderer side effect is required. At the time of this
readiness milestone the page repository API did not carry category plans; the
later CW-M5-013A milestone defines a post-commit application-service seam
without claiming durable page/category atomicity.

## Visibility-aware query and MVC plan

The repository query must canonicalize the category, join only verified index
state and active pages, apply the existing page `Read` visibility policy in
the database/provider boundary, order by binary sort key then stable page ID,
and return at most 100 members. Filtering restricted pages after an
unbounded query is prohibited. Pagination must be applied after the visibility
predicate; opaque page IDs must not be used as client-visible cursors.

No total count is exposed by the prepared contract. A future count requires a
separately reviewed visibility-safe policy.

Category-page MVC should reuse existing canonical page navigation and safe
HTML encoding. It may use an existing `Category::title` Page row when present,
but must provide explicit missing/empty states and the same read authorization.
The existing `TagsController` is not a category route and is not reused.

## Authorization and disposable execution gate

Current status is `AUTHORIZATION_NOT_ESTABLISHED`.

No environment variable, installed server, cached image, previous disposable
run, or connection string is authorization. Before any future database
mutation, an operator must provide all of:

- `CONTINUUMWIKI_RUN_MYSQL_TESTS=1`;
- `CONTINUUMWIKI_CATEGORY_PERSISTENCE_AUTHORIZED=1`;
- a reviewed `CONTINUUMWIKI_CATEGORY_PERSISTENCE_APPROVAL_PATH` scope file;
- an exact `CONTINUUMWIKI_CATEGORY_PERSISTENCE_TARGET_DATABASE` matching the
  approved disposable target; and
- an environment-only `CONTINUUMWIKI_MYSQL_TEST_CONNECTION` for that target.

The target must be isolated, non-production, least-privilege, and version
verified as MariaDB 11.8.x before application-runtime tests. Credentials must
never enter reports or source. The existing rootless Podman/loopback/labelled
cleanup pattern is the appropriate operational model, but no provisioning
command was executed and no category-specific provisioning script was
invented or run.

The new gate-only test
`MediaWikiCategoryPersistenceAuthorizationTests` remains skipped until the
explicit inputs exist. It never opens a database; it only validates that an
approved target path and `cw_m5_012_` database identity are present. It is not
database certification.

## Verification and blockers

- Source-only focused tests: 80 passed, 0 failed.
- Full Debug suite: 337 passed, 24 existing/gated skips, 0 failed.
- Release build: 0 warnings, 0 errors.
- Database mutation count: 0.
- Database tests executed: 0.
- Production database, credentials, schemas, routing, Lua, Cargo, worker
  security, and migration planner: unchanged.

Remaining blockers are the authorized MariaDB 11.8 disposable target, a
reviewed category schema migration, a durable provider implementation, a
visibility predicate contract, durable delete/restore/import lifecycle
handling, and category MVC work.
This milestone does not certify category persistence, production migration, or
historical category parity.
