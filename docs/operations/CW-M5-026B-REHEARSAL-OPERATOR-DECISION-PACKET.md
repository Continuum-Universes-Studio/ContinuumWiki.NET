# CW-M5-026B — Two-target rehearsal authorization decision packet

**Review-only; not an authorization or executable manifest.** No rehearsal
approval has been recorded by this packet. Do not use provisioning or
continuation approvals to authorize snapshot, restore, seeding, migrations,
fault injection, or certification.

## Current evidence and target binding

The later continuation evidence supersedes the earlier provisioning failure
for target existence and source identity as of `2026-10-12T12:34:46.231876Z`.
It does not establish current runtime state. No Podman or database inspection
was performed for this handoff.

| Field | Source, last reported | Destination, last reported |
| --- | --- | --- |
| Container | `continuumwiki-m5-026b-source-ro1` | `continuumwiki-m5-026b-destination-ro1` |
| Container ID | `6a4d4f8707ee4f5b14dddfff183db6d26ce7453eaba377b7c9d081bc1157f914` | `1c58a2f370d36af443e53283d4212604e997188e9fbf81175655446b7c3ea1f5` |
| Volume | `continuumwiki-m5-026b-source-ro1-data` | `continuumwiki-m5-026b-destination-ro1-data` |
| Database | `cw_m5_026b_source_ro1` | `cw_m5_026b_destination_ro1` |
| Endpoint | `127.0.0.1:13326` | `127.0.0.1:13327` |
| Database identity | `continuumwiki_m5_026b_source_app` | `continuumwiki_m5_026b_destination_app` |
| Credential directory reference | `/home/cdjensen94/.local/state/continuumwiki/cw-m5-026b/source-ro1` | `/home/cdjensen94/.local/state/continuumwiki/cw-m5-026b/destination-ro1` |
| Image | `docker.io/library/mariadb@sha256:6422478cb8e159f080fb1d8ccf65101e26fe51385787fde7d16c3b165a331f15` | same digest |

The source database name, port, source container ID, volume, database identity,
credential directory, and image reconcile between the retained provisioning
report and continuation `…-003` authorization/result evidence. Destination
identity fields and container ID are present in the successful continuation
result and match the prior authorized destination plan. The `…-003`
continuation preflight hashes match the `…-002` continuation manifests.

These are evidence-time facts, not a current-state assertion. The successful
continuation report records source identity accepted and destination
provisioned; it does not attest that either target is presently running, that
the destination is currently empty, or that the source contains the required
Page/V1/V2/V3 schema and test rows.

### Material source-side blocker

The original provisioning report records
`Source.ApplicationIdentitySchemaScoped: false`. The continuation report
verified container/image/volume/loopback/credential metadata and the root
runtime identity query, but it did not re-verify or repair the source
application grant. The destination continuation reports its application
identity as schema-scoped. Before rehearsal, source account privileges and
both targets' current isolation must be verified under a separately approved
procedure; do not assume the root identity query proves application-account
isolation.

## Approval worksheet — decisions still required

The current executable rehearsal contract is v2:
`docs/operations/cw-m5-026b-authorization-manifest.schema.json`.
The earlier v1 handoff remains historical and its manifests cannot authorize
the new executor.
It requires the complete object and rejects unknown fields. It cannot safely
represent unset approvals as `null`; therefore this packet deliberately does
not create a schema-shaped draft manifest. The machine-readable handoff is a
separate review record, not an authorization document.

The operator must separately decide and attest:

1. A new, unique rehearsal `ApprovalId`, approver, operator, target owner, and
   their roles. Prior M5-019, M5-025, M5-026B provisioning, and continuation
   approvals are not transferable.
2. Actual `ApprovedAtUtc` and finite `ExpiresAtUtc`. Preflight rejects
   authorization lifetimes longer than 24 hours.
3. Exact current source and destination runtime identities, rootless host,
   target isolation proof, dedicated credential references, and approved
   private artifact directory. Credential contents must remain unread and
   absent from all reports.
4. Whether to grant every mandatory core permission. The preflight requires
   all of these `true`: `ConnectSource`, `VerifySourceIsolation`,
   `BackupSource`, `VerifyBackupArtifact`,
   `ConnectDestination`, `VerifyDestinationIsolation`, `RestoreDestination`,
   `VerifyRestore`, and `RetainSanitizedEvidence`.
5. Source seeding is not part of the executable path. Set
   `SeedDisposableSource=false` and `PostSnapshotWriteCases=false`; migrations,
   fixture writes, and post-snapshot mutations remain unauthorized and are
   not implemented by this runner. Restore writes data only to an independently
   verified empty destination; it is not permission to drop, truncate, or
   overwrite a populated target.
6. Explicit values for `FaultInjection`, source/destination cleanup, schema
   deletion, and container/volume deletion. This proposal recommends all
   `false`; any change requires separate explicit approval. No cleanup or
   deletion is approved here.
