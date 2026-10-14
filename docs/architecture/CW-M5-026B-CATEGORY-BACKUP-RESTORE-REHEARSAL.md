# CW-M5-026B — Category-Inclusive Backup and Restore Rehearsal

## Status and boundary

This document defines the source-only preparation for a separately authorized
disposable rehearsal. It is not an approval, target proof, credential store,
backup, restore, migration, or production activation. The source-only
preparation classification is `CATEGORY_BACKUP_RESTORE_PREPARATION_SOURCE_VERIFIED`;
the current target evidence and remaining rehearsal blockers are recorded in
`artifacts/validation/mediawiki-category-backup-restore-rehearsal-authorization-handoff-v1.json`.

Track A is nonconnecting. Track B remains blocked until a new, exact
CW-M5-026B authorization names both disposable targets and grants each
operation separately. CW-M5-019, CW-M5-025, and any production worksheet are
not transferable. Production migration, category repository activation, and
administrative recovery activation remain `NO_GO`.

## Source-verified dependency and data scope

The actual SQL and repository contracts establish this order:

```text
Page/PageRevision/PageTag/PageReference and MySqlPageSchemaVersion
        ↓
category V1: MySqlCategorySchemaVersion, PageCategoryIndexState,
             PageCategoryMembership
        ↓
recovery V2: TwCategoryRecoveryOperation,
             TwCategoryRecoveryAuditEvent
        ↓
grant V3: TwCategoryAdministrativeGrant,
          TwCategoryAdministrativeGrantAudit
```

The base Page schema is a prerequisite, not part of the category V1 ledger.
`PageRevision` and both category tables retain page/revision and source
provenance fields. `PageCategoryIndexState` distinguishes `Verified`,
`Incomplete`, `Failed`, and `Unknown`; an indexed empty membership set must
not be conflated with an unindexed or failed state. V2 retains operation
status, committed position, checkpoint generation, outcome/audit sequences,
event identity and event-key uniqueness. V3 retains issue, validity,
revocation, consumption, replay, correlation, and audit event-key state.

All listed tables are InnoDB in the repository SQL and use explicit primary,
foreign, unique, and secondary indexes. The source files are:

- `TightWiki.Repository/Scripts/MySql/Pages/V1__PageSchema.sql`
- `TightWiki.Repository/Scripts/MySql/Category/V1__CategorySchema.sql`
- `TightWiki.Repository/Scripts/MySql/Category/V2__CategoryRecoveryDurability.sql`
- `TightWiki.Repository/Scripts/MySql/Category/V3__CategoryAdministrativeGrants.sql`

No live schema or row inventory was performed for CW-M5-026B. A later
continuation report records a provisioned destination and successful source
runtime identity check, but contains no rehearsal readback. The rehearsal
must read back the actual destination rather than infer success from applying
the same migrations.

## Authorization contract

`docs/operations/cw-m5-026b-authorization-manifest.schema.json` defines
`cw-m5-026b-authorization-v2` for the executable rehearsal only. Version 1
manifests are rejected because they do not bind the execution host, exact
runtime container/image/run provenance, DDL freeze, and bounded non-mutating
test scope. Provisioning uses the
separate `cw-m5-026b-provisioning-authorization-v1` contract in
`docs/operations/cw-m5-026b-provisioning-authorization-manifest.schema.json`.
The rehearsal contract requires:

- exact source and destination targets, each disposable, loopback-bound,
  MariaDB 11.8.x, role-scoped, schema-scoped, and separately identified;
- genuine approver, operator, owner, fresh rehearsal-scoped approval ID, and
  UTC validity no longer than 24 hours;
- dedicated credential references without credential values;
- explicit connection, isolation, seed, backup, checksum, restore,
  verification, evidence, fault, cleanup, schema deletion, and
  container/volume deletion booleans. Provisioning is controlled by the
  separate M5-026B provisioning authorization contract and never by this
  rehearsal manifest;
- distinct source and destination identities and endpoints;
- `mariadb-dump --single-transaction` with routines, events, triggers,
  hex-blob, UTF-8, and an explicit post-snapshot divergence policy;
- a bounded suite, at most four workers, crash boundaries, and a required
  post-snapshot-write case.

The nonconnecting preflight rejects legacy or ambiguous permission names,
production/shared targets, non-loopback endpoints, target identity mismatch,
expired/future approval, secrets, same-target source/destination bindings,
missing core permissions, unsupported snapshot semantics, and invalid test
scope. It reads neither connection environment variables nor credential
files.

Destructive permissions are separate. A future manifest may explicitly grant
cleanup, schema deletion, container/volume deletion, or fault injection, but
the stage planners never infer those permissions and never perform them in
the source-only track.

## Snapshot strategy

The planned logical snapshot uses an operator-owned option file or secret
store, never a password in argv or logs:

```text
mariadb-dump --defaults-extra-file=$CW_M5_026B_SOURCE_CNF \
  --single-transaction --routines --events --triggers --hex-blob \
  --default-character-set=utf8mb4 --skip-comments \
  --databases <authorized-source-database> > <private-backup-artifact>
```

