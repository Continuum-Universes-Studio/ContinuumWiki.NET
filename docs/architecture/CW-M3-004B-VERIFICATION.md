# CW-M3-004B verification

Status: verified on 2026-10-10 against the synthetic template fixture and
disposable MariaDB 11.8 harness. No production MediaWiki endpoint or
production database was accessed.

## Implementation coverage

- Imported templates resolve from the completed batch's existing
  `MediaWikiImportPage`, `MediaWikiImportRevision`, `Page`, and `PageRevision`
  rows. Native SQLite remains a no-op adapter.
- `{{Name}}`, `{{Template:Name}}`, named and positional parameters, empty vs
  missing values, nested defaults, Unicode text, and balanced nested values
  are expanded transiently.
- `<noinclude>`, `<includeonly>`, and `<onlyinclude>` have separate direct-page
  and transclusion behavior.
- `#if`, `#ifeq`, `#switch`, `#expr`, and `#ifexpr` use lazy branches and a
  restricted decimal expression evaluator. Unknown functions and `#invoke`
  remain explicitly diagnosed.
- Recursive calls, missing/restricted/ambiguous templates, malformed braces
  and inclusion tags, excessive depth/operations/output/time, invalid
  arithmetic, unsafe URLs, extension content, and script injection are safe
  diagnostic paths.
- Render results expose template dependencies. Imported page cache keys include
  the historical timestamp-selected template revision hash; native cache keys
  remain separate.
- Historical article rendering selects the newest eligible template revision
  at or before the article source timestamp. This is documented as a policy,
  not full MediaWiki parity.
- Source Wikitext remains unchanged in persisted revisions and no contributor
  login accounts are created.

## Commands and results

```text
dotnet restore TightWiki.sln
passed

dotnet build TightWiki.sln --configuration Debug -m:1 --no-restore
0 warnings, 0 errors

dotnet test TightWiki.Tests/TightWiki.Tests.csproj --configuration Debug --no-restore -m:1 --filter FullyQualifiedName~MediaWikiWikitextRendererTests
12 passed, 0 failed, 0 skipped

dotnet test TightWiki.sln --configuration Debug --no-restore -m:1
166 passed, 0 failed, 21 intentional MariaDB integration skips, 187 total

dotnet publish TightWiki/TightWiki.csproj --configuration Release --output ./artifacts/publish --no-restore -m:1 -p:MSBuildEnableWorkloadResolver=false
passed; the workload-resolver property is an environment workaround and not an unmodified default publish claim

scripts/verify-m3-004b.sh
passed: MariaDB 11.8 import of 6 pages/8 revisions, native-page HTTP
regression, current and historical template expansion, parameters, inclusion
tags, parser functions, safe output, and cache invalidation after a persisted
template hash change
```

The harness is disposable and uses `MediaWikiInventory/Fixtures/template-expansion-fixture.xml`.
The preceding 004A media harness remains available separately as
`scripts/verify-m3-003b.sh`; native TightWiki regression coverage remains in
the default suite.

## Remaining compatibility gaps

Scribunto/Lua, tables, galleries, advanced media formatting, magic words,
language variants, arbitrary extensions, and complete MediaWiki parser parity
remain intentionally out of scope. No production data or production hosting
was used.
