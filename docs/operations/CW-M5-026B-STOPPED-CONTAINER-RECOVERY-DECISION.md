# CW-M5-026B — Stopped Source Container Diagnostic and Recovery Decision

**Status:** the retained source was started exactly once under a fresh
operation-specific authorization and passed post-start read-only identity
verification. A later fresh bounded inspection was authorized and executed
once, but stopped at `CONTAINER_NOT_RUNNING` before any database connection;
database readiness remains uncertified. The retained assessment is
`docs/operations/CW-M5-026B-DATABASE-READINESS-ASSESSMENT-20261013-003.md`.

## Evidence reviewed

The two retained inspection reports are unchanged and have two read-only
Podman calls each, zero database connections, and zero mutations:

| Report | SHA-256 | Recorded finding | What it establishes |
| --- | --- | --- | --- |
| `cw-m5-026b-inspection-result-CW-M5-026B-INSPECT-PROPOSAL-C.json` | `f434e2232aa7188c5ea561064e99aac78d965c2c31b05acf76d8ded14254e686` | `CONTAINER_IDENTITY_OR_ISOLATION_MISMATCH` | An earlier combined check failed; it does not identify which field differed. |
| `cw-m5-026b-inspection-result-CW-M5-026B-INSPECT-RETRY-20261013-002.json` | `206058a95958a5a93046b734a58ced1adc73e26d21fcab2ebd66e148babe3bd8` | `CONTAINER_NOT_RUNNING` | The expected container ID matched and the running flag was false. State string, exit code, image/labels, volume, and endpoint were not evaluated or recorded. |

Both reports have `Targets: {}`; neither includes raw Podman output. The second
report does **not** establish whether the container is exited, absent, or
recreated now. The current state must be read again under a fresh diagnostic
approval. Authorization C and the retry inspection authorization are not
reusable for this diagnostic or for a restart.

## Expected source binding from retained evidence

This is an expected identity, not a claim about current runtime state:

- Container: `continuumwiki-m5-026b-source-ro1`
- Container ID: `6a4d4f8707ee4f5b14dddfff183db6d26ce7453eaba377b7c9d081bc1157f914`
- Image: `docker.io/library/mariadb@sha256:6422478cb8e159f080fb1d8ccf65101e26fe51385787fde7d16c3b165a331f15`
- Volume: `continuumwiki-m5-026b-source-ro1-data`, mounted at `/var/lib/mysql`
- Provisioning RunId label: `io.continuumwiki.m5-026b-run-id=20261012T093746Z-86380`
- Container database label: `io.continuumwiki.m5-026b-database=cw_m5_026b_source_ro1`
- Volume role label: `io.continuumwiki.m5-026b-role=source`
- Published endpoint: `127.0.0.1:13326` for `3306/tcp`

The diagnostic also checks the immutable image by matching the container's
image ID to `podman image inspect` and requiring the pinned reference in
`RepoDigests`. A different `ImageName` string is recorded separately; it is
not by itself proof of a different image.

## Endpoint verification correction

The earlier diagnostic implementation verified the endpoint only by testing
the output of `podman port`. It did not inspect the container's persisted
`HostConfig.PortBindings`. Consequently, for an exited container, a missing
runtime port report could be classified as an endpoint identity mismatch
without establishing that the configured loopback binding was wrong. Podman
inspect exposes `HostConfig` (including its creation-time port-binding map)
separately from runtime `NetworkSettings`; the corrected bounded inspect
template now reads the configured binding for `3306/tcp` and compares it
exactly with `127.0.0.1:13326`.

The corrected result reports configured binding match/mismatch separately
from whether `podman port` reports a runtime mapping; that command is not a
host-socket connectivity probe. A stopped container is explicitly recorded
as `CONTAINER_STOPPED_ENDPOINT_INACTIVE` with finding
`ACTIVE_ENDPOINT_INACTIVE_WHILE_STOPPED`; an absent runtime mapping is never
accepted as proof of the configured binding. A missing or mismatched
configured binding remains an identity failure. For a running container, the
runtime mapping must also be present and match the authorized endpoint.

