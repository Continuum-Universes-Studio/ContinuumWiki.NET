# CW-M5-019 — Category Persistence Certification Readiness

## Classification

`CATEGORY_CERTIFICATION_READINESS_SOURCE_VERIFIED`

This is a source-only authorization and certification package. No MariaDB or
MySQL connection, schema provisioning, migration, container, category
mutation, recovery mutation, or provider activation was performed. Production
migration and category-provider activation remain `NO_GO`.

CW-M5-019A adds the separate operator-run disposable MariaDB 11.8.x
provisioning, inspection, and explicitly authorized cleanup workflow in
CW-M5-019A-DISPOSABLE-MARIADB-PROVISIONING.md. Provisioning remains distinct
from authorization and does not change the nonconnecting preflight contract.

## Authorization boundary

The version-controlled contract is
`docs/operations/cw-m5-019-authorization-manifest.schema.json`. An operator
must provide a separate manifest instance; this repository does not contain a
real approval or credential-bearing manifest.

The manifest requires:

- a versioned approval ID, UTC approval/expiry timestamps, approver, operator,
  and target owner;
- a disposable `cw_m5_019_*` database, loopback unprivileged endpoint,
  instance identity, MariaDB 11.8.x range, and dedicated application
  identity;
- explicit `true` permission for connection, V1/V2 migrations, category
  mutations, concurrency/crash tests, and sanitized evidence retention;
- explicit boolean permissions for disposable schema creation, disposable
  schema deletion, database cleanup, and Podman container/volume deletion;
  destructive permissions may be `false` and are never implied by
  certification permission;
- production/shared-target exclusion, dedicated credentials, isolation proof,
  provisioning and cleanup ownership; and
- the exact bounded test scope, maximum concurrency, 100-page recovery bound,
  and all five modeled crash boundaries.

The validator rejects missing, expired, production/shared, non-loopback,
wrong-prefix, wrong-engine, wrong-identity, incomplete-operation, incomplete-
crash-scope, secret-bearing, or otherwise malformed manifests. Denials are
stable field/code pairs and never echo input values.

## Nonconnecting preflight

Run the preflight only against an operator-supplied manifest file:

```bash
scripts/preflight-m5-019.sh \
  --manifest /secure/operator/cw-m5-019-authorization.json \
  --report /secure/evidence/cw-m5-019-preflight.json
```

The preflight is inspection-only. It uses Python's standard library, does not
read connection environment variables, instantiate a connector, inspect a
host, invoke Podman, run migrations, or start tests. It reports
`database_connections: 0`, `database_mutations: 0`, and
`live_certification: NOT_EXECUTED`. A valid manifest only produces a proposed
plan; it does not authorize or execute Track B.

The preflight output contains only hashes of approval/target identity values,
validation codes, fixed proposed steps, and production `NO_GO` status. It never
copies endpoints, connection strings, passwords, tokens, page identifiers, or
category counts into evidence.

## Designed certification sequence

These are future operator steps, not steps executed by CW-M5-019:

1. Review the approval manifest, expiry, exact target identity, owner, and
   cleanup scope.
2. Run the nonconnecting preflight and retain its report.
3. In a separately authorized live phase, connect using the dedicated
   application identity and prove the exact MariaDB 11.8.x version, loopback
   endpoint, disposable database, and isolation boundary before mutation.
4. Apply the existing base schema, then category V1, then recovery V2, and
   record migration hashes and schema read-back.
5. Run the gated category persistence and recovery durability integration
   tests with bounded concurrency and the approved test scope.
6. Capture redacted CAS, transaction, replay, visibility, cancellation, and
   cleanup evidence.
7. Verify operation/audit cleanup and disposable-target destruction, then
   obtain independent reviewer signoff.

The live phase must fail closed on target identity/version mismatch. A
connection string or environment variable alone is never approval.

## Coverage matrix

| Behavior | Source coverage | Live certification |
| --- | --- | --- |
| Category V1 schema and independent index state | Migration, repository, unit tests | Not executed |
| Indexed-empty/unindexed distinction | Typed repository/service tests | Not executed |
| Revision-locked replacement and rollback | Repository implementation and tests | Not executed |
| Deterministic ordering/pagination | Visibility-safe repository and browse tests | Not executed |
| Visibility before page slicing/count exposure | Repository/query tests | Not executed |
| Delete invalidation and restore reindex | Lifecycle/application tests | Not executed |
| Recovery V2 operation/audit schema | Migration and contract tests | Not executed |
| Audit idempotence and 4,096-event budget | Provider source and contract tests | Not executed |
| Checkpoint/status generation CAS | Provider source and M5-017 synthetic replay tests | Not executed |
| Actor/scope/version validation | Typed provider and synthetic tests | Not executed |
| Crash windows and uncertain outcomes | Recoverable two-phase source protocol | Not executed; requires live fault injection/reconciliation |
| Concurrent recovery instances | CAS contract only | Not executed |
| Lease owner/expiry races | Not modeled by current contract | Not applicable until contract exists |
| Default rejection and explicit readiness activation | DI/activation contract tests | Not executed |
| Cleanup and evidence attestation | Runbook and manifest contract | Not executed |

The category mutation transaction and recovery audit/checkpoint transaction are
separate. No evidence may describe them as a shared atomic transaction. An
uncertain category mutation must be reconciled against authoritative index
state before checkpoint advancement.

## Threat model and retained boundaries

The package retains the rejecting category repository and rejecting durability
store as defaults, does not add an admin route, worker, queue, scheduler,
automatic retry, Lua/Cargo behavior, or production setting, and does not alter
historical revision handling. The live runner remains the existing explicit
integration-test path; this milestone adds no alternate path that can turn a
connection variable into permission.

## Current blockers

- No approved disposable MariaDB 11.8 target or operator manifest instance was
  supplied.
- No live target identity/isolation/version proof exists.
- V1/V2 migrations, transaction/CAS, visibility, crash/replay, concurrency,
  and cleanup evidence remain unexecuted.
- Production migration and category provider activation remain `NO_GO`.
