# ContinuumWiki Lua API capability matrix

This is the initial CW-M3-006B inventory. Lua runs only in the isolated
MoonSharp worker. Capabilities are allow-listed in the typed request and are
denied when absent; the worker never resolves missing context from the host.

| Namespace | API | Inputs / outputs | Capability | Budget / determinism | Status | Tests |
|---|---|---|---|---|---|---|
| `cw.text` | `trim`, `split`, `nowiki`, `encode`, `decode`, `listToText` | strings/tables to strings/tables | `cw.text` | bounded existing text operations; deterministic | Supported initial subset | `ExperimentalMediaWikiScribuntoRuntimeTests` plus native corpus pending |
| `cw.args` | `get`, `has` | string to string/boolean | `cw.args` | request snapshot only; deterministic | Supported initial subset | native corpus pending |
| `cw.title` | `current`, `normalize`, `namespace` | supplied strings to strings | `cw.title` | no lookup; deterministic | Supported initial subset | native corpus pending |
| `cw.page` | `exists`, `metadata` | title string to false/nil until a typed page snapshot is supplied | `cw.page` | no live lookup; unavailable snapshot is explicit and deterministic | Contract present, unavailable by default | `ExperimentalMediaWikiScribuntoRuntimeTests` |
| `cw.module` | `name`, `revision`, `dependencies` | immutable request metadata | `cw.module` | supplied bundle only; deterministic | Supported initial subset | native corpus pending |
| `mw.text` | `trim`, `split`, `nowiki`, `encode`, `decode`, `listToText`, `jsonEncode`, `jsonDecode` | bounded MediaWiki-compatible subset; JSON tables/scalars | `mw.text` | literal text behavior; JSON depth, size, key, cycle, and value limits | Supported partial compatibility | `ExperimentalMediaWikiScribuntoRuntimeTests` |
| `mw.ustring` | `len`, `lower`, `upper`, `sub`, `find` | Unicode strings to strings/numbers | `mw.ustring` | scalar-value operations; deterministic | Supported partial compatibility | `ExperimentalMediaWikiScribuntoRuntimeTests` |
| `mw.title` | `text`, `prefixedText`, `namespace`, `exists`, `new`, `getContent` | supplied title strings to immutable title tables | `mw.title` | no page or site lookup; `exists` is false and `getContent` is nil without a page snapshot | Supported bounded subset | `ExperimentalMediaWikiScribuntoRuntimeTests` |
| `mw.site` | `name`, `identifier`, `language`, `namespaces` | validated wiki identity to immutable site snapshot | `mw.site` | identity comes from the request; language and namespace configuration are unavailable and remain empty/nil | Supported minimal subset | `ExperimentalMediaWikiScribuntoRuntimeTests` |
| `mw.language` | `getCode`, `lc`, `uc`, `lcfirst`, `ucfirst` | invariant Unicode string operations | `mw.language` | no locale-sensitive formatting; `getCode` is nil until an explicit language context is supplied | Supported bounded subset | `ExperimentalMediaWikiScribuntoRuntimeTests` |
| `mw.html` | `element` | allow-listed tag/attribute tables to encoded markup | `mw.html` | bounded text/attribute sizes; no raw HTML or event/style attributes | Supported bounded subset | `ExperimentalMediaWikiScribuntoRuntimeTests` |
| `mw.loadData` / `mw.loadJsonData` | validated dependency loading | module name to immutable value | respective capability | declared bundle and dependency budgets | Supported bounded subset | runtime/corpus tests |
| `require` | validated Lua dependency loading | module name to value | `module.require` | declared bundle only | Supported bounded subset | runtime/corpus tests |

The native API intentionally does not expose filesystem, network, environment,
reflection, process, database, repository, or arbitrary CLR operations.

## Not yet implemented / intentionally unavailable

Page-backed `cw.page` metadata is not claimed available by this slice. `cw.page`
exposes its typed contract but returns deterministic
unavailable values until an authorized immutable page snapshot is added.
Missing capabilities and unknown capabilities fail closed at the worker
protocol boundary.
