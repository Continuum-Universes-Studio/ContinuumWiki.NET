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
| extension tags/hooks/custom Continuum templates | UNSUPPORTED | Keep original Wikitext and emit a blocking diagnostic until an adapter exists |

The fixtures at `MediaWikiInventory/Fixtures/` cover Unicode titles, multiple revisions, named/positional-style template syntax, categories, file links, references, parser functions, Lua invocation, redirects, content-model variations, language links, missing fields, malformed XML, external entities, and extension markup. The importer preserves source Wikitext and reports unsupported constructs; rendering additionally diagnoses and safely marks unsupported syntax.

No real source inventory is available in this milestone; all production counts and custom-extension coverage are UNVERIFIED.

The CW-M3-002B planner preserves the original Wikitext and reports dependency findings; it does not interpret or render templates, parser functions, Lua, references, or extension tags. CW-M3-003B resolves `File:`/`Image:` references against an offline manifest and records source-to-target media metadata without rewriting the body. Lexical dependency results are advisory/blocking planning signals, not proof of MediaWiki parser compatibility.

CW-M3-003A persists the original Wikitext losslessly in the target revision body and records dependency/unsupported-construct diagnostics in import provenance. CW-M3-004A dispatches imported current and historical revisions through the separate bounded renderer, while the native renderer remains the default. CW-M3-004B expands only persisted imported templates through the same batch and historical timestamp policy, then sends the expanded Wikitext through the 004A safe renderer. The disposable HTTP check proves actual expansion and cache-version behavior; it is not a claim of full MediaWiki rendering parity.

CW-M3-005A adds offline Scribunto inventory and a provider-neutral execution
boundary. `#invoke` remains rejected at render time; module arguments and
dependencies are typed and diagnosable, while literal loader dependencies,
missing modules, dynamic references, unsupported module content models, and
cycles are recorded by the planner. No Lua runtime is included.
