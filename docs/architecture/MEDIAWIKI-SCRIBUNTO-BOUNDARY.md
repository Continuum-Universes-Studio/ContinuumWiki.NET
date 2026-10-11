# MediaWiki Scribunto boundary (CW-M3-005A)

CW-M3-005A inventories Scribunto references and defines a future provider
boundary. It does not embed Lua, execute Lua, load imported modules as .NET
assemblies, or claim Scribunto compatibility.

## Invocation contract

`MediaWikiScribuntoInvocation` is the typed representation of one
`{{#invoke:Module|function|...}}` call. It preserves the original invocation,
canonical module title, exported function, positional argument order, named
arguments, calling page/source revision, template invocation chain, culture,
time zone, and source location. `MediaWikiModuleTitle` accepts the Module
namespace and rejects empty, NUL-containing, or unrecognized namespace text.

`IMediaWikiScribuntoExecutor` is the provider-neutral execution contract.
`IMediaWikiScribuntoExecutionHost` exposes only typed operations for resolving
permitted module/template/page text and registering dependencies. It exposes no
database connection, filesystem path, network client, process API, reflection
surface, dependency-injection container, or mutable application state.

`RejectingMediaWikiScribuntoExecutor` is the registered default. It returns the
original invocation, emits `ScribuntoExecutionRejected`, records the module
dependency, and sets `Executed=false`. The existing Wikitext renderer still
HTML-encodes the result and preserves the safe unsupported marker path.

## Offline inventory and planner

`MediaWikiScribuntoScanner` is a bounded lexical scanner. It records:

- `#invoke` module/function calls, positional arguments, and named arguments;
- `require("...")`, `mw.loadData("...")`, and
  `mw.loadJsonData("...")` literal module references;
- dynamic loader calls as `UncertainModuleDependencies` and warning findings;
- malformed or unbalanced invocations as warning findings.

The existing planner recognizes namespace 828 (`Module`) and 829 (`Module
talk`), records module closure through template/module references, reports
missing modules, and reports module dependency cycles. Module-page content
models are retained and unsupported models remain blocking plan findings.
Static analysis intentionally does not resolve dynamically computed module
names, generated code, metatables, `package` mutation, or runtime calls.

## Historical and cache policy

The existing import timestamp policy applies to future module resolution:
select the newest non-deleted imported Module revision whose source timestamp
is at or before the calling article revision timestamp, within the same source
wiki identity and import batch. A module with no eligible revision is missing
or restricted; ambiguous source identities do not execute.

`ITwMediaWikiImportRepository` has typed module resolution and module cache
version operations. Imported page cache identity includes the module version
alongside template version, source revision, page context, locale, time zone,
site configuration, and clock context. Stored `PageRevision.Body` is never
rewritten.

## Resource budget

`MediaWikiScribuntoExecutionBudget` defines measurable future limits for
invocations, module dependency depth, module source characters, argument
characters, result characters, instruction count, wall-clock time, and memory.
The current rejecting provider can enforce rejection and renderer budgets, but
an interface alone cannot enforce CPU instructions, process isolation, memory,
or wall-clock limits for an untrusted interpreter. Those require a separate
sandbox with an independently enforced host/runtime boundary.

## Required future sandbox

A later implementation would need a dedicated, least-privilege Lua runtime
with deterministic versioned libraries, no network or process access, no host
filesystem, no raw database access, bounded memory/CPU/time, cancellation,
module source-size limits, output validation, dependency registration, and an
authorization-aware host. It must be opt-in and auditable. It must not run in
the ASP.NET request process merely because a page contains `#invoke`.

## Remaining options

1. Keep the rejecting provider for compatibility inventory and safe display.
2. Add an out-of-process sandboxed Scribunto provider after threat-model,
   authorization, and parity review.
3. Provide an offline pre-render service that consumes the same typed contract
   and stores approved, auditable output without granting runtime access to the
   web application.

None of these options is part of CW-M3-005A.

## Experimental typed module bundle

The experimental MoonSharp worker does not resolve titles or read imported
storage. `MediaWikiScribuntoModuleBundle` is prepared by the host from the
existing import repository and historical `asOfUtc` policy. Its root and each
pre-resolved dependency carry the source wiki identity, canonical Module
title, source page and revision identities, content model, UTF-8 SHA-256, raw
source, and explicit dependency relationships. The worker rejects missing or
ambiguous relationships, conflicting identities, modified checksums,
cross-wiki entries, unsafe/path-like titles, unsupported content models, and
source bundles over the shared dependency budget.

The worker's `require`, `mw.loadData`, and `mw.loadJsonData` implementations
can address only that validated bundle. There is no Lua title lookup, package
search path, filesystem loader, network loader, or unrestricted MoonSharp
package API. A module must also list the requested child in its explicit
relationship; a bundled but undeclared module is rejected. Executed module
identities, revisions, source wiki values, and checksums are returned as
typed rendering dependencies. The bundle and all module caches are invocation
local.

`mw.loadData` supports bounded Lua table results with immutable proxy tables;
recursive table results are rejected. `mw.loadJsonData` supports bounded JSON
content-model entries with scalar, array, and object conversion, duplicate-key
rejection, depth/node limits, and immutable tables. These are an experimental
subset, not a claim of MediaWiki parity.

The frame carries trusted host metadata for `frame:getTitle()` and an
unambiguous parent title when supplied. Positional and named arguments remain
scalar, missing values remain `nil`, empty values remain empty strings, and
named insertion is stable. `frame:preprocess`, `frame:expandTemplate`, and
`frame:callParserFunction` remain explicitly unsupported because no safe typed
host callback protocol exists for them.
