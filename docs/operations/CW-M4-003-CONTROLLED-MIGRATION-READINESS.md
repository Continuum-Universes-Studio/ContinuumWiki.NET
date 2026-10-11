# CW-M4-003 controlled migration readiness

This document is an evidence-acquisition and go/no-go plan. It does not
authorize a production migration, production database writes, Lua activation,
Cargo activation, binary deployment, service restart, or worker diagnostics
from an unauthorized session.

The machine-readable assessment is:

`artifacts/validation/mediawiki-migration-readiness-v1.json`

Current decisions:

- Source-only bounded diagnostics: `GO` within the documented non-worker,
  synthetic/current-state scope.
- Controlled disposable migration: `CONDITIONAL`; not authorized now.
- Production migration: `NO_GO`.

## Evidence prerequisites

Before a controlled migration test, obtain and independently verify a complete
source-preserving export. The existing metadata identifies the Jovan context,
but raw bodies are unavailable for `Template:Divine@9469` and
`Module:DivineFamily@9467`. `Module:DivineMeta@9470` is later than the required
historical context and must not be substituted.

Each selected page/revision must include its source wiki identity, page ID,
namespace, canonical title, revision ID/timestamp, content model/format, raw
Wikitext or Lua, content SHA-256, and export provenance. Use the existing
authorized MediaWiki export workflow; do not acquire source data from an
unapproved system.

Historical Cargo is a separate gate. The authenticated
`cargo-authentic-current-20261011-1` snapshot is current-state evidence only.
It must not be used for historical parity. Helper relationships remain
inactive until their schema, rows, ordering semantics, and typed result
contract are verified.

## Authorization boundaries

The following require separate approval:

1. Read-only historical MediaWiki export access and private staging.
2. Writes to a newly authorized disposable `cw_m3_*` MariaDB target.
3. Dedicated-identity worker activation and host certification.
4. Any current/helper or historical Cargo row acquisition.
5. Any future production change or activation.

No authorization is inferred from a report, connection string, socket listener,
or passing unit test.

## Ordered controlled procedure

1. Stage the authorized XML at the existing harness input path:
   `artifacts/imports/mediawiki/continuumwiki-full-with-files.xml`.
2. Verify source/export hashes and inspect the unchanged strict plan. Do not
   substitute later revisions or fabricate missing modules.
3. Independently authorize and verify the disposable target described by
   `MediaWikiImport/Fixtures/disposable-target.template.json`.
4. Run the existing diagnostic entry point only after source, target, and host
   authorization gates pass:

   ```bash
   CW_SCRIBUNTO_SOCKET_PATH=/run/continuumwiki/scribunto-worker.sock \
     scripts/verify-m3-006c-diagnostic.sh
   ```

5. Preserve the import report, batch identity, plan, reconciliation, and
   read-only post-import checks. A completed transaction alone is insufficient.
6. Compare only newly generated outputs with the unchanged private MediaWiki
   references. Classify exact, partial, unsupported, failure, and unevaluated
   results separately.

## Worker certification

Worker certification is independent of migration planning. Follow
`docs/operations/SCRIBUNTO-HOST-CERTIFICATION.md` from an authorized terminal
as `continuumwiki-scribunto`, using the observed service cgroup path. All
required identity, socket, cgroup, bubblewrap, filesystem, network, capability,
SELinux, resource, cancellation, and recovery checks must be `PASS`.

The historical CoreCLR/GC exit-137 message remains a startup observation, not
proof of cgroup OOM. Do not change `MemoryMax` or other hardening based on
static evidence.

## Failure recovery and rollback

The existing harness is the only approved disposable orchestration. Its target
must be explicitly disposable and its transaction must reconcile before being
classified verified. Do not reset or delete an existing diagnostic target or
reports without approval. On failure, retain the plan, import report,
reconciliation, logs, and diagnostics for review; do not overwrite references
or use a production connection as a fallback.

## Go/no-go rules

- `GO` for source-only work means only bounded, non-worker diagnostics with
  explicit synthetic/current-state labels.
- `CONDITIONAL` for controlled migration means all listed source, target,
  importer, worker, and data conditions must pass first; it is not execution
  authorization.
- `NO_GO` for production remains mandatory while any critical source,
  historical-data, worker, import-safety, or comparison gate is blocked.

