# SQLite-to-MariaDB migration

CW-M2-006B adds a standalone .NET 10 migration command. CW-M2-006C adds the
read-only source integrity report and non-destructive repair rehearsal. SQLite remains the
default provider and the migration never runs as part of normal application
startup. The destination must be a disposable or separately provisioned
MariaDB database initialized by the existing schema initializer; production
cutover is outside this milestone.

## Verified target

The disposable deployment used for this milestone is
`docker.io/library/mariadb:11.8`. The running server reported:

```text
11.8.9-MariaDB-ubu2404
mariadb.org binary distribution
utf8mb4_unicode_ci
```

The application continues to use the existing `Database:Provider=mysql`
compatibility key and Dapper/MySqlConnector boundary. Schema initialization
normalizes Oracle MySQL's unavailable `utf8mb4_0900_ai_ci` to MariaDB's
`utf8mb4_unicode_ci`; no second ORM or SQLite fallback was added.

## Commands

All options are required so a source or destination cannot be inferred from a
production process environment:

```bash
dotnet run --project SqliteToMariaDb/SqliteToMariaDb.csproj -- integrity \
  --source-root /path/to/Data \
  --translations /path/to/Translations.db \
  --defaults /path/to/defaults.db \
  --destination 'Server=127.0.0.1;Port=3306;Database=continuumwiki;User ID=migrator;Password=...' \
  --report /secure/reports/integrity.json

dotnet run --project SqliteToMariaDb/SqliteToMariaDb.csproj -- preflight \
  --source-root /path/to/Data \
  --translations /path/to/Translations.db \
  --defaults /path/to/defaults.db \
  --destination 'Server=127.0.0.1;Port=3306;Database=continuumwiki;User ID=migrator;Password=...' \
  --report /secure/reports/preflight.json

dotnet run --project SqliteToMariaDb/SqliteToMariaDb.csproj -- dry-run ...
dotnet run --project SqliteToMariaDb/SqliteToMariaDb.csproj -- execute ...
dotnet run --project SqliteToMariaDb/SqliteToMariaDb.csproj -- verify ...
```

`integrity`, `preflight`, and `dry-run` open every SQLite file with `Mode=ReadOnly` and do
not write the destination. `execute` uses one destination transaction and
rolls back on the first error or identifier collision. `verify` reconciles
counts, Identity password/security-stamp pairs, and attachment bytes. The
`ContinuumWikiMigrationCompletion` row is written only after verification.

The command exits nonzero for missing stores/tables, non-MariaDB destinations,
missing destination schema, source foreign-key/orphan violations, duplicate
destination keys, failed writes, or reconciliation discrepancies. It never
uses `INSERT IGNORE` or an upsert for migrated user data.

## Source inventory and mapping

| SQLite source | Target mapping | Classification |
| --- | --- | --- |
| `config.db` | Configuration groups/entries, datatypes, themes, menus, crypto and version state | Direct |
| `users.db` | ASP.NET Identity users/roles/claims/logins/tokens, profiles, roles, permissions and assignments | Direct; GUID text is canonicalized case-insensitively for MariaDB `ascii_bin` keys |
| `pages.db` | Pages, revisions, navigation, files, file revisions, associations, comments, tags, references, instructions, tokens, editors | Direct plus canonical navigation transformation |
| `deletedpages.db` | Unified `Page`/`PageRevision`/attachment/content tables with `IsDeleted=true` | Transformed |
| `deletedpagerevisions.db` | `DeletedPageRevision` and archive associations | Transformed; page navigation and deletion metadata are resolved from read-only stores |
| `logging.db` | Severity and event log | Direct |
| `statistics.db` | `PageStatistics` keyed by `PageId` | Transformed; SQLite surrogate `Id` is dropped; orphan rows block migration because statistics are recomputed metadata |
| `emoji.db` | Emoji and category tables | Transformed; legacy missing `Shortcut` uses the stable source name |
| `Translations.db` | Wide `Translation` table | Direct, read-only shipped artifact |
| `defaults.db` | No import | Explicitly excluded; canonical application seed data is recomputed by `MySqlDefaultContentSeeder` |

