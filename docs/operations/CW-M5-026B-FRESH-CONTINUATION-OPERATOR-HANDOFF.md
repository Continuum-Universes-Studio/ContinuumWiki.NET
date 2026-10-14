# CW-M5-026B — Fresh continuation operator handoff

Status: proposal only; no new authoritative manifests or preflight were
created. The previously used `…-001` authorizations are not proposed for reuse.
Candidate `…-002` IDs require operator confirmation that they were never issued
or consumed outside the repository evidence. Approval timestamps remain
unresolved and must reflect the actual approval time.

## Previous failure and runner behavior

- The first result stopped at `AUTHORIZATIONS_VALIDATED` with
  `rootless Podman verification failed`; destination creation was not reached.
- The later host-side result reached `SOURCE_CONTAINER_AND_CREDENTIALS_VERIFIED`
  and then failed with `authorized source identity query did not verify`.
  That older report has no `SourceRuntimeIdentity` diagnostics, so the exact
  failed predicate cannot be recovered from it.
- The corrected runner now selects the pinned source database with `-D`,
  retains only sanitized runtime predicate diagnostics on failure, and stops
  before destination provisioning unless source verification returns accepted.
- Both preflight and live result paths are create-only. Preflight refuses to
  overwrite an existing report; the runner reserves its result path with
  exclusive creation (`x+`, mode `0600`) before host operations. Each later
  attempt therefore needs fresh, absent report paths.

## Proposed new manifest paths

After the operator approves both statements and fills actual approval times,
the recommended distinct authoritative paths are:

- `secure/evidence/cw-m5-026b-source-verification-authorization-20261012-002.json`
- `secure/evidence/cw-m5-026b-destination-provisioning-authorization-20261012-002.json`

Do not create these files until both approvals are genuinely recorded. Validate
both with the Draft 2020-12 schema and run the nonconnecting preflight first.

## Host-side invocation for review

Run only from the authorized operator terminal on `christian-laptop` as
`cdjensen94` (UID 1000), with functional rootless Podman. A prerequisite
`podman info --format '{{.Host.Security.Rootless}}'` must succeed and report
`true`; otherwise stop without running continuation. Use new create-only report
paths for every attempt:

```bash
cd ~/Projects/ContinuumWiki.NET
set -euo pipefail

test "$(hostname)" = christian-laptop
test "$(id -un)" = cdjensen94
test "$(id -u)" = 1000
test "$(podman info --format '{{.Host.Security.Rootless}}')" = true

scripts/preflight-m5-026b-continuation.sh \
  --source-authorization secure/evidence/cw-m5-026b-source-verification-authorization-20261012-002.json \
  --destination-authorization secure/evidence/cw-m5-026b-destination-provisioning-authorization-20261012-002.json \
  --report secure/evidence/cw-m5-026b-continuation-preflight-20261012-002.json

scripts/cw-m5-026b/continue-targets-m5-026b.sh \
  --source-authorization secure/evidence/cw-m5-026b-source-verification-authorization-20261012-002.json \
  --destination-authorization secure/evidence/cw-m5-026b-destination-provisioning-authorization-20261012-002.json \
  --preflight-report secure/evidence/cw-m5-026b-continuation-preflight-20261012-002.json \
  --report secure/evidence/cw-m5-026b-continuation-result-20261012-002.json \
  --continue-live --confirm-destination-provisioning
```

Before use, confirm all four output paths are absent. Stop on identity,
rootless-mode, schema, preflight, expiry, source predicate, or collision failure.
If a result indicates destination provisioning may have started, inspect and
reconcile resources before any further attempt; do not retry blindly.

Production migration and repository activation remain `NO_GO`.