The retained prior diagnostic recorded only `EndpointMatched: false`; it did
not preserve raw `podman port` output or a configured-binding observation.
That historical result confirmed the source defect and explained how an
inactive stopped-container endpoint could cause the reported failure, but did
not establish the exact configured binding. Its diagnostic authorization could
not be reused. The fresh operation-specific run documented below supersedes
that uncertainty without changing the retained resources.

## Fresh authorized diagnostic result

The new exact-target authorization `CW-M5-026B-DIAGNOSTIC-20261013-002` was
approved for one read-only diagnostic on `christian-laptop` by the recorded
project owner/operator. Nonconnecting preflight passed with zero Podman,
database, mutation, and credential operations. The runner then executed once
and wrote create-only, owner-only evidence to:

`/home/cdjensen94/continuumwiki-validation/stopped-source-diagnostic-CW-M5-026B-DIAGNOSTIC-20261013-002/result.json`

The sanitized report was generated at `2026-10-13T02:41:13Z`, has SHA-256
`85edfacc500f30132f0adde3ecf0e704dfd54d01c29de76178502578eafb5b3a`, and
passed the report schema. It records:

- `SOURCE_CONTAINER_STOPPED_IDENTITY_MATCH`, exact expected container ID,
  state `exited`, and exit code `0`;
- pinned image identity, RunId label, database label, `/var/lib/mysql` volume
  mount, volume identity, and configured `127.0.0.1:13326` binding all matched;
- no active runtime port mapping because the container is stopped, recorded as
  `CONTAINER_STOPPED_ENDPOINT_INACTIVE` with finding
  `ACTIVE_ENDPOINT_INACTIVE_WHILE_STOPPED`;
- six fixed read-only Podman commands, zero database connections, zero database
  mutations, zero credential reads, and no persisted raw stdout or stderr.

This proves that the existing container may be considered for an unchanged
`podman start` on identity/configuration grounds. It does not prove database
health, recovery safety, schema readiness, or backup/restore readiness; exit
code `0` is not a recovery explanation.

## Separate diagnostic approval and host execution

The executable read-only runner now has an independent authorization contract:
`docs/operations/cw-m5-026b-stopped-source-diagnostic-authorization.schema.json`.
The existing inspection authorizations did not authorize this diagnostic. The
fresh manifest above established a new approval ID, genuine approval/expiry
UTC timestamps within 24 hours, approver/operator roles, target owner, exact
private result path, every expected source identity, and the exact
`christian-laptop` rootless operator identity in the schema.

The only true permissions are `InspectRootlessPodman`,
`InspectExactSourceContainer`, `InspectPinnedImage`, `InspectBoundVolume`,
`InspectPublishedEndpoint`, and `RetainSanitizedEvidence`. Start/restart,
create/recreate/stop, database and credential access, image/volume changes,
migrations, backup/restore, cleanup/deletion, and production access are all
explicitly false. This permission cannot satisfy the separate restart schema.

After genuine approval, prepare an owner-only working directory on the host
and create a private manifest containing the approved values (do not copy an
old inspection manifest):

```bash
umask 077
mkdir -m 700 -p "$HOME/continuumwiki-validation/stopped-source-diagnostic-<unique-run>"
```

Use fresh, nonexistent paths beneath that directory for `authorization.json`,
`preflight.json`, and `result.json`; ensure each JSON file is mode 0600 and the
directory is mode 0700. Validate the authorization nonconnectingly first:

```bash
python3 scripts/preflight-m5-026b-stopped-source-diagnostic.py \
  --manifest "$HOME/continuumwiki-validation/stopped-source-diagnostic-<unique-run>/authorization.json" \
  --report "$HOME/continuumwiki-validation/stopped-source-diagnostic-<unique-run>/preflight.json"
```

