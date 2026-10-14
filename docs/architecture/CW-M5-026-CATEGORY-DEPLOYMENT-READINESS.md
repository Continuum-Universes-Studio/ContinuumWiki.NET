# CW-M5-026 — Category Deployment Readiness and Production Migration Gap Analysis

## Decision

`CATEGORY_DEPLOYMENT_READINESS_ASSESSED`

This is a source-and-evidence assessment, not deployment approval. The
category subsystem remains `NO_GO` for production migration and repository
activation. No database connection, migration, live test, authorization
change, infrastructure operation, provider activation, or cleanup was
performed for CW-M5-026.

The repository contains bounded disposable MariaDB evidence for CW-M5-019,
CW-M5-020, and CW-M5-025. Those target- and milestone-specific results do not
transfer to production. CW-M5-025's 8/8 live tests and its retained diagnostic
rows are recorded as bounded evidence only; OS, container, and MariaDB-server
crash behavior remains unverified.

## Evidence trust grading

| Area | Evidence | Tier | What it proves | Limitation |
| --- | --- | --- | --- | --- |
| V1 category persistence | `artifacts/validation/mediawiki-category-persistence-v1.json`, `docs/architecture/CW-M5-014-CATEGORY-PERSISTENCE.md` | `SOURCE_VERIFIED` | Schema, revision CAS, empty-index semantics, visibility-safe reads, and provider activation contract exist | Earlier live gate was authorization-blocked; later bounded tests are target-specific |
| Page/lifecycle integration | `artifacts/validation/mediawiki-category-pages-lifecycle-v1.json`, `docs/architecture/CW-M5-015-CATEGORY-PAGES-LIFECYCLE.md` | `SOURCE_VERIFIED` | Category page route and edit/delete/restore lifecycle behavior are covered by source tests | No production backfill or live production lifecycle |
| Reindex/recovery planning | `artifacts/validation/mediawiki-category-reindex-recovery-v1.json`, `docs/architecture/CW-M5-016-CATEGORY-REINDEX-RECOVERY.md` | `SOURCE_VERIFIED` | Bounded planning, stale revision, cancellation, and partial-result contracts | Durable category audit/checkpoint and production execution were not certified in that milestone |
| V2 recovery durability | `artifacts/validation/mediawiki-category-recovery-durability-v1.json`, `docs/architecture/CW-M5-017-CATEGORY-RECOVERY-DURABILITY.md` | `SOURCE_VERIFIED` | Recoverable two-phase operation/checkpoint/audit model and fail-closed transitions | No cross-store atomicity or exactly-once recovery claim |
| V1/V2 disposable MariaDB | `secure/evidence/cw-m5-019-certification-v1.json`, `artifacts/validation/mediawiki-category-concurrency-crash-replay-v1.json` | `DATABASE_VERIFIED_BOUNDED` | Authorized operator4 target exercised category, recovery, CAS, concurrency, and application fault cases | Target was disposable; connection counts were not instrumented; OS/container/server crash cases were not run |
| Admin recovery API | `artifacts/validation/mediawiki-category-admin-recovery-api-v1.json`, `docs/architecture/CW-M5-022-CATEGORY-ADMIN-RECOVERY-API.md` | `SOURCE_VERIFIED` | Preview/execute, authentication, administrator, anti-forgery, page authorization, and readiness gates are implemented | API remains disabled by default and has no production provider evidence |
| Admin authorization | `artifacts/validation/mediawiki-category-admin-authorization-v1.json`, `docs/architecture/CW-M5-023-CATEGORY-ADMIN-AUTHORIZATION.md` | `SOURCE_VERIFIED` | Source-level actor, scope, operation, and readiness authorization boundaries | No production grant issuer, identity binding, or activation approval |
| V3 grant durability | `artifacts/validation/mediawiki-category-admin-grant-durability-v1.json`, `docs/architecture/CW-M5-024-ADMIN-GRANT-DURABILITY.md` | `SOURCE_VERIFIED` | Durable grant model, revocation, single-use CAS, audit idempotence, and rejecting defaults | Source-only milestone; later bounded live evidence is separate |
| V3 disposable MariaDB | `artifacts/validation/mediawiki-category-admin-grant-mariadb-certification-v1.json`, `docs/architecture/CW-M5-025-ADMIN-GRANT-MARIADB-CERTIFICATION.md` | `DATABASE_VERIFIED_BOUNDED` | Exact authorized disposable target applied base Page → V1 → V2 → V3 and passed 8/8 live certification tests | 28 grants and 47 audit rows retained; no production readiness or server-crash claim |
| Default activation | `TightWiki/Program.cs`, `TightWiki/MySqlDevelopmentRuntime.cs`, `TightWiki/appsettings.json` | `SOURCE_VERIFIED` | Rejecting category repositories/stores and disabled Lua/production settings remain defaults | Source inspection is not deployed-runtime certification |
| Provider activation | `TightWiki.Repository/Helpers/ProviderNeutralPersistenceServiceCollectionExtensions.cs`, `TightWiki/MySqlCategoryAdministrativeGrantActivation.cs` | `SOURCE_VERIFIED` | Activation requires MySQL, explicit feature flags, exact schema versions, an existing connection factory, and readiness proofs | No approved production readiness proof or activation run |
| Generic backup/restore | `docs/operations/MYSQL-BACKUP-RECOVERY.md`, `docs/operations/MARIADB-CUTOVER-DECISIONS.md` | `SOURCE_VERIFIED` | Generic logical backup, checksum, distinct-target restore, and rollback limitations are documented and historically rehearsed | No category-specific production snapshot/restore or category-index reconciliation evidence |
| Historical MediaWiki parity | MediaWiki import/compatibility artifacts and roadmap | `NOT_CERTIFIED` | Source compatibility work exists separately | Category deployment readiness does not establish full MediaWiki, Cargo, Scribunto, or extension parity |