The utility inventories tables not selected for import and reports explicit
exclusions. Orphan records are not silently discarded: preflight reports them
and execute refuses to proceed until the source copy is repaired or the
record is intentionally resolved by an operator.

## CW-M2-006B disposable migration drill

The drill copied the repository SQLite stores to a disposable source copy,
added a synthetic Unicode page with two revisions, comment, tags, reference,
feature template, menu/theme/translation rows, an Identity account with a
known password hash, and two attachment revisions. The repository source
files were not modified. A stale deleted-store metadata row and an orphan
statistics row were detected and removed only from the synthetic rehearsal
copy; the unchanged repository source is correctly rejected by preflight for
those orphan records.

Against MariaDB `11.8.9-MariaDB-ubu2404`:

- preflight passed with 10 readable stores and no unexplained discrepancies;
- dry-run passed with zero destination writes;
- execute/reconcile passed with 51 migrated mappings, 4 explicit seed
  exclusions, 16,555,428 binary bytes processed, and completion marker
  `sqlite-to-mariadb-v1`;
- migrated account authentication returned HTTP 302, current and historical
  page routes returned HTTP 200, search/profile/admin routes returned HTTP 200,
  and anonymous admin access returned HTTP 302;
- attachment revision 0 matched
  `8afe5227cab5e4587830482d46ccba6bb12b09aca55902126fa4a6631637b674` and
  revision 1 matched
  `6999f857b7a6fe2dd9298e727b4dccbba500cf246bb59e205d9898b127a8bb09`;
- the MariaDB backup/recovery regression passed all REC-01 through REC-17
  checks after restore into a distinct database. Its dump was 2,671,009 bytes
  with SHA-256
  `6f2753023b85af81d81de4ff9b457f6b4031e34d8d10f66043c8bb97fe62a863`.

Machine-readable drill outputs were written during execution to
`/tmp/cw-006b-source2-preflight.json`,
`/tmp/cw-006b-source2-dry-run.json`,
`/tmp/cw-006b-final-migration.json`,
`/tmp/cw-006b-mariadb-manifest.json`, and
`/tmp/cw-006b-mariadb-recovery-http.json`; sanitized copies are retained in
[`artifacts/validation/cw-m2-006b/`](../../artifacts/validation/cw-m2-006b/).

Repository validation completed with `dotnet restore TightWiki.sln` (0), the
Debug solution build (0 warnings, 0 errors), the default solution test run
(141 passed, 19 explicit MySQL skips, 0 failed), and Release publish to
`artifacts/publish` (0). A fresh MariaDB provider selection separately passed
28/28 tests against MariaDB 11.8. No production endpoint or source SQLite
file was modified.

## Operational restrictions

Do not point the command at production SQLite files, a live application
database, or a destination containing unreviewed data. Take a verified backup
before any future rehearsal, stop source writes, run preflight and dry-run
against an untouched source copy, review conflicts/exclusions, and retain the
SQLite source until post-cutover reconciliation and rollback are complete.
MediaWiki import and production provider cutover are not included.

## CW-M2-006C readiness result

The unchanged repository source has four individually reported orphaned
records. They remain blocking until an operator approves the isolated-copy
repair/exclusion policy. The complete audit, exclusion review, repair hashes,
and disposable migration/HTTP/rollback evidence are in
[`MARIADB-CUTOVER-READINESS.md`](MARIADB-CUTOVER-READINESS.md).

The current verdict is `CW-M2-006C BLOCKED`; no production cutover was
performed. The record-level operator package is
[`MARIADB-CUTOVER-DECISIONS.md`](MARIADB-CUTOVER-DECISIONS.md); its pending
decisions are not treated as approvals.