Proceed only if it reports
`CATEGORY_STOPPED_SOURCE_DIAGNOSTIC_AUTHORIZATION_VALID_NONCONNECTING` and all
resource-operation counts are zero. Then invoke the fixed runner once, with
the exact same authorized result path:

```bash
python3 scripts/cw-m5-026b/diagnose_stopped_source.py \
  --manifest "$HOME/continuumwiki-validation/stopped-source-diagnostic-<unique-run>/authorization.json" \
  --preflight-report "$HOME/continuumwiki-validation/stopped-source-diagnostic-<unique-run>/preflight.json" \
  --report "$HOME/continuumwiki-validation/stopped-source-diagnostic-<unique-run>/result.json" \
  --execute-readonly-diagnostic
```

The runner requires the same manifest hash, approval ID, output path, unexpired
approval, and a preflight report no more than 600 seconds old. It runs only
the fixed rootless `podman info`, `container exists`, `container inspect`,
`port`, `image inspect`, and `volume inspect` reads, stopping after `exists`
when the source is absent or storage is inaccessible. It never opens a DB or
reads credentials. Evidence contains state, exit code, identity booleans,
findings, counts, and a hash of the observed ID; stdout/stderr are not saved.
Both preflight and result creation are exclusive/no-overwrite and owner-only.
Before the first Podman call it also creates `<result.json>.started` with
`O_EXCL`; this consumes that operation authorization even if execution is
interrupted, so a retry requires a new operation-specific approval and fresh
preflight/result paths.
The report contract is
`docs/operations/cw-m5-026b-stopped-source-diagnostic-report.schema.json`.

Podman's documented `container exists` codes are handled distinctly: 0 means
present, 1 absent, and 125 storage inaccessible. Other nonzero codes and
malformed output are incomplete/inconclusive rather than "missing." The
runner/classifier are `scripts/cw-m5-026b/diagnose_stopped_source.py` and
`scripts/cw-m5-026b/stopped_source_diagnostic.py`.

## Classification and stop rules

| Diagnostic result | Meaning | Recovery decision |
| --- | --- | --- |
| `SOURCE_CONTAINER_MISSING` | Name lookup returned the documented absent code. | Stop. Do not recreate or start by name. Escalate for a new recovery plan. |
| `SOURCE_CONTAINER_RECREATED` | Name exists, but observed ID differs from the pinned ID. | Stop. Do not start. Preserve both container and volume. |
| `SOURCE_CONTAINER_IDENTITY_MISMATCH` | Container ID matches but pinned image digest, labels, volume/mount, configured endpoint, or (when running) runtime endpoint does not. | Stop. No restart authorization. |
| `SOURCE_CONTAINER_STOPPED_IDENTITY_MATCH` | Exact expected ID and runtime bindings match; status is exited/stopped and running is false. Exit code is recorded. | Eligible for a separate operator decision only; not permission to start. |
| `SOURCE_CONTAINER_RUNNING_IDENTITY_MATCH` | Exact identity matches and state is running. | Do not restart. Request a newly scoped read-only inspection authorization if readiness is still needed. |
| `DIAGNOSTIC_INCOMPLETE` / `SOURCE_CONTAINER_STATE_UNEXPECTED` | Rootless mode, commands, parsing, state, or identity could not be safely established. | Stop; no recovery operation. |

An exit code alone does not establish why the container stopped. In particular,
do not infer an OOM, corruption, or safe database recovery from a numeric code.
If explanation requires journal or application logs, obtain separate
read-only authorization and define a separate sanitized log scope.

## Authorized source start and post-start verification