The M5-025 authorization manifest and disposable credentials were not read or
changed in this assessment. No secret values are included here.

## Dependency matrix

| Capability | Required dependency | Source/target status | Production implication |
| --- | --- | --- | --- |
| Category membership persistence | MariaDB base `Page` schema, then category V1 | Source-traced; order exercised by bounded M5-019/M5-025 runs | A production migration must prove the exact existing Page schema and apply V1 transactionally or abort before provider activation |
| Index state and membership browse | V1 tables, Page identity/revision data, visibility and permission evaluation | Source verified; bounded database behavior verified | Backfill must preserve page revision and index-generation provenance; no production backfill evidence exists |
| Recovery planning | Category extraction, page source, bounded expansion, cancellation and partial-result contracts | Source verified | Incomplete expansion must remain fail-closed; production retry and operator abort policy must be exercised |
| Recovery durability | V1 schema-version ledger, V2 operation/audit tables, checkpoint CAS | Source verified; bounded database behavior verified | Recovery is intentionally two-phase and recoverable, not cross-store atomic or exactly-once |
| Administrative grants | V1 schema-version ledger, V3 grant/audit tables, durable actor/scope binding | Source verified; bounded M5-025 database behavior verified | Production grant issuance, revocation, identity ownership, retention, and audit review remain unproven |
| API preview/execute | Authenticated trusted administrator, anti-forgery, page authorization, readiness evidence, grant provider, durable recovery store | Source verified; default runtime registrations reject | The route must remain disabled until all production gates pass; endpoint existence is not activation |
| Provider activation | MySQL provider, explicit feature flag, exact schema version, readiness proof, approved target | Source verified only | No startup path currently calls the activation methods; production activation remains `NO_GO` |

Operational application order is **base Page schema → V1 → V2 → V3**. V1
owns the shared `MySqlCategorySchemaVersion` ledger and its Page foreign keys;
V2 and V3 add their own tables and version rows. The V3 live certification
fix explicitly applied the existing base schema before V1 after an earlier
disposable run demonstrated the missing-Page failure.

## Production gate register

