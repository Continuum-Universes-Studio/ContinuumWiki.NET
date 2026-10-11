# CW-M4-002 disposable migration diagnostics

CW-M4-002 is currently source-only. The repository has revision metadata and
hashes for the investigated dependencies, but no importable authorized XML
source export is present under `artifacts/imports/mediawiki/` or the private
validation directory. No MariaDB import or worker activation is authorized by
that evidence.

## Entry points

The strict entry point remains:

```bash
scripts/verify-m3-006c.sh
```

The diagnostic entry point is separate and must be used only after the source
export and disposable target have been independently authorized:

```bash
CW_SCRIBUNTO_SOCKET_PATH=/run/continuumwiki/scribunto-worker.sock \
  scripts/verify-m3-006c-diagnostic.sh
```

It first checks socket filesystem access and the worker protocol, then invokes
the existing strict harness with `CW_M3_006C_DIAGNOSTIC=1`. The diagnostic
policy is limited to disposable MariaDB targets and the existing audited
rendering-only extension-tag allowlist. It does not relax strict migration
readiness.

## Required source gate

Before running either import path, verify a source-preserving export contains
the complete historical closure. In particular, the Jovan context requires
`Template:Divine@9469` and `Module:DivineFamily@9467`; `Module:DivineMeta@9470`
is later than the required context and must not be substituted. Revision
metadata without the corresponding raw Wikitext/Lua bodies is insufficient for
planning or import.

## Required target gate

The importer must receive an explicitly named disposable target whose purpose is
`disposable-mariadb` and whose database name begins with `cw_m3_`. No
production connection, current-state Cargo fallback, or source mutation is
permitted. The importer remains transactional and reconciliation-gated.

## Evidence and verdict

The current report is
`artifacts/validation/mediawiki-disposable-dependency-diagnostics-v1.json`.
It records `SOURCE_ONLY_DIAGNOSTICS_VERIFIED`, `IMPORT_BLOCKED`,
`DEPENDENCY_CLOSURE_PARTIAL`, and `HISTORICAL_PARITY_UNAVAILABLE` separately.
Existing private reference, output, and comparison artifacts are not rewritten.