The fresh authorization
`CW-M5-026B-RECOVERY-START-20261013-001` was bound to the diagnostic report
hash and every exact source identity above. Its manifest passed the restart
authorization schema and the nonconnecting preflight reported
`CATEGORY_STOPPED_SOURCE_RESTART_AUTHORIZATION_VALID_NONCONNECTING` with zero
Podman, database, mutation, and credential operations.

The authorization schema is
`docs/operations/cw-m5-026b-stopped-source-restart-authorization.schema.json`.
It rejects a manifest unless the diagnostic classification is exactly
`SOURCE_CONTAINER_STOPPED_IDENTITY_MATCH` and the diagnostic report hash and
all pinned target fields are present. The source-safe preflight and one-shot
runner are now available at
`scripts/preflight-m5-026b-stopped-source-restart.py` and
`scripts/cw-m5-026b/start_stopped_source.py`; their contracts are recorded in
`docs/operations/cw-m5-026b-stopped-source-restart-preflight.schema.json` and
`docs/operations/cw-m5-026b-stopped-source-restart-report.schema.json`.
The fixed read-only post-start verifier is
`scripts/cw-m5-026b/verify_started_source.py`, with report contract
`docs/operations/cw-m5-026b-started-source-verification-report.schema.json`.
The manifest and create-only evidence are retained under:

`/home/cdjensen94/continuumwiki-validation/stopped-source-start-CW-M5-026B-RECOVERY-START-20261013-001/`

The runner accepted only one invocation of
`/usr/bin/podman start continuumwiki-m5-026b-source-ro1`, bound to the
verified container ID, and returned
`SOURCE_CONTAINER_START_SUCCEEDED` with exit code `0`. Its sanitized report
has SHA-256
`e3d930bfa798e41b06bf60cbc9f139c26ed4fc055d97f3ca9373bdc986d8652d`.
The one-shot marker prevents replay; no retry was performed.

The subsequent fixed six-command read-only verification returned
`SOURCE_CONTAINER_RUNNING_IDENTITY_MATCH`: state `running`, exact container
ID, pinned image, labels, volume mount and identity, configured endpoint, and
active runtime mapping all matched, with no findings. Its sanitized report
has SHA-256
`c69cfb659f8bb7721d3c52646b7bdb031dd1196a1ea18d80d4aff85b6d4a6950`.
No raw Podman output or stderr was persisted.

Container creation/recreation, `podman restart`, image or volume changes,
database access, migrations, grants, fixtures, backup, restore, cleanup,
deletion, production access, and repository activation remain false. A failed
start would have consumed the authorization and could not be retried with the
same paths.

## Current disposition

The retained source container passed exact identity/configuration verification
immediately after the authorized start. The later inspection
`CW-M5-026B-INSPECT-20261013-003` encountered `CONTAINER_NOT_RUNNING` during
the source-first host gate, wrote no target records, and opened no database
connection. The fixed six-call sequence shows that the complete source host
check preceded the failed destination container check; thus destination was
not running at that inspection time. The result still cannot establish
database grants, schema, ledgers, fixtures, or destination emptiness, and it
does not establish destination state now. Backup/restore rehearsal remains
uncertified and `NO_GO`; production migration and repository activation remain
`NO_GO`. The subsequent destination diagnostic now records an exact identity
mismatch on the destination RunId label and volume identity, so no destination
start authorization was eligible and no start was executed. The retained
blocker report is
`docs/operations/CW-M5-026B-DESTINATION-RECOVERY-BLOCKER-20261013-005.md`.

The follow-up destination identity diagnostic
`CW-M5-026B-DIAGNOSTIC-20261013-006` found both RunId labels present and equal
to each other, but both different from the original provisioning RunId. The
source review shows that continuation provisioning generated a fresh
destination RunId for both labels, while destination expected-identity
derivation reused the original RunId. The exact comparison remains
fail-closed; no destination start is eligible under the historical binding.
The consolidated disposition is recorded in
`docs/operations/CW-M5-026B-DESTINATION-IDENTITY-DECISION-20261013-006.md`.
