# CW-M3-006F.3 Cargo data discovery and acquisition

This workflow is operator-run and read-only. It does not modify MediaWiki,
Cargo tables, ContinuumWiki production data, or the Lua worker. It requires a
private MariaDB client option file and never accepts a password on the command
line.

## Metadata-only inventory

Create a MariaDB client option file readable only by the operator (`0600`),
using the existing authorized read-only account. Do not commit or paste it.
Then run:

```bash
cd /home/cdjensen94/Projects/ContinuumWiki.NET
export MYSQL_CARGO_DEFAULTS_FILE=/private/path/to/read-only.cnf
export MYSQL_CARGO_DATABASE=mediawiki_database_name
export CARGO_INVENTORY_REPORT_PATH="$HOME/continuumwiki-validation/mediawiki-cargo-data-inventory-v1"
scripts/inventory-mediawiki-cargo.sh
```

The inventory uses `information_schema` only for table metadata and writes a
private TSV plus SHA-256 sidecar. Treat engine row counts as estimates. It
must not run unrestricted counts, maintenance, `#cargo_store`, or
`#cargo_declare`.

## Data acquisition gate

Do not export rows until the operator separately authorizes a consistent,
read-only snapshot and records its identity, capture time, schema hash, and
relationship to the eight reference fixtures. Current Cargo rows are not
historical evidence. The rejecting Cargo provider remains active until this
evidence and a typed provider review are complete.

No row-acquisition command is supplied because the source database and
historical snapshot are not available to Codex. This avoids credential
exposure and invented historical data.

The JSON reports record the current pending state and required evidence.
