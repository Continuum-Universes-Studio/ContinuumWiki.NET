# CW-M3-005A verification

CW-M3-005A is an offline inventory and safe-boundary milestone. No Lua runtime,
production MediaWiki endpoint, production database, external process, or
network capability is used.

## Focused evidence

`MediaWikiScribuntoBoundaryTests` covers typed invocation argument ordering,
Unicode/module namespace normalization, static `require`/`mw.loadData`/
`mw.loadJsonData` references, dynamic-reference uncertainty, malformed
invocations, missing modules, module closure through templates, cyclic module
dependencies, unsupported content models, budget fields, and the rejecting
executor. Existing `MediaWikiWikitextRendererTests` also prove that `#invoke`
remains rejected through the normal renderer and returns a structured module
dependency without executing Lua.

The synthetic source is:

```text
MediaWikiInventory/Fixtures/scribunto-boundary-fixture.xml
```

It includes current and historical module revisions, a Unicode module, module
namespace pages, static and dynamic loaders, a template-generated invocation,
missing references, malformed syntax, an A/B cycle, and a non-Wikitext content
model.

The disposable MariaDB/HTTP harness from 004D was rerun after adding module
cache-version lookup and passed, proving the application still imports and
renders through the real persisted path. It remains a 004D harness because
005A requires no schema migration and intentionally executes no Lua.

## Required commands

```bash
dotnet restore TightWiki.sln
dotnet build TightWiki.sln --configuration Debug -m:1
dotnet test TightWiki.sln --configuration Debug --no-restore -m:1
MSBuildEnableWorkloadResolver=false dotnet publish TightWiki/TightWiki.csproj \
  --configuration Release --output ./artifacts/publish
```

The restricted Linux test host may require loopback-socket approval. That is an
execution-environment condition and is not treated as product test success.

## Deliberate non-claims

The milestone does not claim Lua execution, Scribunto behavioral parity,
complete dynamic dependency discovery, CPU/memory isolation from interfaces,
or authorization approval for a future runtime. Those require a separately
reviewed out-of-process sandbox and are outside CW-M3-005A.
