# CW-M5-026B continuation — operator approval checklist

Status: review proposals only. Neither proposal is an authorization, neither is
at an authoritative manifest path, and neither can pass the live continuation
preflight. No live operation was performed for this handoff.

## Separate decisions required

Approve or reject each action independently. Approval of one does not imply the
other:

1. `VerifyRetainedSourceIdentity`: exact read-only host/container/image/volume/
   credential-metadata/loopback checks followed by one query only:
   `SELECT DATABASE(), VERSION(), @@version_comment;` against the retained
   source target. No credential values are to be read or logged.
2. `ProvisionDestinationTarget`: create only the named fresh destination target
   after successful source verification and an immediate collision check. This
   action does not authorize database migrations, backup, restore, or
   certification.

## Operator must provide or confirm for each approval

- A genuine, fresh approval decision for that operation, with actual
  `ApprovedAtUtc` and `ExpiresAtUtc`; the validity interval must be positive and
  no longer than 24 hours. Do not backdate or reuse the historical provisioning
  approval.
- A unique `ApprovalId` for each operation. The two IDs must differ from each
  other and from `CW-M5-026B-PROVISION-20261012-001`.
- Confirm approver, operator, and target owner names and roles. The proposals
  reflect historical records (Christian Daniel Jensen; Project Owner,
  System Administrator, and target owner), not fresh authorization.
- Confirm the host values from current trustworthy evidence: `christian-laptop`,
  user `cdjensen94`, UID `1000`, and rootless Podman. The recorded host report
  is historical and notes partial Podman inventory; it does not prove current
  runtime state.
- Approve the exact operation-specific target, immutable image reference,
  identity prefix, credential directory, and operation boundary shown in the
  corresponding proposal. Do not modify the retained source or create
  credentials during approval.
- Confirm the fixed exclusions: production/shared resources, rehearsal,
  backup, restore, migrations, certification, cleanup, schema deletion, and
  container/volume deletion all remain false.

## Required gates after approval is recorded

1. Create two authoritative, operation-specific manifests at the exact paths
   expected by the repository preflight. Replace null approval fields only with
   the genuine operator-provided values. Do not copy these proposal wrappers to
   authoritative paths.
2. Validate each manifest against
   `docs/operations/cw-m5-026b-continuation-authorization.schema.json` using a
   Draft 2020-12-capable JSON Schema validator, then run the repository's
   nonconnecting gate:

   ```bash
   scripts/preflight-m5-026b-continuation.sh \
     --source-authorization secure/evidence/cw-m5-026b-source-verification-authorization.json \
     --destination-authorization secure/evidence/cw-m5-026b-destination-provisioning-authorization.json \
     --report secure/evidence/cw-m5-026b-continuation-preflight.json
   ```

3. Stop on any mismatch or preflight failure. Only after a valid preflight and
   a separate operator go-ahead may the continuation runner be considered. Its
   live flags are not exercised by this source-only handoff.

## Fixed boundary

`RehearsalRightsGranted=false`; migrations, backup, restore, certification,
cleanup, schema deletion, container/volume deletion, production migration, and
repository activation remain prohibited. Current live state remains the
historically evidenced `PROVISIONING_FAILED_RESOURCES_RETAINED`; it has not
been rechecked here.
