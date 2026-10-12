# CW-M5-019C — Operator Authorization and Controlled Certification

## Decision

`CATEGORY_OPERATOR_AUTHORIZATION_PENDING`

The operator4 evidence is internally consistent. The operator supplied
approval for certification-only operations and explicitly prohibited all
destructive cleanup operations. The authoritative v2 manifest was intentionally
not written by Codex, so no preflight, SQL, migration, certification test, or
cleanup operation was executed.

The source-only authorization contract now uses
`cw-m5-019-authorization-v2`. It requires these certification permissions to
be explicitly `true`:

`Connect`, `ApplyV1Migration`, `ApplyV2Migration`, `CategoryMutations`,
`ConcurrencyAndCrashTests`, and `RetainSanitizedEvidence`.

It separately requires boolean values for `CreateDisposableSchema`,
`DeleteDisposableSchema`, `DatabaseCleanup`, and `ContainerVolumeDeletion`.
For certification without deletion, the destructive permissions must be
explicitly `false`; legacy `DropDisposableSchema` and `Cleanup` keys are
rejected.

The scopes are intentionally distinct: `DeleteDisposableSchema` covers
category/recovery schema objects, `DatabaseCleanup` covers certification rows
inside MariaDB, and `ContainerVolumeDeletion` covers removal of the exact
Podman container and volume. Certification requires none of those deletion
permissions.

## Exact eligible target

These values are reconciled from the existing provisioning metadata and
read-only inspection report:

| Field | Value |
| --- | --- |
| Container | `continuumwiki-m5-019-operator4` |
| Volume | `continuumwiki-m5-019-operator4-data` |
| Database | `cw_m5_019_operator4` |
| Endpoint | `127.0.0.1:13321` |
| Image digest | `docker.io/library/mariadb@sha256:6422478cb8e159f080fb1d8ccf65101e26fe51385787fde7d16c3b165a331f15` |
| Observed engine | `11.8.9-MariaDB-ubu2404` |
| Application identity | `continuumwiki_m5_019_app` |
| Provisioning | `DISPOSABLE_TARGET_PROVISIONED_NOT_AUTHORIZED` |
| Inspection | `PROVISIONED_TARGET_READY_NOT_AUTHORIZED` |

The exact instance identity is the value recorded in the existing evidence;
it must be copied into the authoritative manifest, not reconstructed from the
nickname. No credentials are included here.

## Required operator decision

An authorized operator must review the exact target above and create
`secure/evidence/cw-m5-019-authorization.json` according to
`docs/operations/cw-m5-019-authorization-manifest.schema.json`, supplying
real values for:

- approval ID, approval and expiry UTC timestamps, approver name/role,
  operator name/role, and target owner;
- explicit permissions for connection, V1 migration, V2 migration, category
  mutations, concurrency/crash testing, and sanitized evidence retention;
- explicit boolean values for disposable schema creation, disposable schema
  deletion, database cleanup, and container/volume deletion;
- isolation proof, provisioning ownership, cleanup ownership, and the complete
  bounded test scope.

The operator has supplied certification scope and prohibited destructive scope
for this decision. Codex must not add the authoritative manifest, record an
approval timestamp, or infer cleanup consent on the operator's behalf.

## Next gate after approval

Only after the authoritative manifest exists and has been reviewed, run the
existing nonconnecting preflight:

```bash
scripts/preflight-m5-019.sh \
  --manifest "$PWD/secure/evidence/cw-m5-019-authorization.json" \
  --report "$PWD/secure/evidence/cw-m5-019-preflight.json"
```

The preflight must pass before any database connection. A passing preflight
does not itself authorize every operation; each live stage must remain within
the manifest scope and recheck target identity, engine version, endpoint, and
expiry. Cleanup remains separately gated.

## Current execution record

| Operation | Status |
| --- | --- |
| Authoritative manifest | Not supplied |
| Preflight | Not run; candidate was previously rejected with exit 3 |
| SQL / migrations | Not executed |
| Category certification | Not executed |
| Recovery durability certification | Not executed |
| Cleanup | `CLEANUP_NOT_AUTHORIZED` |
| Database contacts in CW-M5-019C | 0 connections, 0 mutations |
| Production migration/provider activation | `NO_GO` |

The category repository and recovery durability store remain rejecting by
default. The two category/recovery persistence transactions remain separate;
no shared atomic transaction is claimed.
