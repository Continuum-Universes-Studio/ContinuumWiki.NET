# CW-M5-026B — Destination Recovery Blocker

**Diagnostic approval:** `CW-M5-026B-DIAGNOSTIC-20261013-005`  
**Diagnostic result:** `DESTINATION_CONTAINER_IDENTITY_MISMATCH`  
**Destination start:** `NOT EXECUTED`  
**Two-target database inspection:** `NOT EXECUTED`  
**Backup/restore certification:** `NO_GO`

## Evidence-backed result

The existing stopped-container diagnostic/start-gate architecture was
extended only for the exact retained destination target. The fresh diagnostic
authorization passed its nonconnecting preflight and the bounded diagnostic
executed once on the normal rootless Podman host at
`2026-10-13T05:23:43.105667Z`.

The destination was observed as `exited` with exit code `0`. These checks
passed:

- expected container ID;
- pinned image identity;
- database label;
- `/var/lib/mysql` volume mount; and
- configured loopback endpoint `127.0.0.1:13327`.

The exact-target gate failed on:

- `PROVISIONING_RUN_ID_LABEL_MISMATCH`; and
- `VOLUME_IDENTITY_MISMATCH`.

The sanitized report contains no raw Podman output, stderr, credentials, or
database data. It recorded six read-only Podman commands, zero database
connections, zero queries, and zero mutations.

## Retained evidence

| Evidence | Path | SHA-256 |
| --- | --- | --- |
| Fresh diagnostic authorization | `/home/cdjensen94/continuumwiki-validation/cw-m5-026b-destination-diagnostic-CW-M5-026B-DIAGNOSTIC-20261013-005/authorization.json` | `eda6e15c28f11a265d52b5eaa9414157c54c6b4c157f670053353147a1bab1a9` |
| Nonconnecting preflight | `/home/cdjensen94/continuumwiki-validation/cw-m5-026b-destination-diagnostic-CW-M5-026B-DIAGNOSTIC-20261013-005/preflight.json` | `48bebad0233f862b91145bea35a7803c067fecc405b30d6322fa3d8c0a17a54e` |
| Sanitized diagnostic result | `/home/cdjensen94/continuumwiki-validation/cw-m5-026b-destination-diagnostic-CW-M5-026B-DIAGNOSTIC-20261013-005/result.json` | `1cbd691de661b9d58e94b593b822999f1950b4edcd45ed2eacbe78ee4dead132` |
| One-shot claim marker | `/home/cdjensen94/continuumwiki-validation/cw-m5-026b-destination-diagnostic-CW-M5-026B-DIAGNOSTIC-20261013-005/result.json.started` | `266b10d2a3a6b36956e4056550d9d7d7bacc085f7fae7726ed7ff3137e0b2d4f` |

Earlier invalid destination diagnostic manifests and preflights remain
preserved under their distinct `...-003` and `...-004` directories. They did
not execute Podman and are not reused.

## Readiness status

| Item | Status | Reason |
| --- | --- | --- |
| Destination stopped-state observation | PASS | The exact retained destination was observed as exited, exit code 0. |
| Destination exact identity | FAIL | Run ID label and volume identity did not match the authorized provisioning identity. |
| Destination start authorization | BLOCKED | The start schema requires `DESTINATION_CONTAINER_STOPPED_IDENTITY_MATCH`; the diagnostic produced an identity mismatch. |
| Destination running identity/endpoint | UNKNOWN | No start was executed, so no post-start verification exists. |
| Source current database identity/grants/schema/ledgers/fixtures | UNKNOWN | No fresh two-target database inspection was executed in this turn. |
| Destination database identity/grants/schema/ledgers/emptiness | UNKNOWN | The identity gate failed before any database connection. |
| Backup/restore rehearsal readiness | FAIL | Exact destination identity is not established and database readiness is unknown. |

## Minimum remaining work

The minimum work is to resolve the destination identity mismatch through a
separately authorized, non-destructive recovery decision. The current
constraints prohibit repairing labels, changing the volume, recreating the
container, or deleting anything, so `podman start` cannot be safely authorized
from this evidence.

After an exact destination identity is restored or a new exact target is
explicitly approved, a fresh destination diagnostic and start authorization
are required. Only after successful post-start identity verification may a
fresh independent two-target read-only inspection establish grants, schema,
migration ledgers, source fixture sufficiency, and destination emptiness.

Production migration and repository activation remain `NO_GO`.

## Follow-up identity decision

The later exact-target diagnostic `CW-M5-026B-DIAGNOSTIC-20261013-006`
resolved the ambiguity without changing this historical report. Both the
destination container and volume RunId labels were present and matched each
other by sanitized SHA-256, but both differed from the original provisioning
RunId expected by the diagnostic. The continuation source generated a fresh
destination RunId and wrote it to both labels, while the continuation result
did not retain that destination-specific value and the inspection preflight
reused the original RunId for Destination.

This is an expected-identity binding/evidence defect, not a reason to weaken
the exact comparer. The current destination remains ineligible for start
under the historical target contract. The consolidated decision and fresh
evidence hashes are recorded in
`docs/operations/CW-M5-026B-DESTINATION-IDENTITY-DECISION-20261013-006.md`.
