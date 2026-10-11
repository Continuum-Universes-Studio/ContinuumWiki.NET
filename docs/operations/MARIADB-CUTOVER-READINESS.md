# MariaDB cutover readiness — CW-M2-006C (historical)

CW-M2-006C is preserved as historical development evidence and is **NOT
APPLICABLE — LEGACY SQLITE CUTOVER** to the CW-M3 production path. No
production database, application startup, or SQLite source file was modified.
The production source is MediaWiki; the future migration path is MediaWiki →
ContinuumWiki.NET. The target database is MariaDB 11.8.

## Source integrity result

The read-only source snapshot covered the ten required stores. SQLite
`PRAGMA integrity_check` returned `ok` for every store and
`PRAGMA foreign_key_check` returned zero rows. The broader cross-store audit is
in [`source-integrity-audit.json`](../../artifacts/validation/cw-m2-006c/source-integrity-audit.json).

Four individual anomalies remain in the unchanged source and are pending
operator disposition. Until that disposition is recorded, the cutover gate is
blocked:

| Store/table | Record key | Relationship | Classification | Proposed handling |
| --- | --- | --- | --- | --- |
| `deletedpages.db.PageToken` | `128:profiles` | `PageToken -> Page` | Explicitly excludable, blocking until approved | Omit only from an isolated migration copy |
| `deletedpages.db.PageToken` | `128:sidebar` | `PageToken -> Page` | Explicitly excludable, blocking until approved | Omit only from an isolated migration copy |
| `deletedpages.db.PageProcessingInstruction` | `128:hidefootercomments` | `PageProcessingInstruction -> Page` | Explicitly excludable, blocking until approved | Omit only from an isolated migration copy |
| `statistics.db.PageStatistics` | `PageId=0` | `PageStatistics -> Page` | Explicitly excludable, blocking until approved | Omit only from an isolated migration copy; recompute later |

The parent page `128` is absent from both live and deleted page stores. No
authoritative page, author, revision, or content can be invented safely. The
machine-readable finding and repair records are in
[`original-integrity.json`](../../artifacts/validation/cw-m2-006c/original-integrity.json).

All other audited categories passed: page/revision parents and current
revisions, authors, Identity/security relationships, attachment parents and
sizes, navigation collisions, live/deleted overlap, revision numbers, source
stores, and schema version `2.40.0`.

## Four existing exclusions

The four CW-M2-006B exclusions are rows from `defaults.db`, not imported user
data. They were proposed for replacement by target default initialization, but
the current MariaDB seeder is not treated as historical provenance authority;
the canonical source comparison is recorded below:

| Exclusion | Rows | Durable user content? | Production consequence |
| --- | ---: | --- | --- |
| `DefaultConfiguration` | 73 | No, if canonical | Target must seed canonical defaults |
| `DefaultThemes` | 27 | No, if canonical | Target must seed canonical themes |
| `DefaultFeatureTemplates` | 61 | No, if canonical | Target must seed canonical templates |
| `DefaultWikiPages` | 104 | No, if canonical | Target must seed canonical built-in pages |

This is an explicit policy decision, not silent discard. If any row is
customized or user-authored, the exclusion is unacceptable and becomes a
blocking migration defect. The review is recorded in
[`exclusion-review.json`](../../artifacts/validation/cw-m2-006c/exclusion-review.json).

### Canonical provenance audit

CW-M2-006C.2 completed a read-only comparison of all 265 rows against the
authoritative tracked defaults artifact at commit
`35c534b37d1c8a013dd64c1afe8b93a70013a493` (2026-10-03, `db update.`). The
artifact was regenerated from that commit's `Data/config.db` and
`Data/pages.db` using the existing `GenerateSeedData` utility, and all four
regenerated row sets matched. The source and canonical `defaults.db` hashes
are identical: `6e716393661200b6276a2b5b72a51ddc73e35d72834f1ef16e1ad8ba93b29dab`.

| Domain | Rows | Canonical match | Customized | Version difference | Unknown |
| --- | ---: | ---: | ---: | ---: | ---: |
| `DefaultConfiguration` | 73 | 73 | 0 | 0 | 0 |
| `DefaultThemes` | 27 | 27 | 0 | 0 | 0 |
| `DefaultFeatureTemplates` | 61 | 61 | 0 | 0 | 0 |
| `DefaultWikiPages` | 104 | 104 | 0 | 0 | 0 |
| **Total** | **265** | **265** | **0** | **0** | **0** |

The complete record-level report is
[`default-provenance-comparison.json`](../../artifacts/validation/cw-m2-006c/default-provenance-comparison.json),
with deterministic source and canonical snapshots alongside it. No
normalization was applied to values, content bodies, formatting, hashes, or
metadata. The evidence supports treating all four domains as shipped
canonical defaults, not source-owned user content. The recommended migration
plan is to exclude them from source-owned import and reconcile the target
default store to the canonical artifact, with no overwrite of user-owned
target data. This remains a recommendation pending explicit operator approval;
the audit does not authorize cutover.

## Non-destructive repair rehearsal