7. The test suite is restricted to snapshot, restore, and table-content
   readback. Set `MaxConcurrentWorkers=1`, `CrashBoundaries=[]`, and
   `FaultInjection=false`; no crash or recovery certification is performed.

### Contract and implementation gaps

- There are no `ApplyPageSchema`, `ApplyCategoryV1`, `ApplyV2`, or
  `ApplyV3` permissions. `SeedDisposableSource` must not be interpreted as
  migration authority. If setup migrations are required, they need a separate
  explicit operation-scoped authorization/contract before execution.
- There is no separate permission for post-snapshot test-row mutation, and no
  row-level or bounded fixture specification. Do not infer it from restore or
  backup permission.
- For this runner, `FaultInjection=false`, `CrashBoundaries=[]`, and
  `PostSnapshotWriteCases=false` are mandatory. Crash and post-snapshot
  mutation tests require separate authorization and implementation.
- The four `stage.py` wrappers remain plan-only. A separate
  `scripts/cw-m5-026b/rehearse-m5-026b.sh` runner implements a narrow,
  authorization-gated snapshot/restore/table-content readback path. It does
  not perform full schema/FK/audit/recovery verification, crash tests, fixture
  seeding, migrations, or post-snapshot reconciliation.
- The required `CredentialReference`, `SnapshotPlan.ArtifactDirectory`, and
  credential transport (`option-file` or `secret-store`) need operator
  supplied protected references. The rehearsal preflight cannot prove that
  the referenced credentials work or have least privilege.
- Current `InstanceIdentity` approvals must include the exact host/UID,
  container ID, immutable image digest, and provisioning run ID. The source
  provisioning evidence recorded the source app identity as not schema-scoped;
  the executor rejects global grants, and source schema/table readiness and
  destination emptiness remain unverified.
- `--single-transaction` gives consistent snapshot semantics only for
  InnoDB tables and does not make concurrent DDL transactional. A nonzero dump,
  missing artifact, incomplete checksum, DDL uncertainty, or unknown table
  engine must abort. A successful import is not proof of restore correctness.
- Keep the acceptance claims separate: a transactionally consistent backup
  needs verified InnoDB/no-DDL conditions; a restorable backup needs a complete
  artifact and matching checksum plus a successful import; restored category
  integrity needs the full schema/index/revision/audit readback; and
  post-snapshot writes are not automatically recovered without an explicit
  journal/reconciliation decision.
- There is no cross-store atomicity, exactly-once replay, automatic recovery
  of post-snapshot writes, or lossless rollback guarantee. Divergence requires
  explicit reconciliation; retain artifacts and failure evidence without
  automatic cleanup.

## Required checks and abort thresholds

Before any live executor exists or is approved, require fresh target identity,
loopback binding, dedicated grant scope, credentials metadata/modes without
reading secret values, MariaDB engine/version, source and destination
database/table inventories, and explicit proof that the restore destination
is empty. Abort on any mismatch, shared/production scope, unknown target
state, nonempty/ambiguous destination, missing source schema/fixtures,
non-InnoDB table, concurrent DDL, authorization expiry, partial dump,
checksum mismatch, import interruption, schema/ledger mismatch, orphan/FK
failure, revision/provenance mismatch, missing audit, or unreconciled
post-snapshot writes. Preserve partial artifacts and diagnostics; do not
delete or overwrite resources.

The verification readback must cover Page/PageRevision/tags/references,
Page and category schema ledgers, index-state distinctions (unindexed versus
indexed-empty, populated, incomplete, failed, unknown), membership ordering
and generation, recovery checkpoint/CAS and audit sequences, grants and grant
audit state, foreign keys/orphans, aggregate counts and redacted stable hashes,
and source-versus-restored post-snapshot divergence. Keep private rows and
credential values out of reports.

## Existing command sequence for operator review — NOT EXECUTED

These entrypoints generate nonconnecting plans only. Placeholder paths below
must be replaced only after approval defines the exact manifest, private
artifact, and report locations. The current scripts do not perform the live
backup, restore, or verification.

```bash
scripts/preflight-m5-026b.sh \
  --manifest /path/to/cw-m5-026b-authorization.json \
  --report /path/to/cw-m5-026b-preflight.json

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

No command above was run in preparing this packet. A proper Draft 2020-12
validation and successful nonconnecting preflight would still not authorize
execution by the plan-only scripts or fill the contract gaps listed above.
The authorized operator must use the normal host-side process for any future
live work; Codex sandbox access is not assumed.

## Classification and evidence

Preparation classification: `CATEGORY_BACKUP_RESTORE_REHEARSAL_AUTHORIZATION_PREPARED_SOURCE_VERIFIED` only after the source-safe tests and artifact/schema checks recorded in the handoff report pass. Rehearsal authorization and execution remain absent; backup, restore, and certification are `NOT_EXECUTED`. Production migration and repository activation remain `NO_GO`.

Machine-readable status and evidence references:
`artifacts/validation/mediawiki-category-backup-restore-rehearsal-authorization-handoff-v1.json`.
