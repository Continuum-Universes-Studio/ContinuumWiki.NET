# CW-M4-005 — Operator Evidence Acquisition and Authorization Handoff

Status: **Operator handoff verified; authorizations pending.**

This document is a coordination package. It records the evidence required to
advance the MediaWiki migration investigation and gives an authorized operator
the existing, bounded commands to run after the corresponding approval is
recorded. It does not grant approval and it does not execute any command.

## Current boundary

CW-M4-003 remains the controlling readiness decision:

- source-only investigation: `GO`;
- controlled disposable migration: `CONDITIONAL`, not authorized;
- production migration or activation: `NO_GO`.

The strict import planner, rejecting Scribunto executor, production-disabled
Lua/Cargo configuration, worker sandbox, resource budgets, and source and
reference provenance rules remain unchanged. The current Cargo snapshot is
current-state evidence only; it is not historical parity evidence.

No credentials, private API responses, private Cargo rows, or approval claims
are included in this handoff.

## Evidence tracks

### Track A — Historical source recovery

Purpose: obtain an operator-approved, source-preserving export or equivalent
historical source package containing the requested page and revision bodies.

The existing evidence report is
`artifacts/validation/mediawiki-historical-source-acquisition-v1.json`.
It records metadata for `Jovan@9482`, `Module:DivineTree@9481`,
`Template:Divine@9469`, and `Module:DivineFamily@9467`; it does not contain
authenticated raw bodies. `Module:DivineMeta@9470` is later than the eligible
Template:Divine context and must not be substituted.

Required approval: read-only source access, historical revision scope, private
artifact destination, retention, and an operator-provided integrity manifest.
Do not place credentials in the repository or in generated reports.

After approval, set `APPROVED_MEDIAWIKI_EXPORT_PATH` to the already-authorized
private export path and run the existing inventory/planning commands:

```bash
approved_export="${APPROVED_MEDIAWIKI_EXPORT_PATH:?set to an operator-approved private export}"
dotnet run --project MediaWikiInventory/MediaWikiInventory.csproj -- modules \
  --input "$approved_export" \
  --output artifacts/validation/mediawiki-module-inventory-v1.json
dotnet run --project MediaWikiInventory/MediaWikiInventory.csproj -- invocations \
  --input "$approved_export" \
  --output artifacts/validation/mediawiki-full-invocation-inventory-v1.json
dotnet run --project MediaWikiInventory/MediaWikiInventory.csproj -- plan \
  --input "$approved_export" \
  --output /tmp/cw-m4-005-source-plan.json
```

These commands are not permission to acquire the export. They are the
post-approval processing step and must retain exact revision identities.

### Track B — Disposable migration authorization

Purpose: run the existing diagnostic import and comparison workflow only in an
explicitly disposable target after source completeness and target authorization
are recorded.

Required approvals: disposable MariaDB target name/owner, data-retention and
cleanup policy, source package hash, worker socket authorization, and a review
that the six-fixture diagnostic allowlist is unchanged. Production MariaDB,
production Lua, and the strict migration path remain out of scope.

The existing diagnostic entry point is:

```bash
CW_SCRIBUNTO_SOCKET_PATH=/run/continuumwiki/scribunto-worker.sock \
  scripts/verify-m3-006c-diagnostic.sh
```

Do not replace it with `scripts/verify-m3-006c.sh`: the latter intentionally
retains strict import rejection. A successful diagnostic run is execution
evidence only; it does not establish compatibility parity or production
readiness.

### Track C — Scribunto host certification

Purpose: certify the deployed MoonSharp worker under its dedicated identity,
with the installed Unix-domain socket, systemd unit, bubblewrap boundary, and
resource limits intact.

Follow `docs/operations/SCRIBUNTO-HOST-CERTIFICATION.md`. The operator must
record the deployed worker and launcher hashes, unit/drop-in and launcher
provenance, socket ownership/mode/group access, effective identity, sandbox
mounts and namespace behavior, CoreCLR startup, memory/CPU/file/task limits,
and the real socket-backed protocol result.

The existing host procedure is run by an authorized operator, using the
dedicated identity and the already-installed verification script:

```bash
repo_root="$PWD"
worker_cgroup_path="${CW_SCRIBUNTO_WORKER_CGROUP_PATH:?set to the verified worker cgroup path}"
sudo -u continuumwiki-scribunto env \
  CW_SCRIBUNTO_SOCKET_PATH=/run/continuumwiki/scribunto-worker.sock \
  CW_SCRIBUNTO_WORKER_CGROUP_PATH="$worker_cgroup_path" \
  "$repo_root/scripts/verify-moonsharp-host-sandbox.sh"
```

The placeholder must be replaced with the cgroup path verified from the actual
service instance. Do not change socket permissions, systemd properties,
bubblewrap arguments, or the installed worker as part of certification.

### Track D — Historical Cargo provenance

Purpose: obtain separately authorized, revision-correlated Cargo data or a
trusted historical snapshot. The existing current-state snapshot
`cargo-authentic-current-20261011-1` remains valid only for current-state
schema/row evidence. Historical rows and time-bounded snapshots remain
unavailable.

No acquisition command is supplied until an independent read-only Cargo
authorization identifies the permitted tables, fields, time/revision scope,
private destination, and integrity procedure. Positive Cargo queries and
historical fallback remain disabled.

## Acceptance and dependency order

The tracks are independent gates, but their safe execution order is:

```text
historical source bodies + hashes
          |
          v
strict source/dependency review ----> disposable-target authorization
          |                                      |
          v                                      v
worker host certification ----------------> diagnostic import/comparison
          |
          v
historical Cargo evidence (separate; not implied by Lua success)
```

No track may silently satisfy another track. A source-complete package does not
certify the host; a host result does not authorize import; a current Cargo
schema does not establish historical Cargo parity; and matching error strings
are not compatibility parity.

## Approval workflow

Use `docs/operations/templates/CW-M4-005-OPERATOR-APPROVAL-TEMPLATE.json` for
each track. The template deliberately starts with `NOT_REQUESTED` and
`UNVERIFIED`. An operator must fill only the approved scope, evidence paths,
hashes, retention, and identity details that were actually authorized.

The machine-readable reconciliation is
`artifacts/validation/mediawiki-operator-evidence-handoff-v1.json`.
It records all fourteen CW-M4-003 gates, the evidence currently available,
the missing authorization, and the independent acceptance criteria.

## Stop conditions

Stop and preserve diagnostics if source revisions are missing, a dependency
closure is incomplete, a target is not explicitly disposable, the worker is
not certified under its dedicated identity, a reference/output hash changes,
or a comparison fails. Do not fabricate source, Cargo rows, reference HTML,
worker provenance, or approval.

