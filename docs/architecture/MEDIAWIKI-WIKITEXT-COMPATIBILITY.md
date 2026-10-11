# MediaWiki Wikitext compatibility (CW-M3-004A)

Native pages continue to use TightWiki markup (`WikiEngine.Transform` and
plugin processing). Imported revisions with completed MediaWiki provenance use
the bounded `MediaWikiWikitextRenderer`; existing HTTP/parser tests therefore
remain native regression tests, while `MediaWikiWikitextRendererTests` cover
the imported subset.

| Construct | Status | Required handling |
| --- | --- | --- |
| headings, basic emphasis, lists | SUPPORTED_SUBSET | Bounded typed parser and renderer |
| internal/external links | SUPPORTED_SUBSET | Imported navigation mapping; http/https allow-list |
| Wikitext tables and infoboxes | SUPPORTED_SUBSET | Typed bounded table AST; safe attributes and spans; preserve source |
| `{{Template}}`, named/positional parameters | SUPPORTED_SUBSET | Imported namespace resolver with bounded transient expansion |
| parser functions (`#if`, `#ifeq`, `#switch`, `#expr`, `#ifexpr`) | SUPPORTED_SUBSET | Lazy allow-list evaluation with restricted arithmetic |
| `#invoke` / Lua Scribunto | INVENTORIED_REJECTED | Typed invocation/dependency boundary; default executor rejects and never executes Lua |
| `<ref>` / `<references />` | SUPPORTED_SUBSET | Revision-local safe reference collection and rendering |
| `[[Category:...]]` | SUPPORTED_SUBSET | Typed category result and safe link projection |
| `[[File:...]]` / `[[Image:...]]` | SUPPORTED_SUBSET | Imported attachment mapping with bounded display options; original-byte thumbnail fallback |
| `<gallery>` | SUPPORTED_SUBSET | Bounded responsive gallery with diagnostics for unknown modes/attributes |
| redirects | SUPPORTED_SUBSET | Safe target display; no automatic redirect following |
| namespaces, Unicode titles, language variants | REQUIRES_ADAPTER | Explicit namespace and normalization table |
| page, namespace, revision, site, language, and clock magic words | SUPPORTED_SUBSET | Typed per-render context; encoded title forms where safe; unsupported behavior switches are diagnosed |
| interlanguage links/localized variants | REQUIRES_ADAPTER | Preserve language code/title pairs and variant markup |
| `<mermaid>` extension tag | SAFE_STATIC | Bounded typed source validation and encoded accessible static representation; actual diagram rendering remains unavailable |
| extension tags/hooks/custom Continuum templates | UNSUPPORTED | Keep original Wikitext and emit a typed diagnostic until an adapter exists |

The fixtures at `MediaWikiInventory/Fixtures/` cover Unicode titles, multiple revisions, named/positional-style template syntax, categories, file links, references, parser functions, Lua invocation, redirects, content-model variations, language links, missing fields, malformed XML, external entities, and extension markup. The importer preserves source Wikitext and reports unsupported constructs; rendering additionally diagnoses and safely marks unsupported syntax.

No real source inventory is available in this milestone; all production counts and custom-extension coverage are UNVERIFIED.

The CW-M3-002B planner preserves the original Wikitext and reports dependency findings; it does not interpret or render templates, parser functions, Lua, references, or extension tags. CW-M3-003B resolves `File:`/`Image:` references against an offline manifest and records source-to-target media metadata without rewriting the body. Lexical dependency results are advisory/blocking planning signals, not proof of MediaWiki parser compatibility.

CW-M3-003A persists the original Wikitext losslessly in the target revision body and records dependency/unsupported-construct diagnostics in import provenance. CW-M3-004A dispatches imported current and historical revisions through the separate bounded renderer, while the native renderer remains the default. CW-M3-004B expands only persisted imported templates through the same batch and historical timestamp policy, then sends the expanded Wikitext through the 004A safe renderer. The disposable HTTP check proves actual expansion and cache-version behavior; it is not a claim of full MediaWiki rendering parity.

CW-M3-005A adds offline Scribunto inventory and a provider-neutral execution
boundary. `#invoke` remains rejected at render time; module arguments and
dependencies are typed and diagnosable, while literal loader dependencies,
missing modules, dynamic references, unsupported module content models, and
cycles are recorded by the planner. No in-process Lua runtime is included;
authorized execution, when explicitly configured, remains outside the renderer
through the isolated worker boundary.

## CW-M3-007B bounded expansion characterization

The imported Wikitext expander retains the existing finite limits: a 500 ms
wall-clock budget, 10,000 expansion operations, depth 16, 1,500,000 expanded
output characters, 1,000,000 input characters, and at most 100 diagnostics.
The Scribunto boundary receives the same bounded invocation, source, argument,
result, dependency, instruction, memory, and time limits; no limit was raised
for this characterization.

Synthetic characterization covers plain text, nested templates, positional and
named parameters, lazy parser functions, repeated nonrecursive expansion,
empty Lua output, and Lua-returned Wikitext. These finite cases complete within
the unchanged budgets. Direct, mutual, longer, parser-function, and nested
Scribunto cycles are stopped by their existing invocation-chain checks rather
than being allowed to consume the wall-clock budget. Operation exhaustion is
reported as `ExpansionBudgetExceeded`; wall-clock exhaustion remains reported as
`ExpansionTimeLimitExceeded`.

Typed Scribunto results with error-severity diagnostics are terminal for the
current expansion context. The expander preserves the diagnostic and source
following the failed invocation, but stops resolving later dependencies. This
keeps errors such as `CargoProviderUnavailable`, `ScribuntoModuleMissing`, and
worker/protocol failures from being obscured by a secondary timeout. The
default rejecting executor emits a warning and remains recoverable, so an
unsupported `#invoke` does not abort unrelated valid text. Empty text from an
executed Lua result remains a valid successful result.

The four authentic positive ContinuumWiki artifacts that contain
`ExpansionTimeLimitExceeded` predate this diagnostic correction and were not
rewritten. Their earliest independently established terminal evidence is an
unavailable historical Cargo result; the DivineMeta fixture also has an
independent missing-source condition. The separate Jovan artifact has a
recoverable missing-template diagnostic and an unavailable Cargo result, but no
timeout. Whether any timeout reproduces after the correction is not established
until the disposable, revision-aware harness is rerun. These findings do not
establish MediaWiki parity or historical Cargo parity.

The characterization evidence is recorded in
`artifacts/validation/mediawiki-expansion-characterization-v1.json`.

## CW-M3-007E bounded extension compatibility

The observed Jovan `<mermaid>` construct is handled by a narrow typed boundary
in `MediaWikiMermaidCompatibility`. The adapter accepts only a bounded,
nonempty source beginning with an observed Mermaid diagram type, accepts no tag
attributes, rejects nested tags and executable or embedded markup, and emits
HTML-encoded source in an accessible `<figure>`/`<pre>` static representation.
URLs in the source remain inert text; no browser, JavaScript, network, SVG, or
external process is used. The result always carries
`ExternalRendererUnavailable`, so static source preservation is not reported as
diagram compatibility. Shared renderer output limits apply to the fallback and
can emit `ExtensionOutputLimitExceeded`.

The independent Jovan reference contains a 121-line, 9,791-character
`flowchart`-related `<pre>` block with generated `click` URL markup. That
captured HTML does not independently authorize a Mermaid renderer or establish
raw source parity. `#cargo_store` remains unsupported and write-free;
TemplateStyles reference errors remain separate source/dependency evidence.