| Gate | Current outcome | Required closure evidence | Owner/authority |
| --- | --- | --- | --- |
| Nonproduction source assessment | `PASSED_SOURCE_ASSESSMENT` | This report validates and remains read-only | Engineering review |
| Bounded database compatibility | `PASSED_BOUNDED_ONLY` | Existing M5-019/020/025 reports and exact target evidence | Engineering plus disposable-target operator |
| Production target authorization | `BLOCKED` | New explicit production target, scope, expiry, isolation, and mutation authorization; neither disposable manifest transfers | Project Owner/System Administrator and operations approver |
| Production schema migration | `BLOCKED` | Target-specific preflight, base Page/V1/V2/V3 migration plan, schema readback, abort conditions, and reviewed rollback procedure | Database operator |
| Backup/restore | `PARTIAL` | Category-inclusive backup, checksum, distinct-target restore, index/revision reconciliation, and application recovery smoke test | Database operator/backup owner |
| Authentication and grant issuance | `PARTIAL` | Production identity ownership, administrator/page authorization, grant issuance/revocation procedure, audit retention and review | Security/operations owner |
| Provider activation | `BLOCKED` | Readiness proofs for the exact production database followed by explicit feature configuration approval | Application owner and operations approver |
| Index backfill/reconciliation | `BLOCKED` | Dry-run counts, revision/generation reconciliation, zero-member and partial-index handling, bounded execution, monitoring, and abort/rollback thresholds | Category data owner |
| Monitoring/failure detection | `NOT_CERTIFIED` | Production metrics/alerts for provider unavailable, stale/incomplete/failed index state, recovery indeterminate, grant audit failure, and disabled/default transitions | Operations owner |
| Crash/fault coverage | `PARTIAL` | Separate authorized OS/container/MariaDB crash and mid-transaction SQL-fault campaign, with replay evidence | Reliability owner |
| Historical compatibility | `NOT_CERTIFIED` | Independent MediaWiki/Cargo/Scribunto/reference evidence for the broader migration scope | Migration owner |
| Final activation/rollback decision | `NO_GO` | All prior gates closed, explicit production approval, rehearsed abort and rollback decision | Project Owner/System Administrator |

## Production blocker register

1. **Critical — No production authorization.** Existing approvals identify
   disposable, milestone-specific targets only. Closure requires a separate
   production approval and target-bound manifest; no such manifest was created
   or requested here.

2. **Critical — Production schema and migration are unverified.** The SQL is
   repeatable in the limited `CREATE IF NOT EXISTS`/forward-version sense, but
   no production schema compatibility, lock/latency behavior, migration
   window, or downgrade migration is evidenced. Closure requires a
   production-like rehearsal and reviewed abort/rollback procedure.

3. **High — Provider activation is not production-proven.** Source activation
   requires explicit flags and readiness proofs, while the normal runtime
   registers rejecting implementations. Closure requires exact target
   readiness evidence and independent approval; do not enable by configuration
   alone.

4. **High — Backfill and reconciliation are not operationally certified.**
   Source tests cover revision conflicts, visibility, empty sets, and bounded
   recovery, but no production-sized category backfill, progress monitoring,
   repair policy, or abort threshold is recorded.

5. **High — Backup/restore evidence is not category-specific.** Generic
   MariaDB backup evidence exists, including the documented loss of post-
   snapshot writes on rollback. Category index state, recovery audit,
   administrative grants, and provenance reconciliation require an explicit
   rehearsal.

6. **High — Crash and fault coverage is incomplete.** M5-020 and M5-025
   include application-level interruption/fault cases, not OS, container, or
   MariaDB-server crashes. A live mid-transaction SQL fault was also marked
   unexecuted because no safe injector existed.

7. **High — Production administrative identity and grant operations are not
   evidenced.** Disposable grant certification proves database behavior, not
   the production actor directory, approval workflow, revocation ownership,
   audit retention, or incident response.

8. **Medium — Multi-worker lease semantics are not modeled.** The recovery
   contract uses checkpoint/generation CAS but no lease owner/expiry contract.
   A deployment with multiple recovery workers requires a design decision and
   certification before activation.

9. **High — Overall migration parity remains separate.** Category evidence
   cannot establish historical MediaWiki source completeness, Cargo row
   provenance, Scribunto parity, TemplateStyles, or custom extension behavior.

