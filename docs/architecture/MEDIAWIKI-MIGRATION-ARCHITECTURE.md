# MediaWiki migration architecture (CW-M3-002)

MediaWiki is the authoritative source. This milestone is discovery and design only: it does not connect to production, modify the source wiki, or import content. TightWiki SQLite is not a migration source and `SqliteToMariaDb` is not an importer.

## Read-only source boundary

Prefer an administrator-produced MediaWiki XML dump, with a separately supplied file/image export and API metadata if required. The Action API and REST API are optional read-only supplements for namespaces, deleted/suppressed metadata, file details, and gaps not present in an XML dump. A database export may be analyzed offline only after it is explicitly supplied. No tool in this milestone accepts credentials or opens a database connection.

`MediaWikiInventory` provides two offline paths. The original inventory command reads an XML export and emits JSON counts/lists for page titles and IDs, revision IDs, current bodies, timestamps, namespaces, redirects, named and anonymous contributors, template definitions/transclusions/parameters, parser functions, Lua invocations, references, categories, files and file metadata, links, magic words, language links/variants, and extension tags. The CW-M3-002A reader uses a secure `XmlReader`, materializes only one page at a time, preserves typed source records and original Wikitext, and reports warnings/unsupported XML separately. Run `dotnet run --project MediaWikiInventory -- <export.xml>` for the inventory or `dotnet run --project MediaWikiInventory -- preview <export.xml>` for the import preview. Neither path accepts a source URL, API credential, or database connection.

## Offline import foundation (CW-M3-002A)

`MediaWikiXmlReader` exposes `MediaWikiImportHeader`, `MediaWikiPage`, `MediaWikiRevision`, `MediaWikiContributor`, namespace definitions, warnings, and preserved unsupported XML elements. Source page/revision IDs remain strings and are not target IDs. Contributor kinds distinguish named users, IP contributors, unknown anonymous contributors, and missing contributors; no application identities are created. Revision records retain timestamp, comment, content model/format, deletion/suppression flags, exact Wikitext, and whether the source text element was present.

`MediaWikiImportPreview` streams pages from the reader and emits page/revision counts, namespace and contributor distributions, redirect counts, content-model distributions, SHA-256 hashes of each preserved Wikitext body, warnings, and unsupported-element counts. XML DTDs and external entities are prohibited, external resources are never resolved, malformed input is rejected, and missing optional fields produce explicit warnings. The preview is report-only and never writes to a target database.

## Offline import planning (CW-M3-002B)

`MediaWikiImportPlanner` performs a deterministic two-pass read over the offline XML: it first indexes source page identities, then creates a typed plan without assigning target database IDs. `NamespaceMappingConfiguration` supports the common MediaWiki namespace IDs and configurable source aliases. Unknown or ambiguous namespaces block the plan. Target navigation preserves Unicode titles, retains namespace distinctions, and detects normalized case collisions.

Run:

```text
dotnet run --project MediaWikiInventory -- plan --input <export.xml> --output <plan.json> [--mapping <mapping.json>]
dotnet run --project MediaWikiInventory -- validate --plan <plan.json> --input <export.xml> [--mapping <mapping.json>]
```

Plans preserve source page/revision IDs, revision order, timestamps, comments, contributor provenance, parent IDs, SHA-1/minor metadata, content model/format, original Wikitext, and SHA-256 content hashes. Dependency scanning is intentionally lexical and incomplete; it reports that limitation, extracts template/parser-function/module/category/link/file/reference/extension dependencies, and blocks missing templates/modules and unsupported extension tags. A plan with blocking findings is never import-ready. Input and mapping identities are checksummed, and validation rejects reuse against a different export or mapping.

## Target mapping

| MediaWiki | ContinuumWiki.NET target strategy |
| --- | --- |
| page ID and title/namespace | `Page` identity plus a migration provenance record; never force source IDs into target integer IDs |
| revision ID, timestamp, comment, original text | `PageRevision` fields plus immutable source revision metadata and original Wikitext blob |
| username/IP | Existing profile only when an explicitly approved identity match exists; otherwise an import attribution record. Never create login users for historical contributors |
| redirect | Page navigation/redirect metadata, preserving the exact target and unresolved status |
| category membership | `PageTag` only after retaining the original `Category:` target and namespace semantics |
| file/image | `PageFile`/revision attachment after checksum, source title, MIME, dimensions, and provenance validation |
| templates/modules | Dedicated imported template/module namespace and adapter registry; do not treat `DefaultFeatureTemplates` as equivalent |

## Persistence execution (CW-M3-003A)

The dedicated `MediaWikiImport` .NET 10 CLI consumes a validated CW-M3-002B plan and re-reads the source XML through `MediaWikiXmlReader`. Its explicit disposable-target preflight requires `Purpose=disposable-mariadb`, an exact target database name, supported MariaDB schema versions, and an empty `Page`/`PageRevision` destination. It does not run during application startup.

`MySqlMediaWikiImportSchemaInitializer` provisions the versioned `MediaWikiImportBatch`, `MediaWikiImportPage`, `MediaWikiImportRevision`, and `MediaWikiImportDependency` tables. One import transaction creates target pages and ordered revisions, stores exact Wikitext in `PageRevision.Body`, records source attribution and dependency/redirect provenance, reconciles counts and hashes, and only then commits `Completed`. Failure rolls back page/revision/provenance writes and records a `RolledBack` batch for a fresh retry.

