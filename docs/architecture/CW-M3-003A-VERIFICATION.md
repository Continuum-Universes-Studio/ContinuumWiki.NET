# CW-M3-003A verification record

## Evidence

| Requirement | Evidence | Result |
| --- | --- | --- |
| Versioned provenance schema | `MySqlMediaWikiImportSchemaInitializer`; `Scripts/MySql/Import/V1__MediaWikiImportSchema.sql` | PASS |
| Validated plan feeds execution | `MediaWikiImportExecutor` runs `ImportPlanValidator`, source checksum/length checks, and re-reads source XML | PASS |
| Explicit MariaDB target preflight | Exact database name, disposable purpose, supported schema, empty target, and representability checks | PASS |
| Source identity and revision preservation | `MediaWikiImportPage`/`MediaWikiImportRevision` retain source page/revision IDs and target mappings | PASS |
| Wikitext preservation | Target `PageRevision.Body` plus provenance SHA-256; synthetic Unicode/template/category/file fixture | PASS |
| Attribution policy | Named and IP contributors stored as provenance; no historical login accounts created | PASS |
| Namespace/navigation mapping | Main, Template, File, Unicode, and collision-safe target navigation mappings | PASS |
| Redirect/dependency persistence | Redirect `PageReference`; dependency JSON/rows with unresolved status diagnostics | PASS |
| Transaction rollback/retry | Running batch, single write transaction, rollback status, fresh retry behavior | PASS; exercised by disposable run |
| Deterministic reconciliation | Counts, mappings, ordering, timestamps, contributor fields, body hashes, and redirect references | PASS; 4 pages, 5 revisions, zero differences |
| Controlled CLI | `MediaWikiImport provision`, `preflight`, `import`, `reconcile`, and `status`; target secrets are file/environment based | PASS |
| Disposable MariaDB integration test | `MediaWikiImportIntegrationTests` against MariaDB 11.8 | PASS, 1 passed |
| Disposable HTTP harness | `scripts/verify-m3-003a.sh`; live/ready, current, historical, and redirect-source retrieval | PASS |
| Focused offline tests | Prior inventory/planner/importer filter | PASS, 9 passed |
| Default regression suite | `dotnet test TightWiki.sln --configuration Debug --no-restore -m:1` | PASS, 151 passed, 20 skipped |
| Debug solution build | `dotnet build TightWiki.sln --configuration Debug -m:1` | PASS, 0 warnings/errors |
| Release publish | Serial publish with `--no-restore -m:1 -p:MSBuildEnableWorkloadResolver=false` to `artifacts/publish` | PASS |

The disposable integration target was local-only and synthetic. No production MediaWiki or production database was contacted. HTTP checks establish application retrieval and historical route selection, not MediaWiki rendering compatibility; the original Wikitext remains available losslessly in persistence.

## Remaining limitations

Media binaries are not imported. Template expansion, parser functions, Lua/Scribunto, extension hooks, and full MediaWiki rendering remain outside this milestone. The planner’s lexical dependency scan remains intentionally heuristic and unresolved/unsupported constructs stay visible in provenance diagnostics.

## Verdict

`CW-M3-003A VERIFIED`
