# CW-M3-002A verification record

## Evidence

| Requirement | Evidence | Result |
| --- | --- | --- |
| Typed intermediate model | `MediaWikiImportModel.cs` records headers, namespaces, pages, revisions, contributors, warnings, and unsupported XML | PASS |
| Streaming secure XML reader | `MediaWikiXmlReader.cs` uses `XmlReader`, DTD prohibition, null resolver, and one-page materialization | PASS |
| Source identity and attribution | `MediaWikiRevision` and `MediaWikiPage` preserve source IDs; contributor kinds preserve named/IP/missing attribution | PASS |
| Exact Wikitext preservation | `MediaWikiXmlImportTests.StreamingReaderPreservesIdsAttributionAndExactWikitext` | PASS |
| Redirects/namespaces | Import fixture and focused tests | PASS |
| Malformed and unsafe XML | Focused tests reject malformed XML and external entities | PASS |
| Offline preview | `MediaWikiImportPreview` and `preview <export.xml>` CLI mode | PASS |
| Debug build | `dotnet build TightWiki.sln --no-restore -m:1 --configuration Debug` | PASS, 0 warnings/errors |
| Focused importer tests | Combined offline inventory/importer filter | PASS, 4 passed |
| Default regression suite | `dotnet test TightWiki.sln --configuration Debug --no-build -m:1` | PASS, 146 passed, 19 MariaDB tests skipped |
| Offline preview command | `dotnet run --project MediaWikiInventory -- preview MediaWikiInventory/Fixtures/import-fixture.xml` | PASS, machine-readable JSON emitted |
| Release publish | Serial Release publish with workload resolver disabled | PASS, artifact present |

## Unsupported or unavailable

The reader preserves unsupported XML elements and reports warnings, but it does not execute templates, parser functions, Lua, extension hooks, or media downloads. Real production export counts and MariaDB integration remain outside this offline milestone and are not required for its verification.

## Verdict

`CW-M3-002A VERIFIED`

No MediaWiki installation, production database, or target MariaDB database was accessed by this milestone. MariaDB tests are not a requirement for this offline-only milestone and remain skipped because no connection is configured.
