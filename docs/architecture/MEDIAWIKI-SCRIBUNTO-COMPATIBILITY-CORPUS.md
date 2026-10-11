# Scribunto compatibility corpus (CW-M3-005B.4)

## Verdict and scope

`CW-M3-005B.4 VERIFIED — EXPERIMENTAL`

This fixture corpus is an offline, versioned compatibility inventory for the
MoonSharp worker from CW-M3-005B.3. It exercises synthetic module bundles and
the existing JSON worker protocol. It does not read production MediaWiki
content, use a database, register Lua in the application renderer, or certify
the blocked operating-system sandbox. The default renderer remains
`RejectingMediaWikiScribuntoExecutor`, and the overall `CW-M3-005B BLOCKED`
OS-sandbox verdict remains unchanged.

## Reproducible command

Build the worker first, then run the standalone offline runner:

```bash
dotnet run --project ScribuntoCompatibility/ScribuntoCompatibility.csproj --configuration Debug -- \
  --corpus MediaWikiInventory/Fixtures/scribunto-compatibility-corpus-v1.json \
  --worker ScribuntoWorker/bin/Debug/net10.0/ContinuumWiki.ScribuntoWorker.dll \
  --report artifacts/validation/scribunto-compatibility-report-v1.json \
  --gap-inventory MediaWikiInventory/Fixtures/scribunto-api-gap-inventory-v1.json
```

The runner sorts case IDs, validates the corpus and the serialized worker
request, starts one disposable worker process per executable case, bounds each
case, compares text/diagnostics/dependencies, and emits deterministic JSON.
Validation-only cases are reported separately because they reject malformed or
unsafe requests before a worker process is started.

The current corpus contains 20 cases: 18 worker executions and 2
validation-only checks. All 20 pass; there are 7 supported, 5 partial, 1
unsupported, 2 invalid-input, and 5 security-rejected classifications, with
no skipped or failed cases. Two cases require later reference verification.
These counts are report output, not a production compatibility claim.

## Corpus schema and provenance

`MediaWikiInventory/Fixtures/scribunto-compatibility-corpus-v1.json` is pinned
to `cw-m3-005b.4-v1`. Each case includes:

- a stable case ID, module title, source identity, source wiki, revision, and
  Lua source;
- function name and bounded positional/named arguments;
- explicit capability names, dependency entries, root edges, frame metadata,
  and invocation-chain metadata;
- source, argument, result, instruction, dependency, and execution-time
  budgets;
- expected text or diagnostic code, expected dependency provenance, an
  explicit classification, gap IDs, and optional verification notes.

The dependency bundle is synthetic and hash-checked by the existing worker
protocol. The corpus includes current and historical revision identities,
nested and shared `require`, immutable `mw.loadData`, JSON loading, frame
metadata, Unicode/text helpers, malformed input, unsupported callbacks,
dynamic references, security rejection, and work-budget cases.

## Classifications and API gap inventory

The machine-readable report uses exactly these classifications:

| Classification | Meaning |
|---|---|
| `SUPPORTED` | The bounded fixture behavior is implemented and matched. |
| `PARTIALLY_SUPPORTED` | A tested subset works, but complete Scribunto/PHP/reference parity remains unverified. |
| `UNSUPPORTED` | The requested surface is deliberately absent or rejected as unavailable. |
| `INVALID_INPUT` | The fixture is malformed or requests an invalid module/export/data shape. |
| `SECURITY_REJECTED` | The request crosses a declared trust, dependency, or resource boundary. |

`MediaWikiInventory/Fixtures/scribunto-api-gap-inventory-v1.json` is generated
from the report and contains one record per tracked surface, current behavior,
known difference/security boundary, recommended priority, and fixture count.
The highest-priority open surfaces are controlled module loading, `mw.text`,
`mw.ustring`, immutable data semantics, and frame callbacks. Host callbacks,
dynamic loading, and the OS sandbox remain intentionally rejected or blocked.

The `lua-version-difference` case records a concrete language-version finding:
MoonSharp's parser rejects the Lua-version-sensitive `table.pack`/integer
division probe with `LuaSyntaxError`. This is evidence of a difference, not a
claim that every Scribunto module will fail; exact parity requires differential
cases against a pinned reference Scribunto/MediaWiki runtime.

## Verification boundaries and next work

The focused corpus tests cover deterministic serialization, unsupported corpus
versions, duplicate IDs, incorrect expected text, and invalid dependency
hashes. Existing worker tests remain the authority for protocol tampering,
resource bounds, dependency graph safety, and controlled-loader behavior.

Recommended next steps are to add pinned reference-runtime differential
fixtures for `mw.text`, `mw.ustring`, frame semantics, `mw.loadData`, JSON edge
cases, and Lua-version behavior, then review each resulting gap. None of those
steps may bypass the explicit bundle, capability, budget, or OS-sandbox
controls.