The artifact is valid only when the command succeeds, the file is complete,
the SHA-256 sidecar matches, and the retained evidence identifies the source
target and client/server versions without secrets. `--single-transaction`
provides a consistent InnoDB snapshot; it does not make non-InnoDB tables or
concurrent DDL transactional. DDL uncertainty, a partial artifact, a checksum
mismatch, or an unknown engine/table state is fail-closed.

## Restore safety

The destination must be newly provisioned, separately authorized, loopback
bound, dedicated, and empty before import. The workflow does not drop,
truncate, overwrite, or restore over a populated or ambiguous target. A
checksum is verified before import, and the destination schema/ledger is
checked after import. Repeating V1/V2/V3 migrations is not treated as proof
that data was restored.

The staged planner uses a destination-only credential reference and emits the
following operator-reviewed shape only after the restore permission gate:

```text
mariadb --defaults-extra-file=$CW_M5_026B_DESTINATION_CNF \
  --database <authorized-destination-database> < <verified-backup-artifact>
```

The current implementation emits a plan and does not execute that command.

## Verification matrix

The independently run verification stage must compare sanitized aggregate
evidence for:

| Area | Required checks |
| --- | --- |
| Schema | Page and category ledger versions, table engines, columns, indexes, foreign keys |
| Page history | Page, PageRevision, tags, references, current/deleted state, revision identity |
| Category | Index-state semantics, empty versus unindexed, membership navigation and sort order, index generation |
| Provenance | Source page/revision IDs, source wiki identity, indexed revision and timestamps |
| Recovery | Operation state, checkpoint generation/CAS, outcome/audit sequence, event ID and event-key uniqueness, replay/idempotency |
| Grants | Issued, consumed, revoked, expiry, replay mode, correlation, audit continuity and event-key uniqueness |
| Integrity | Orphan/FK checks, aggregate counts and privacy-safe stable hashes |
| Divergence | Writes after the snapshot, recovered/source differences, explicit reconciliation or replay decision |

No private Cargo/category rows or unrestricted page contents belong in the
report. Counts and hashes are evidence only when tied to the exact source and
destination identities and snapshot artifact checksum.

## Failure and indeterminate outcomes

The rehearsal must stop and classify, without cleanup, on missing/expired
approval, target mismatch, production/shared identity, nonempty destination,
partial or corrupt artifact, checksum mismatch, incompatible schema, import
interruption, missing critical table, stale revision, FK/orphan mismatch,
missing audit, authorization expiry, or unreconciled post-snapshot writes.

An SQL import success is not a restore success. The restored destination does
not automatically recover writes made after the snapshot. Those writes need
an external journal or an explicit manual reconciliation decision. This
workflow makes no cross-store atomicity, exactly-once, point-in-time recovery,
or lossless rollback claim.

## Staged source-only commands

After fresh targets have been provisioned under the separate provisioning
contract and a genuine exact-target M5-026B rehearsal manifest exists, the
operator can first run the nonconnecting check:

```bash
scripts/preflight-m5-026b.sh \
  --manifest /path/to/cw-m5-026b-authorization.json \
  --report /path/to/cw-m5-026b-preflight.json
```

The four stages are intentionally separate. Each writes a redacted plan and
reports zero database connections, zero mutations, and zero Podman commands:

```bash
scripts/cw-m5-026b/snapshot-m5-026b.sh \
  --manifest /path/to/cw-m5-026b-authorization.json \
  --artifact /private/path/source.sql \
  --output /private/path/snapshot-plan.json

scripts/cw-m5-026b/restore-m5-026b.sh \
  --manifest /path/to/cw-m5-026b-authorization.json \
  --artifact /private/path/source.sql \
  --output /private/path/restore-plan.json

scripts/cw-m5-026b/verify-m5-026b.sh \
  --manifest /path/to/cw-m5-026b-authorization.json \
  --artifact /private/path/source.sql \
  --output /private/path/verification-plan.json

scripts/cw-m5-026b/evidence-m5-026b.sh \
  --manifest /path/to/cw-m5-026b-authorization.json \
  --artifact /private/path/source.sql \
  --output /private/path/evidence-plan.json
```

The four wrappers above remain plan-only and do not span live operations.

## Explicitly gated executable backup/restore path

`scripts/cw-m5-026b/rehearse-m5-026b.sh` is a separate source implementation
for a narrowly bounded snapshot/restore/readback operation. It is not invoked
by the plan-only stage wrappers. Default invocation is nonconnecting; live
execution requires both `--execute` and
`--confirm-approval-id <exact-manifest-ApprovalId>`.

The rehearsal manifest binds the operator host and UID, exact source and
destination container IDs, immutable MariaDB image digest, and unique
provisioning run IDs inside each `InstanceIdentity`. `SnapshotPlan` also
requires `DdlFreezeConfirmed=true`. The runner rejects expired/invalid
authorization, changed container/image/volume/loopback identity, non-rootless
Podman, noncanonical or non-owner-only option files, non-schema-scoped grants,
non-InnoDB tables, absent required tables, or a nonempty destination. Artifact,
checksum sidecar, and report paths are create-only; partial artifacts and
failure reports are retained. It performs no cleanup, deletion, migration,
seeding, or fault injection.