Run `scripts/verify-m3-003a.sh` for the disposable MariaDB 11.8 and development-only HTTP harness. That earlier harness verifies application reachability and historical route selection; the CW-M3-003B/004A harness additionally verifies the bounded imported renderer and staged media output.

The existing model is not a lossless MediaWiki model. `TwPage.Id` and `TwPageRevision.Revision` are target-local integers; `PageRevision` stores target metadata/body but has no MediaWiki revision ID, namespace identity, contributor kind, or original Wikitext provenance field. `PageTag` is a string tag projection, and `PageReference` stores normalized outgoing navigation rather than source link syntax. `PageFile`/`PageFileRevision` store target-local file revisions, MIME/size/data hash, and attachment relationships, while version-2 media provenance retains MediaWiki file SHA-1, archive metadata, source title, source revision IDs, and SHA-256 values without duplicating `Data`. `DeletedPageRevision` supports target deletion state but cannot by itself represent MediaWiki suppression permissions. These gaps require dedicated provenance/import tables or an equivalent provider-neutral migration store before controlled execution.

Target navigation must not blindly call `TwNavigation.Clean`: it lowercases and removes characters and uses `::` as its namespace separator, while MediaWiki uses case/Unicode title rules and `:` namespace syntax. A deterministic mapping table must preserve the canonical source title, namespace key/name, aliases, and collision decisions alongside the target navigation.

Deleted or suppressed revisions must remain represented as restricted provenance with visibility policy, not silently omitted. Import checkpoints are keyed by source wiki identity, page ID, revision ID, and content hash. Execution belongs in a separately invoked pipeline, never application startup.

## Rendering foundation (CW-M3-004A)

The application selects rendering by the completed import provenance row for
the target page and target revision. Native TightWiki pages remain on
`WikiEngine.Transform`; imported revisions use the bounded
`MediaWikiWikitextRenderer`. Historical revisions are selected independently,
and cache keys include the typed source descriptor so native and imported
renderings cannot collide. The original `PageRevision.Body` and its retained
MediaWiki SHA-256 are not rewritten.

The renderer subset covers headings, emphasis, links, paragraphs, lists,
horizontal rules, redirects, basic imported file/image links, categories,
references, bounded ordinary template transclusion, parameters, inclusion
tags, and the documented core parser functions. Lua, unknown extensions, and
unsafe URLs are diagnosed and marked without unsafe evaluation. HTML is
encoded, media is resolved only through existing imported attachment mappings
and routes, and input, output, expansion, depth, block, time, and diagnostic
limits are enforced. Template cache versions include the selected imported
template revisions; historical rendering uses the explicit source-timestamp
policy documented in `MEDIAWIKI-TEMPLATE-EXPANSION.md`.

## Pipeline

1. Acquire a cryptographically recorded, read-only export.
2. Validate XML, namespaces, timestamps, IDs, text encoding, and media hashes.
3. Inventory and produce a compatibility report.
4. Dry-run namespace, identity, page, revision, template, category, link, and media mappings.
5. Block or explicitly quarantine unsupported constructs.
6. Execute idempotently in batches with checkpoints and retryable media staging.
7. Reconcile counts, IDs, hashes, links, categories, redirects, files, and attribution.
8. Render sampled pages through the typed native/imported dispatch and compare against MediaWiki exports before approval.

Required safeguards include bounded template expansion, no network fetching from imported markup, HTML sanitization, SSRF protection, deterministic retry, and an audit log. Production hosting and migration are outside CW-M3-002.

## Milestones

CW-M3-002A: secure source reader. CW-M3-002B: deterministic offline planning. CW-M3-003A: synthetic MariaDB page persistence, reconciliation, and disposable HTTP verification. CW-M3-003B: offline media manifest, secure binary staging, historical attachment persistence, HTTP download verification, and backup/restore rehearsal. CW-M3-004A: bounded Wikitext foundation. CW-M3-004B: bounded template/parser-function expansion. CW-M3-004C: tables, galleries, and reviewed media adapters. CW-M3-004D: magic words and controlled functions. CW-M3-005A: Scribunto inventory and a rejecting execution boundary. CW-M3-007: reconciliation, render diffs, rollback rehearsal, and owner approval.

## Scribunto boundary (CW-M3-005A)

Scribunto references are inventoried offline but are not executed. The typed
`MediaWikiScribuntoInvocation`, `IMediaWikiScribuntoExecutionHost`, and
`IMediaWikiScribuntoExecutor` contracts preserve module/function arguments,
calling revision context, dependency identity, and diagnostic locations. The
default executor rejects `#invoke`; it does not embed Lua or expose database,
filesystem, network, process, reflection, or mutable application capabilities.
`MediaWikiScribuntoScanner` records literal `require`, `mw.loadData`, and
`mw.loadJsonData` dependencies, marks dynamic references uncertain, and the
planner reports missing and cyclic Module namespace dependencies. See
`MEDIAWIKI-SCRIBUNTO-BOUNDARY.md` for the historical and future-sandbox policy.

## Remaining source-access requirements

The real corpus still requires an administrator-approved XML export containing all revisions, a namespace/site configuration export, and a separate file/media export with checksums and metadata. Request read-only API access only if the dump omits file details, deleted/suppressed revision metadata, language links, or extension inventory. Also require a documented list of enabled MediaWiki extensions, parser functions, Scribunto modules, custom tags/hooks, and Continuum Universes templates. No production counts are asserted until those artifacts are supplied and inventoried offline.
