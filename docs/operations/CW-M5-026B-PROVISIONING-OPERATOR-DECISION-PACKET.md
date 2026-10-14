# CW-M5-026B-PROVISION — Operator Authorization Decision Packet

## Decision status

`PROVISIONING_FAILED_RESOURCES_RETAINED`

The timestamped operator approval was converted into the authoritative
manifest and passed the nonconnecting preflight. The controlled provisioning
attempt then failed during source runtime identity/version verification. The
source container and volume were retained; the destination was not created.
No cleanup was performed.

The source preparation is verified as
`CATEGORY_BACKUP_RESTORE_PREPARATION_SOURCE_VERIFIED`. Live provisioning has
not been run for this handoff.

## Contract and source of truth

The provisioning contract is
`cw-m5-026b-provisioning-authorization-v1`, defined by
`docs/operations/cw-m5-026b-provisioning-authorization-manifest.schema.json`.
The operator worksheet is
`docs/operations/CW-M5-026B-LIVE-PROVISIONING-AUTHORIZATION-WORKSHEET.md`.

The later rehearsal is a separate approval boundary. The provisioning
approval must have `RehearsalRightsGranted=false`, and it cannot be reused as
authority for backup, restore, schema migration, or certification.

## Recorded approval and remaining manifest requirements

Recorded decision: approval of two new, distinct, rootless-Podman, loopback-
only, disposable MariaDB 11.8.x targets, with dedicated credentials and
sanitized provisioning evidence only. Rehearsal, backup, restore, schema
migration, certification, cleanup, schema deletion, and container/volume
deletion are not approved.

The decision does not establish the following schema-required values. They
must be supplied or independently verified before an authoritative manifest
can be created:

The operator must provide, through the existing approval process:

- approver name and role, operator name and role, and target owner;
- a unique approval ID;
- actual finite `ApprovedAtUtc` and `ExpiresAtUtc` values;
- the exact authorized host name and rootless-Podman evidence;
- the immutable `docker.io/library/mariadb@sha256:<64 hex>` reference for
  MariaDB 11.8.x;
- exact, distinct source and destination plans, each including container,
  volume, database, loopback endpoint, application identity, owner-only
  credential directory, and identity prefix;
- exclusions covering production/shared resources and all prior M5-019,
  M5-025, and operator4 resources; and
- explicit provisioning/isolation/evidence permissions with cleanup, schema
  deletion, and container/volume deletion set to false.

Codex has not filled in or inferred any missing approval values. No prior
approval is transferable to this task, and no M5-019, M5-025, or operator4
resource may be reused.

## Required provisioning contract

The authoritative manifest must require fresh, non-shared targets,
loopback-only bindings, rootless Podman, dedicated credentials, an immutable
MariaDB image digest, and separate source/destination identities. It must
explicitly deny source and destination cleanup, schema deletion, and
container/volume deletion. Backup, restore, schema migration, certification,
and rehearsal rights must remain false.

The provisioning tool itself is bounded to health, identity, grant, isolation,
and empty-schema checks. It does not apply application schema, create a
backup, restore data, run certification, activate repositories, or clean up.

## Operator command sequence after genuine approval

Use private paths selected by the operator. Do not place credentials in the
repository or command arguments.

1. Create the authoritative manifest at
   `/private/path/cw-m5-026b-provisioning-authorization.json` using the
   contract above. This packet is not that manifest.

2. Run the nonconnecting preflight first:

   ```bash
   scripts/preflight-m5-026b-provisioning.sh \
     --manifest /private/path/cw-m5-026b-provisioning-authorization.json \
     --report /private/path/cw-m5-026b-provisioning-preflight.json
   ```

3. If and only if that preflight passes, the operator may run the live
   provisioning boundary:

   ```bash
   scripts/cw-m5-026b/provision-targets-m5-026b.sh \
     --manifest /private/path/cw-m5-026b-provisioning-authorization.json \
     --metadata /private/path/mediawiki-category-backup-restore-provisioning-v1.json \
     --provision --confirm-provisioning
   ```

   The tool must retain resources on failure and does not delete containers,
   volumes, schemas, or databases.

4. Inspect the resulting nonsecret metadata and verify actual container IDs,
   image digests, MariaDB identity/version, loopback bindings, fresh empty
   schemas, dedicated identities, and credential-path ownership.

5. Only after those exact targets exist, create a separate exact-target
   rehearsal manifest. Run its nonconnecting preflight with
   `scripts/preflight-m5-026b.sh` before any rehearsal database connection.

The live command was executed under the approved manifest. Its sanitized
result is recorded in
`secure/evidence/mediawiki-category-backup-restore-provisioning-v1.json`.

## Runtime identity diagnostic observability

The source verifier now separates the runtime query into pure parsing,
predicate evaluation, and sanitized diagnostic functions in
`scripts/cw-m5-026b/provision_targets.py`. A future authorized run will
classify command failure, malformed or ambiguous output, exact database-name
failure, `11.8.` version-family failure, and the case-insensitive
`MariaDB` version-comment failure independently.

The parser is fail-closed: it requires one tab-separated row with exactly
three non-null, nonempty fields, rejects extra rows/delimiters and
leading/trailing field whitespace, and performs no padding or repair. Failure
metadata contains codes, predicate states, bounded field-shape facts, and an
exit code where applicable; it does not contain raw tuple values, stderr,
credentials, or SQL output. The exact database-name and `11.8.` version-family
predicates, plus all later root-host, schema-scope, grant-option, and
empty-schema checks, are unchanged.

The authorized one-shot diagnostic confirmed database-name and version-family
matches, while the former case-sensitive version-comment check failed even
though the comment matched after `casefold()`. The source verifier now uses
that case-insensitive comparison. This fixes the confirmed verifier defect;
it does not reprovision or revalidate the retained container.

The source-only regression suite passed 28 tests. The live classification
remains `PROVISIONING_FAILED_RESOURCES_RETAINED`; the source correction is
verified separately as `CATEGORY_PROVISIONING_VERSION_COMMENT_FIX_SOURCE_VERIFIED`.
No retry, database mutation, destination creation, credential change, or
resource cleanup was performed.

## Explicitly prohibited in this state

- creating or guessing the authoritative approval manifest from the recorded
  scope alone;
- using an approval from M5-019, M5-025, operator4, or another target;
- contacting Podman, MariaDB, or an existing database;
- applying schema, backup, restore, or certification operations;
- deleting or cleaning any schema, database, container, or volume;
- enabling production migration, repository activation, Lua, Cargo, or worker
  changes.

## Current verdict

`PROVISIONING_FAILED_RESOURCES_RETAINED`

Final state: provisioning was attempted after valid authorization, but failed
during source runtime identity/version verification. The retained source
resource requires diagnosis; the destination was not created. An independent
exact-target rehearsal approval remains unavailable. Production migration and
provider activation remain `NO_GO`.