The executable's output classification is deliberately limited to
`RESTORE_CONTENT_READBACK_MATCHED_NOT_FULL_CERTIFIED`: it checks table/engine
inventory and per-table row-content digests. It does not certify schema DDL,
ledger values, foreign-key/orphan semantics, category-index correctness,
recovery/grant audit continuity, crash boundaries, or post-snapshot writes.
Those suites remain separate authorization and implementation work. A successful
restore is not a full recovery or production certification.

Review-only invocation shape (NOT EXECUTED):

```bash
scripts/cw-m5-026b/rehearse-m5-026b.sh \
  --manifest /private/path/cw-m5-026b-authorization.json \
  --artifact /private/path/backup.sql \
  --report /private/path/rehearsal-result.json \
  --execute \
  --confirm-approval-id '<exact-approved-id>'
```

The retained target pair remains blocked pending fresh, complete rehearsal
authorization and current host-side verification. The earlier provisioning
record says the source application identity failed its schema-scope check;
that is a historical, ambiguous check result, not a current grant verdict.
Continuation verified source server identity but did not inspect source
application grants. Destination scope was reported at continuation time only.
Both current grants are `UNKNOWN/NOT_OBSERVED`. Source table/fixture readiness
and current destination emptiness have not been evidenced. No rehearsal
manifest was created for implementation/testing.

## Current evidence and next action

The machine-readable report records the source mapping, authorization
contract, planned matrix, source-only test results, and the distinction
between continuation and rehearsal authorization. The continuation evidence
records the source and destination as
`SOURCE_VERIFIED_DESTINATION_PROVISIONED` at
`2026-10-12T12:34:46.231876Z`; it does not prove current runtime state. The
prior source provisioning report records
`Source.ApplicationIdentitySchemaScoped: false`; the new runner verifies
`SHOW GRANTS` and will reject global source privileges. Source schema/fixture
state, destination current
emptiness, and present-day target isolation remain unverified. The exact
target-bound rehearsal still needs fresh, separate authorization, current
identity/isolation checks, source privilege correction under separate
authority, and the full recovery/certification verification matrix.

Do not infer permission for Page/V1/V2/V3 migrations or test-row writes from
`SeedDisposableSource`, backup, or restore. The executable path requires
`SeedDisposableSource=false`, `PostSnapshotWriteCases=false`,
`FaultInjection=false`, and an empty crash-boundary list. Migrations, fixture
seeding, fault injection, and post-snapshot write/recovery tests require a
separate contract and implementation before they can be authorized.

Production migration, provider activation, Lua/Cargo configuration, workers,
systemd, and production data remain unchanged and `NO_GO`.

## Source-only data-readiness assessment

`scripts/cw-m5-026b/readiness.py` reads retained JSON evidence and repository
SQL only; all modes are nonconnecting. Its report is
`artifacts/validation/mediawiki-category-backup-restore-data-readiness-v3.json`.
The source-evidence classification is
`CATEGORY_REHEARSAL_DATA_READINESS_SOURCE_VERIFIED`; the separate live gates
remain `HISTORICAL_ONLY`, `LIVE_INSPECTION_REQUIRED`,
`SCHEMA_OR_FIXTURE_INSUFFICIENT`, and `NOT_CERTIFIED`.

The assessment reconciles provisioning and continuation source/destination
identity prefixes, names, database identities, endpoints, immutable image
digest, container IDs, continuation manifest hashes, and timeline. The
continuation implementation checks the provisioning run ID against container
and volume labels, but its successful report does not persist that run ID;
the report records a hash of the historical run ID and explicitly leaves its
current value un-attested. Current target health and identity remain
unverified. The original provisioning code creates the MariaDB database and
accounts, checks an initially empty schema, and performs no application
migrations or fixture seeding.

The v3 report adds exact key/index columns and foreign-key targets and clarifies
that the historical source empty-schema guard was not reached: the retained
provisioning result says its source grant mutation was not reached, and the
code performs that empty check afterward. v1 and v2 reports remain preserved;
v3 is the current assessment.

The derived schema inventory contains 21 table definitions across Page V1/V2/V3
and Category V1/V2/V3, including six expected ledger domain/version rows. The
rehearsal runner does not validate columns, indexes, foreign keys, ledgers,
revision/provenance semantics, recovery/grant audit continuity, or non-base
objects during its destination-emptiness check. Synthetic fixture shapes for
indexed-empty, unindexed, populated/current membership, visibility, V2, and V3
are specified in the report; none were created. Migrations and fixture writes
need separate explicit authorization and implementation.

Any future source/destination read-only inspection needs a fresh distinct
exact-target approval. Its non-authorizing operator worksheet is
`docs/operations/CW-M5-026B-READINESS-INSPECTION-AUTHORIZATION-WORKSHEET.md`.
Provisioning/continuation authority does not transfer, and no live inspection
runner is provided by this source-only assessment.
