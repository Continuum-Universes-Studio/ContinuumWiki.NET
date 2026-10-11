# CW-M3-004A verification

Status: verified on 2026-10-09 against the synthetic fixtures and disposable
MariaDB 11.8 harness. No production MediaWiki endpoint or production data was
accessed.

## Coverage

- Typed render selection uses completed import provenance for the exact target
  page/revision. Native SQLite pages return the native descriptor.
- Current and historical imported pages use the dedicated bounded renderer;
  native TightWiki pages continue through `WikiEngine.Transform`.
- Headings, emphasis, links, paragraphs, ordered/unordered lists, horizontal
  rules, redirects, basic file/image references, categories, and references
  are covered by focused tests or the disposable HTTP fixture.
- Templates, parameters, parser functions, `#invoke`, unknown extension tags,
  malformed delimiters, unsafe URLs, HTML/script injection, media traversal,
  excessive input/output, and parse depth are covered by focused tests.
- Imported page source is not rewritten; source hashes remain persistence and
  import responsibilities.

## Commands and results

```text
dotnet build TightWiki.sln --configuration Debug -m:1 --no-restore
0 warnings, 0 errors

dotnet test TightWiki.Tests/TightWiki.Tests.csproj --configuration Debug --no-restore -m:1 --filter FullyQualifiedName~MediaWikiWikitextRendererTests
5 passed, 0 failed, 0 skipped

dotnet test TightWiki.sln --configuration Debug --no-restore -m:1
159 passed, 0 failed, 21 intentional MariaDB integration skips

dotnet publish TightWiki/TightWiki.csproj --configuration Release --output ./artifacts/publish --no-restore -m:1 -p:MSBuildEnableWorkloadResolver=false
passed

scripts/verify-m3-003b.sh
passed: disposable MariaDB 11.8 import, 3 files/4 media revisions,
binary restore, current and historical imported article rendering, attachment
download hashes, and anonymous permission denial
```

The first focused test attempt was blocked before test execution by the
sandbox's VSTest loopback restriction; the same command was rerun with the
approved loopback-enabled execution path. The first HTTP harness attempt
exposed and corrected a Dapper unsigned-column materialization issue and the
second exposed an assertion that was too specific to the media-only fixture;
the final disposable run passed.

## Remaining compatibility priorities

Template expansion, parser-function semantics, Scribunto/Lua, tables,
thumbnail/gallery parity, magic words, language links, custom extensions, and
render-diff comparison against an administrator-supplied offline export remain
out of scope for CW-M3-004A.
