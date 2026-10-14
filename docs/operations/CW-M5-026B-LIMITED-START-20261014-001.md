# CW-M5-026B — Limited Container Start Results

**Authorization:** `CW-M5-026B-RECOVERY-START-20261014-001`  
**Source start:** PASS  
**Destination start:** PASS  
**Database readiness inspection:** NOT RUN — separate authorization required

## Pre-start identity verification

Fresh read-only diagnostics verified both exact targets before starting them.
Each diagnostic returned its stopped identity-match classification, confirmed
the expected ID, pinned image, RunId and database labels, volume, and configured
loopback endpoint, and recorded six read-only Podman commands. Neither
diagnostic accessed MariaDB or credentials.

| Target | ID | RunId | Pre-start result |
| --- | --- | --- | --- |
| Source | `6a4d4f8707ee4f5b14dddfff183db6d26ce7453eaba377b7c9d081bc1157f914` | `20261012T093746Z-86380` | `SOURCE_CONTAINER_STOPPED_IDENTITY_MATCH` |
| Destination | `1c58a2f370d36af443e53283d4212604e997188e9fbf81175655446b7c3ea1f5` | `20261012T123438Z-282573-599fff18456ea0cf36ef987019b3efef` | `DESTINATION_CONTAINER_STOPPED_IDENTITY_MATCH` |

## Start and post-start verification

The source runner executed exactly one `podman start` and returned
`SOURCE_CONTAINER_START_SUCCEEDED` (exit code 0). Its immediate six-command
read-only verification returned `SOURCE_CONTAINER_RUNNING_IDENTITY_MATCH`.

The destination runner then executed exactly one `podman start` and returned
`DESTINATION_CONTAINER_START_SUCCEEDED` (exit code 0). Its immediate
six-command read-only verification returned
`DESTINATION_CONTAINER_RUNNING_IDENTITY_MATCH`.

Both verification reports matched the exact container ID, pinned image,
target labels, volume mount and identity, and configured endpoint, with no
findings. There were no database connections, mutations, credential reads,
retries, or additional lifecycle commands. Existing containers, volumes,
images, and configuration were retained.

## Retained evidence

All files are in owner-only directories (mode `0700`) and are owner-only files
(mode `0600`). The operation and verification reports do not persist raw
Podman output or stderr.

| Target / evidence | Path | SHA-256 |
| --- | --- | --- |
| Source diagnostic | `/home/cdjensen94/continuumwiki-validation/cw-m5-026b-source-diagnostic-CW-M5-026B-DIAGNOSTIC-20261014-008/result.json` | `5fd65332ae0eb5037ef643914ffc34aee6d006322500f9305d8f33221684fa40` |
| Destination diagnostic | `/home/cdjensen94/continuumwiki-validation/cw-m5-026b-destination-diagnostic-CW-M5-026B-DIAGNOSTIC-20261014-009/result.json` | `646e3f914064a1a12034657c4dfe57ddc78b3207da0c6bd268a63f1a01c547bd` |
| Source authorization | `/home/cdjensen94/continuumwiki-validation/cw-m5-026b-source-start-CW-M5-026B-RECOVERY-START-20261014-001/authorization.json` | `ebacfc28a62a0cb1c8f7973b95f7d1018f6255d1ecef153725d16670cb0b4dc1` |
| Source preflight | `/home/cdjensen94/continuumwiki-validation/cw-m5-026b-source-start-CW-M5-026B-RECOVERY-START-20261014-001/preflight.json` | `3373da034ea551a21a7352f28dfa04309de93716ac75d75fac529aca84c35278` |
| Source start result | `/home/cdjensen94/continuumwiki-validation/cw-m5-026b-source-start-CW-M5-026B-RECOVERY-START-20261014-001/result.json` | `1a8cbad96542fabd8400692278daf06a0c08dcbcf01fa45948693e62c1213a59` |
| Source verification | `/home/cdjensen94/continuumwiki-validation/cw-m5-026b-source-start-CW-M5-026B-RECOVERY-START-20261014-001/verification.json` | `2b2a276bf00dfe1396e89325be7afcf0d6e0f07be25a1a526b6bcfc15db4dd5b` |
| Destination authorization | `/home/cdjensen94/continuumwiki-validation/cw-m5-026b-destination-start-CW-M5-026B-RECOVERY-START-20261014-001/authorization.json` | `e1d4407e17044756fdfae14983c7fc5fe812aea7de8d57f238fd582c23114e6f` |
| Destination preflight | `/home/cdjensen94/continuumwiki-validation/cw-m5-026b-destination-start-CW-M5-026B-RECOVERY-START-20261014-001/preflight.json` | `5b0d9047c39b2da863985b019e9858bcc9a7f69c4ad984b41c38a1597105a0bc` |
| Destination start result | `/home/cdjensen94/continuumwiki-validation/cw-m5-026b-destination-start-CW-M5-026B-RECOVERY-START-20261014-001/result.json` | `ca0fa011aec9e3533dca37f01f6fd36cddae4015041589716a9597ff8229df2b` |
| Destination verification | `/home/cdjensen94/continuumwiki-validation/cw-m5-026b-destination-start-CW-M5-026B-RECOVERY-START-20261014-001/verification.json` | `077cab92c8524fbb64eae1df031c0140271d277de80381cd15abfdda09d01b8a` |

The start authorization is consumed. A new independent, two-target, read-only
database-inspection authorization and nonconnecting preflight are required to
query identities, grants, schema, migration ledgers, fixtures, and destination
emptiness. Production migration and repository activation remain `NO_GO`.
