# CW-M5-026B-LIVE — Provisioning Authorization Worksheet

## Status

`APPROVAL RECORDED / MANIFEST INCOMPLETE`

The operator approved the bounded two-target disposable provisioning scope in
the conversation. This worksheet is still not an authorization manifest and
does not authorize execution by itself. Missing schema-required values must be
established before the authoritative manifest can be created or preflighted.
The provisioning contract is separate from the rehearsal contract so that
target creation cannot implicitly authorize a database backup, restore,
migration, certification, or cleanup.

## Operator must supply

| Field | Required value/evidence |
| --- | --- |
| Approver name and role | Genuine project/change approver |
| Operator name and role | Genuine operator who will run the command |
| Target owner | Accountable disposable-target owner |
| Approval ID | Unique bounded identifier |
| ApprovedAtUtc / ExpiresAtUtc | Actual UTC timestamps; finite validity |
| HostName | Exact authorized host |
| Rootless Podman proof | Operator evidence that rootless mode is active |
| MariaDB image reference | Immutable `docker.io/library/mariadb@sha256:<64 hex>` digest for 11.8.x |
| Source planned target | Exact role-scoped container, volume, database, loopback endpoint, app identity, credential directory, identity prefix |
| Destination planned target | Distinct exact role-scoped container, volume, database, loopback endpoint, app identity, credential directory, identity prefix |
| Exclusions | Prior M5-019/M5-025/operator4/production/shared prefixes and exact names |
| Provisioning permissions | Source and destination provisioning, isolation verification, sanitized evidence only |
| Prohibited permissions | Cleanup, schema deletion, container/volume deletion all explicitly false |

The manifest must also state `FreshTargetsRequired=true`,
`ReuseExistingResources=false`, loopback-only binding, dedicated credentials,
and all rehearsal/backup/restore/schema-migration/certification rights false.

## Nonconnecting validation

After the genuine provisioning manifest exists at an operator-selected private
path, validate it without contacting the host runtime:

```bash
scripts/preflight-m5-026b-provisioning.sh \
  --manifest /private/path/cw-m5-026b-provisioning-authorization.json \
  --report /private/path/cw-m5-026b-provisioning-preflight.json
```

The preflight must pass before any live provisioning command. It does not read
credentials, Podman state, or database state.

## Plan-only and live command boundaries

Plan-only is the default and is safe to run after preflight:

```bash
scripts/cw-m5-026b/provision-targets-m5-026b.sh \
  --manifest /private/path/cw-m5-026b-provisioning-authorization.json \
  --metadata /private/path/mediawiki-category-backup-restore-provisioning-v1.json
```

Only the authorized operator may choose to run the live provisioning boundary:

```bash
scripts/cw-m5-026b/provision-targets-m5-026b.sh \
  --manifest /private/path/cw-m5-026b-provisioning-authorization.json \
  --metadata /private/path/mediawiki-category-backup-restore-provisioning-v1.json \
  --provision --confirm-provisioning
```

The live command requires rootless Podman, the exact immutable image digest,
unused loopback ports, absent exact resources, new owner-only credential
directories outside the repository, and fresh empty MariaDB targets. It
retains resources on failure and never deletes anything. It records credential
paths only; values are never placed in metadata, logs, or arguments.

It performs provisioning health/identity/grant/empty-schema checks only. It
does not apply Page/V1/V2/V3 schema, seed fixtures, create a backup, restore,
run certification, activate repositories, or clean up.

## Handoff after successful provisioning

The operator must independently inspect the nonsecret provisioning evidence,
then create a separate exact-target CW-M5-026B rehearsal authorization using
the actual container IDs, image digests, endpoints, databases, application
identities, and credential references. The rehearsal manifest must pass
`scripts/preflight-m5-026b.sh` before any database connection. It must not
reuse this provisioning approval as backup/restore authority.

Production migration and provider activation remain `NO_GO`.
