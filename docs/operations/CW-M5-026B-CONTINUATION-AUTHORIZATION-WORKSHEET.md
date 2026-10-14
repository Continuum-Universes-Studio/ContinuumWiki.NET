# CW-M5-026B — Retained-Source Continuation Authorization

The prior provisioning attempt retained the recorded source container, volume,
credential files, and pinned MariaDB image. The destination remains absent.
The original provisioning command is not a resume command: it requires both
targets to be fresh and provisions source before destination.

Continuation requires **two distinct, current, operation-specific manifests**
conforming to `cw-m5-026b-continuation-authorization-v1`:

1. `VerifyRetainedSourceIdentity` binds the exact source container ID, volume,
   database, loopback endpoint, immutable image reference, credential directory,
   and the single read-only identity query. It cannot provision the destination.
2. `ProvisionDestinationTarget` binds the exact absent destination target and
   pinned image. It cannot authorize source verification or any migration,
   backup, restore, certification, cleanup, deletion, or production operation.

The two approval IDs must be distinct from one another and from the original
two-target provisioning approval. Both approvals must identify the same owner,
operator, host, rootless Podman Unix account and UID, and expire no more than 24
hours after their respective approval time. The operator must establish actual
approval timestamps and provide genuine approvals; this worksheet creates no
manifest or authorization.

The preflight binds the approver, operator, and target owner to the recorded
original operator/owner identity, and binds hostname, Unix account, UID, and
rootless-Podman requirement to the existing read-only host evidence. A fresh
approval ID and actual approval/expiry timestamps are still required for each
operation.

The nonconnecting gate is:

```bash
scripts/preflight-m5-026b-continuation.sh \
  --source-authorization secure/evidence/cw-m5-026b-source-verification-authorization.json \
  --destination-authorization secure/evidence/cw-m5-026b-destination-provisioning-authorization.json \
  --report secure/evidence/cw-m5-026b-continuation-preflight.json
```

Only after both operation-specific approvals exist and this gate passes may the
operator separately choose to run the live continuation. The live runner checks
the current host/account and the preflight hashes, verifies the exact retained
source container/image/volume/loopback binding and credential ownership/modes,
executes only the authorized identity query once, and then rechecks destination
absence immediately before invoking the existing destination provisioner.
Any failure stops; it does not clean up or overwrite resources. This source-only
change does not authorize or perform live continuation.

The future live invocation is:

```bash
scripts/cw-m5-026b/continue-targets-m5-026b.sh \
  --source-authorization secure/evidence/cw-m5-026b-source-verification-authorization.json \
  --destination-authorization secure/evidence/cw-m5-026b-destination-provisioning-authorization.json \
  --preflight-report secure/evidence/cw-m5-026b-continuation-preflight.json \
  --report secure/evidence/cw-m5-026b-continuation-result.json \
  --continue-live --confirm-destination-provisioning
```

Do not run this command until both separate approvals are genuine, current,
exact-target manifests and the nonconnecting preflight passes. Output evidence
paths are create-only; choose new paths for each attempt.

Migrations, backup, restore, certification, cleanup, schema deletion,
container/volume deletion, production migration, and repository activation
remain prohibited (`NO_GO`).