The original stores were copied byte-for-byte to an isolated directory. Only
the isolated copy removed the four listed records. The original repository
hashes still match the pre-repair copy; only `deletedpages.db` and
`statistics.db` changed in the repaired copy. See
[`repair-rehearsal-hashes.json`](../../artifacts/validation/cw-m2-006c/repair-rehearsal-hashes.json).

The exact plan is also emitted in the original integrity report. Each action
is reversible by discarding the isolated copy and recreating it from the
original checksum. No replacement identity, author, page, revision, or
content was invented.

## Disposable migration and reconciliation

Against disposable MariaDB `11.8.9-MariaDB-ubu2404`, the repaired copy passed:

- integrity: zero findings and zero discrepancies;
- preflight: successful, read-only destination;
- dry-run: successful, no destination mutation;
- execute/verify: 55 mappings, completion marker
  `sqlite-to-mariadb-v1`, zero discrepancies, 16,555,380 binary bytes;
- Identity password hashes and security stamps, and attachment bytes, were
  verified by the utility's execute/verify path.

Reports are retained under
[`artifacts/validation/cw-m2-006c/`](../../artifacts/validation/cw-m2-006c/),
including [`repaired-execute.json`](../../artifacts/validation/cw-m2-006c/repaired-execute.json).

## Migrated application and recovery smoke

The development-only application started against the migrated disposable
database. A synthetic administrator, page with two revisions, comment, tag,
translation/theme claims, menu entry, and two attachment revisions were
created in the disposable MariaDB database. The HTTP driver executed all 17
checks, including login, authorization, page/revision routes, search,
comments, attachment downloads, localization, profile, menu, and backup
route. All 17 passed; original and historical attachment SHA-256 checks were
performed by the driver. Evidence is in
[`migrated-http.json`](../../artifacts/validation/cw-m2-006c/migrated-http.json).

## Backup and rollback rehearsal

A logical MariaDB dump was taken before the HTTP-only smoke fixture was added,
then restored into a distinct disposable database. Page and revision counts
matched before and after restore. The subsequent rollback report records the
important limitation: writes made after the cutover snapshot are not
automatically preserved. A production procedure must freeze source writes,
journal or manually reconcile post-cutover writes, and reopen the source only
after the rollback decision. Evidence is in
[`backup-rollback-rehearsal.json`](../../artifacts/validation/cw-m2-006c/backup-rollback-rehearsal.json).

## Acceptance matrix

| Gate | Status | Evidence/limitation |
| --- | --- | --- |
| Source integrity | PENDING OPERATOR APPROVAL | Four unresolved source anomalies require an explicit disposition |
| Exclusion provenance | Verified | 265/265 exact canonical matches; 0 customized, version-different, or unknown records |
| Exclusion policy | PENDING OPERATOR APPROVAL | Four evidence-backed canonical-default exclusions still require explicit approval |
| Migration completeness | Verified on repaired copy | 55 mappings, zero unexplained discrepancies |
| Referential integrity | Verified on repaired copy | Integrity and cross-store checks passed after approved rehearsal repairs |
| Binary attachment integrity | Verified on disposable migration/HTTP | Utility verification and HTTP SHA-256 checks passed |
| Identity and permissions | Verified on disposable migration/HTTP | Identity fingerprints and authenticated admin routes passed |
| MariaDB backup/recovery | Verified with limitation | Logical restore passed; not point-in-time recovery |
| Application HTTP functionality | Verified on disposable target | 17/17 migrated-data checks passed |
| No provider fallback | Verified with limitation | Development-only MySQL path was exercised; production startup remains gated |
| Operational observability | Verified with limitation | Sanitized reports/logs retained; production monitoring is not deployed |
| Deployment configuration | BLOCKED | Production MariaDB startup is intentionally still rejected/gated |
| Rollback feasibility | Verified with limitation | Snapshot rollback rehearsed; post-cutover writes require reconciliation |

The operator-facing decision package is
[`MARIADB-CUTOVER-DECISIONS.md`](MARIADB-CUTOVER-DECISIONS.md). Pending
operator approval is not treated as approval. The separate production
cutover authorization gate is also still pending and remains blocked by the
deployment safeguard.

## Required production cutover sequence

If separately authorized later, the operator must take a verified backup,
enter a maintenance/read-only window, freeze SQLite writes, take the final
integrity snapshot, obtain a decision on all four source anomalies and four
default exclusions, execute migration, reconcile IDs/counts/hashes/security
relationships, run application health checks, obtain an explicit cutover
authorization checkpoint, and only then change deployment configuration.
Rollback triggers include failed reconciliation, failed authorization or
attachment checks, startup/provider fallback, or health-check failure. A
rollback restores the last verified MariaDB snapshot or switches the provider
back to SQLite, then reconciles the write journal before source reopening.

## Verdict

`CW-M2-006C BLOCKED`

The isolated repaired migration is technically successful, and CW-M2-006C.2
established canonical provenance for all 265 default-store rows. The unchanged
source still contains four mandatory integrity findings, the four exclusion
decisions remain unapproved, and production cutover authorization is absent.
Production cutover must not begin.
