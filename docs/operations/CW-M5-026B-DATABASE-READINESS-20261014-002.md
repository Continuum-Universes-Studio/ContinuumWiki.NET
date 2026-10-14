# CW-M5-026B — Two-Target Database Readiness Assessment

**Option-file binding:** PASS  
**Current container identity:** PASS  
**Current container runtime state:** FAIL  
**Database readiness:** UNKNOWN / BLOCKED  
**Backup/restore certification:** NO_GO

## Credential path investigation

The consumed authorization `CW-M5-026B-INSPECT-20261013-009` explicitly
listed `client.cnf` for both targets. The credential-preparation implementation
creates `inspection-client.cnf` with create-only semantics, its authorization
contract binds that basename, and the inspection runner consumes the supplied
`CredentialOptionFile` path without rewriting it. No inspected code converts
the approved name to `client.cnf`; the incorrect basename was in the inspection
manifest itself. The retained packet and worksheet specify `inspection-client.cnf`.

Current metadata-only checks established:

| Target | Path | File metadata | Parent metadata |
| --- | --- | --- | --- |
| Source | `/home/cdjensen94/.local/state/continuumwiki/cw-m5-026b/source-ro1/inspection-client.cnf` | canonical regular file, not a symlink, UID 1000, mode `0600` | canonical directory, not a symlink, UID 1000, mode `0700` |
| Destination | `/home/cdjensen94/.local/state/continuumwiki/cw-m5-026b/destination-ro1/inspection-client.cnf` | canonical regular file, not a symlink, UID 1000, mode `0600` | canonical directory, not a symlink, UID 1000, mode `0700` |

File contents were not read. No credential conversion or file write occurred.
The inspection schema and nonconnecting preflight now require the exact
`inspection-client.cnf` basename; the live runner independently enforces
canonical paths, owner, and permissions.

## Current target observations

Read-only Podman inspection at `2026-10-14T16:57:37Z` found both expected
container IDs in `exited` state with exit code 0. Each still has the expected
pinned MariaDB image, target-specific database and RunId labels, named volume
mounted at `/var/lib/mysql`, and configured loopback port:

| Target | Container ID | RunId | Configured endpoint | State |
| --- | --- | --- | --- | --- |
| Source | `6a4d4f8707ee4f5b14dddfff183db6d26ce7453eaba377b7c9d081bc1157f914` | `20261012T093746Z-86380` | `127.0.0.1:13326` | `exited`, code 0 |
| Destination | `1c58a2f370d36af443e53283d4212604e997188e9fbf81175655446b7c3ea1f5` | `20261012T123438Z-282573-599fff18456ea0cf36ef987019b3efef` | `127.0.0.1:13327` | `exited`, code 0 |

## Inspection and readiness

The most recent fresh inspection authorization was
`CW-M5-026B-INSPECT-20261014-001`; it passed JSON Schema Draft 2020-12
validation and nonconnecting preflight. Its single inspection attempt ended
with `CONTAINER_NOT_RUNNING` before database connection. It recorded zero
database connections or queries, zero credential-value reads or writes, and
zero mutations, migrations, grant changes, fixture writes, backups, restores,
or cleanup operations.

| Criterion | Status | Observation |
| --- | --- | --- |
| Source container ID, image, labels, volume, configured endpoint | PASS | Current read-only Podman identity matched the expected source binding. |
| Destination container ID, image, labels, volume, configured endpoint | PASS | Current read-only Podman identity matched the expected destination binding and independent RunId. |
| Both option-file paths and permissions | PASS | Both approved files meet the runner’s metadata checks. |
| Source runtime availability | FAIL | Source is exited; inspection could not connect. |
| Destination runtime availability | FAIL | Destination is exited; inspection could not connect. |
| Database identities and grants | UNKNOWN | No database connection/query occurred. |
| Required schema objects, migration ledgers | UNKNOWN | No database query occurred; expected inventory (21 tables, 6 ledger entries, 19 foreign keys, 22 secondary indexes) remains unverified. |
| Source fixture sufficiency | UNKNOWN | No database query occurred. |
| Destination emptiness | UNKNOWN | No database query occurred. |
| Backup/restore readiness | FAIL | The read-only inspection has not established database readiness. |

## Remaining blocker

Both containers stopped after their earlier recovery. Starting either container
is a new lifecycle operation; prior one-shot start authorizations were consumed.
The current request prohibits unapproved starts, so this assessment does not
prepare or execute another start. A new, exact-target approval for one
`podman start` of each existing container is required. After both are confirmed
running with their expected identities and endpoints, a fresh independent
two-target inspection authorization and preflight are required. The consumed
inspection authorization above cannot be reused.

No database mutation, grant change, migration, fixture write, backup, restore,
cleanup, production operation, or repository activation is authorized.

## Retained inspection evidence

| Evidence | SHA-256 |
| --- | --- |
| Authorization | `/home/cdjensen94/continuumwiki-validation/cw-m5-026b-inspection-CW-M5-026B-INSPECT-20261014-001/authorization.json` — `882d429e260b67a989d2c8659c7e383ca5eec3f47d72acc151e77bb8d1af1535` |
| Nonconnecting preflight | `/home/cdjensen94/continuumwiki-validation/cw-m5-026b-inspection-CW-M5-026B-INSPECT-20261014-001/preflight.json` — `eeeca8aab72066da8b5d4da3c3370c1f65cccdce88ff76287862aee1047cc7ca` |
| Inspection result | `/home/cdjensen94/continuumwiki-validation/cw-m5-026b-inspection-CW-M5-026B-INSPECT-20261014-001/result.json` — `9f9cc796a0cf4e16976fb97e3dd76f64f0a8536ea4a4635dc49e99bb92cef143` |

The earlier consumed inspection manifests and reports remain preserved.
Production migration and repository activation remain `NO_GO`.
