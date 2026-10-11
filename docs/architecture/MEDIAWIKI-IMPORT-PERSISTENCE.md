# MediaWiki import persistence (CW-M3-003A)

CW-M3-003A adds an explicitly invoked, disposable-target import pipeline. It consumes a validated CW-M3-002B JSON plan and the same source XML used to create that plan. It never runs during application startup and it does not accept a production target by default.

## Commands

The dedicated .NET 10 CLI is `MediaWikiImport`:

```text
MediaWikiImport provision --target-config MediaWikiImport/Fixtures/disposable-target.template.json
MediaWikiImport preflight --input <export.xml> --plan <plan.json> --target-config <target.json> [--mapping <mapping.json>]
MediaWikiImport import --input <export.xml> --plan <plan.json> --target-config <target.json> [--mapping <mapping.json>] --reconciliation-output <report.json>
MediaWikiImport reconcile --input <export.xml> --plan <plan.json> --batch <id> --target-config <target.json>
MediaWikiImport status --batch <id> --target-config <target.json>
```

Passwords are not command-line arguments. A target file can name an environment variable instead:

```json
{
  "ConnectionStringEnvironmentVariable": "CONTINUUMWIKI_M3_TARGET_CONNECTION",
  "DatabaseName": "cw_m3_003a",
  "Purpose": "disposable-mariadb",
  "RequireEmptyTarget": true
}
```

The importer requires the exact database name, `Purpose=disposable-mariadb`, and an empty `Page`/`PageRevision` target. It validates the plan checksum, input byte length, mapping identity, planner/schema versions, target schema, namespace/navigation representability, and required source timestamps before writing.

## Provenance schema

`MySqlMediaWikiImportSchemaInitializer` explicitly creates the page provenance tables plus version-2 `MediaWikiImportMedia`, `MediaWikiImportMediaRevision`, and `MediaWikiImportMediaReference`, all under the versioned `MediaWikiImportSchemaVersion` table. The original Wikitext remains in the target `PageRevision.Body`; its UTF-8 SHA-256 and source text state are retained in `MediaWikiImportRevision`. Media bytes remain only in the existing `PageFileRevision.Data` column; media provenance never duplicates binary blobs.

Source page/revision IDs, source namespace/title, export checksum, planner/mapping identities, timestamps, comments, content model/format, SHA-1, minor/deleted/suppressed state, contributor kind/ID/name/IP, dependency inventory, redirect targets, target IDs, and diagnostics are retained separately from target-local integer IDs.

Historical contributors never become application login accounts. Target page/revision rows use `Guid.Empty` as the explicit system import actor marker; source attribution is immutable provenance rather than an impersonated account.

## Transactions and retry

The importer creates a `Running` batch, writes pages, revisions, existing attachment rows, media provenance, dependencies, references, and redirect references in one MariaDB transaction, reconciles before commit, and marks the batch `Completed` only after a zero-difference report is available. Any failure rolls back target rows and marks the durable batch `RolledBack`; a fresh retry uses a new batch. Existing `Running` or `Completed` source/mapping combinations are rejected.

## Reconciliation and HTTP checks

Reconciliation compares source and target page/revision/media counts, source IDs, target mappings, ordering, current revision relationships, body/binary hashes, timestamps, contributor fields, navigation, media references, and redirect references. Reports are deterministic JSON with a stable hash.

The disposable harness is `scripts/verify-m3-003a.sh`. Its MariaDB 11.8 run verifies planning, target preflight, import, provenance queries, reconciliation, application readiness, current-page HTTP retrieval, historical revision retrieval, and redirect-source retrieval. The later `scripts/verify-m3-003b.sh` harness verifies the CW-M3-004A bounded imported renderer as well; neither harness claims full MediaWiki parity. The original source text remains lossless in persistence.

## Boundaries

No real Continuum Universes export, production MediaWiki endpoint, production database, media binary, template expansion, Lua execution, or authentication change is included. Heuristic dependencies remain diagnostics; unresolved templates/modules and unsupported constructs remain plan blockers. Advanced MediaWiki media parity and full Wikitext compatibility remain later work.