10. **Medium — Disposable diagnostic rows remain.** The M5-025 target retains
    28 grants and 47 audit rows under an explicit no-cleanup authorization.
    This is not a production blocker by itself, but cleanup requires separate
    approval and must not be silently folded into a later gate.

## Risks and required controls

- A stale or incomplete category index must surface as an explicit state or
  provider failure, never as an empty successful category. Preserve source
  revision and generation checks during backfill.
- A restored database snapshot may omit writes after the snapshot. Require a
  write journal or reconciliation procedure before any production rollback.
- Recovery remains a recoverable two-phase protocol. Treat uncertain outcomes
  as indeterminate and reconcile authoritative state; do not advertise
  cross-store exactly-once behavior.
- Keep `RejectingTwCategoryMembershipRepository`,
  `RejectingTwCategoryRecoveryDurabilityStore`, and rejecting grant evidence/
  store defaults until target-bound readiness and authorization are complete.
- Keep the administrative controller disabled by configuration and protected
  by authentication, administrator, anti-forgery, page, operation, and
  readiness checks.
- Redact credentials, page identifiers, category contents, grant payloads,
  and sensitive operational details from readiness evidence and monitoring.

## Prioritized remediation plan

| Order | Proposed milestone | Action | Acceptance criterion | New authority needed |
| --- | --- | --- | --- | --- |
| 1 | CW-M5-026A | Prepare a production-specific authorization and target-readiness worksheet | Exact target, owner, expiry, isolation, scope, and `NO_GO` defaults reviewed; no execution | Explicit production approval |
| 2 | CW-M5-026B | Rehearse category-inclusive backup/restore on a new disposable target | Checksummed restore, schema/index/revision reconciliation, recovery smoke test, and rollback limitation accepted | New disposable database authority |
| 3 | CW-M5-026C | Run production-like migration and backfill dry run | Base→V1→V2→V3 ordering, repeatability, counts, revision/generation checks, abort thresholds, and residual audit plan pass | New disposable or staging authority |
| 4 | CW-M5-026D | Complete monitoring and failure campaign | Redacted alerts, provider outage handling, indeterminate recovery response, and authorized crash/fault evidence pass | Reliability/test authority; no production activation |
| 5 | CW-M5-026E | Final activation and rollback review | All independent gates closed and explicit production activation approval recorded | Separate production authorization |

No remediation step above authorizes deployment. Production migration and
repository activation remain `NO_GO`.

The original CW-M5-026B source-only provisioning handoff below is historical;
it did not provision resources. A later, separately authorized continuation
recorded `SOURCE_VERIFIED_DESTINATION_PROVISIONED` for the exact source and
destination at `2026-10-12T12:34:46.231876Z`. The continuation is not a
backup/restore rehearsal authorization and does not establish current target
state. The source application identity had been reported as not
schema-scoped, and continuation did not verify or repair that grant. Current
target state, source schema/fixture readiness, and destination emptiness still
require authorized host-side verification. See
`artifacts/validation/mediawiki-category-backup-restore-rehearsal-authorization-handoff-v1.json`.

For CW-M5-026B provisioning, the historical operator decision packet is
`docs/operations/CW-M5-026B-PROVISIONING-OPERATOR-DECISION-PACKET.md`, and the
non-authorizing machine-readable handoff is
`artifacts/validation/mediawiki-category-backup-restore-provisioning-authorization-handoff-v1.json`.
Those source-only handoff artifacts remain historical and non-authorizing;
later operator-approved provisioning and continuation evidence is recorded
separately. Provisioning approval, continuation approval, and rehearsal
approval remain distinct gates.

The subsequent read-only environment inspection is recorded in
`docs/operations/CW-M5-026B-READONLY-HOST-INSPECTION-AND-TARGET-PROPOSAL.md`
and
`artifacts/validation/mediawiki-category-backup-restore-readonly-host-inspection-v1.json`.
It remains partial because rootless Podman initialization and netlink listener
inspection were blocked; its source/destination values are proposals only.

## Verification boundary

CW-M5-026 performed only filesystem/source/evidence inspection and report
construction. It did not connect to MariaDB, run migrations or integration
tests, inspect live Podman state, read credential values, modify manifests,
activate providers, alter runtime configuration, or delete retained rows or
containers.

