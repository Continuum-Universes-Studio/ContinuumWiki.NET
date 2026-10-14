# CW-M5-026B — Destination RunId and Volume Identity Decision

**Outcome:** A — expected-identity binding/evidence defect identified. The
low-level exact comparison is correct and remains fail-closed.

**Destination start:** NOT AUTHORIZED / NOT EXECUTED  
**Database connections:** 0  
**Backup/restore certification:** NO_GO

## Current evidence

A fresh authorization, `CW-M5-026B-DIAGNOSTIC-20261013-006`, passed the
nonconnecting preflight and executed once on `christian-laptop` at
`2026-10-13T05:53:17.172157Z`. The bounded diagnostic used six read-only
Podman commands. It recorded the retained destination as `exited`, exit code
`0`, with the expected container ID, immutable image, database label, volume
mount, and configured endpoint all matching.

The two failed checks are distinct fields but have one cause:

| Check | Result | Sanitized evidence |
| --- | --- | --- |
| Container RunId vs authorized expected RunId | FAIL | label present; observed SHA-256 `1a149f4084cdc64e394deb4c507dd06e1c196dd967cf856f9c6fc4d91b78fe56` |
| Volume RunId vs authorized expected RunId | FAIL | label present; observed SHA-256 `1a149f4084cdc64e394deb4c507dd06e1c196dd967cf856f9c6fc4d91b78fe56` |
| Container RunId vs volume RunId | PASS | the two observed hashes are equal |

The expected original provisioning RunId is
`20261012T093746Z-86380`; its SHA-256 is
`2f5af9c9928e8532c8f8949b88e9322f8730820b2da32636dc02de4d0e0c795c`, which
differs from the observed hash. The raw alternate RunId was not persisted.
No raw Podman output, stderr, credentials, or database data was retained.

Therefore this is not an absent-label case, not a container/volume divergence,
and not evidence that the container was recreated. It is one shared alternate
destination RunId being compared with the wrong expected RunId.

## Source-level cause

Retained source review establishes the defect precisely:

1. `scripts/cw-m5-026b/continue_targets.py` creates a fresh
   `destination_run_id` for the continuation and passes it to
   `provision_one`.
2. `scripts/cw-m5-026b/provision_targets.py` writes the supplied RunId to
   both the destination volume label and the destination container label.
3. The continuation result persists the destination identity and volume but
   does not persist the destination-specific RunId.
4. `scripts/preflight-m5-026b-inspection.py` reads only the original
   provisioning RunId and assigns it to both Source and Destination.
5. `scripts/cw-m5-026b/stopped_source_diagnostic.py` compares each observed
   value using exact equality. Its `PROVISIONING_RUN_ID_LABEL_MISMATCH` and
   `VOLUME_IDENTITY_MISMATCH` findings are consequently correct for the
   incorrectly bound expected target.

The comparison logic must not be weakened and was not weakened. The source
safe diagnostic enhancement retains only label presence, equality between
the two observed labels, and SHA-256 values. The full M5-026B Python
regression suite passes: **143 tests, 0 failures**.

## Consolidated remediation decision

Do not start the current destination under the original target binding. A
destination start authorization requires an exact identity-match
classification, which is not present.

The minimum safe work to enable a later recovery decision is:

- Correct the continuation evidence producer to retain a
  destination-specific provisioning RunId, and make inspection/preflight
  bind Destination only to that value, failing closed when it is absent.
- Generate a new exact-target destination authorization from that retained
  destination-specific identity. Do not rewrite the historical manifests or
  reports and do not change the current labels or volume metadata.
- Only after that independent authorization passes its stopped-identity gate
  may one destination start be considered, followed by separate post-start
  verification and a fresh two-target read-only database inspection.

For the current retained container, the hash-only diagnostic proves the
container and volume share the same alternate RunId but does not retain its
raw value. It therefore supports the root-cause decision but does not itself
authorize a start under the old manifest. No migration, grant change, fixture
write, backup, restore, cleanup, container recreation, deletion, or database
connection is permitted by this decision.

## Readiness disposition

| Area | Status | Basis |
| --- | --- | --- |
| Destination stopped-state observation | PASS | exact container observed exited, exit code 0 |
| Destination container/volume identity under current authorization | FAIL | both expected RunId comparisons fail |
| Shared container/volume RunId relationship | PASS | both labels present and equal by sanitized hash |
| Destination start eligibility | FAIL | exact stopped identity classification not satisfied |
| Source grants/schema/ledgers/fixtures | UNKNOWN | no fresh two-target database inspection in this decision |
| Destination grants/schema/ledgers/emptiness | UNKNOWN | destination was not started or connected |
| Backup/restore rehearsal readiness | FAIL | destination recovery gate and database readiness remain unresolved |

Production migration and repository activation remain `NO_GO`.

## Retained evidence

| Evidence | SHA-256 |
| --- | --- |
| `/home/cdjensen94/continuumwiki-validation/cw-m5-026b-destination-diagnostic-CW-M5-026B-DIAGNOSTIC-20261013-006/authorization.json` | `3fe666542e9bd58b97c2fcac0289d5290c683e2758761b505395a406db92d72e` |
| `/home/cdjensen94/continuumwiki-validation/cw-m5-026b-destination-diagnostic-CW-M5-026B-DIAGNOSTIC-20261013-006/preflight.json` | `72da24bf71a1f66ab8be803227d6fa40f81355ee4dc9372a186e75af737dfa0a` |
| `/home/cdjensen94/continuumwiki-validation/cw-m5-026b-destination-diagnostic-CW-M5-026B-DIAGNOSTIC-20261013-006/result.json` | `315b9f96f4eccb3d0ec25cdbb96c73ae197e4dc73cda57c8e75ce7952606f4a8` |
| `/home/cdjensen94/continuumwiki-validation/cw-m5-026b-destination-diagnostic-CW-M5-026B-DIAGNOSTIC-20261013-006/result.json.started` | `5f82cf504c447f2e900cd8fc48d5808d4ea629131bf609589e54ac41bfe22798` |

Earlier diagnostic evidence, including authorization `...-005`, remains
preserved and was not reused or overwritten.
