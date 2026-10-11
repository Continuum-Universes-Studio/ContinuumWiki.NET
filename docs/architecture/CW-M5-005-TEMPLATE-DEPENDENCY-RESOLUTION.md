# CW-M5-005 Historical template dependency resolution

## Scope and verdict

`TEMPLATE_DEPENDENCY_RESOLUTION_PARTIALLY_VERIFIED`.

This milestone verifies and hardens the bounded imported-Wikitext template
path with synthetic repositories and source-reviewed persistence logic. It
does not establish historical MediaWiki parity, complete historical source
availability, Lua/Scribunto parity, Cargo parity, or production migration
readiness.

## Resolution pathway

For each transclusion, the renderer:

1. Parses balanced `{{...}}` and top-level arguments.
2. Rejects explicitly unsupported forms such as `subst:` and leading-colon
   main-namespace transclusions rather than silently changing namespace.
3. Normalizes the supported Template namespace form, URI escapes, underscores,
   surrounding whitespace, and passes the canonical bare title to the import
   repository.
4. Resolves the page and eligible revision through
   `ITwMediaWikiImportRepository.ResolveTemplateAsync` using the imported
   batch and viewed source timestamp.
5. Records typed source page/revision identity and resolution status.
6. Applies inclusion rules, expands parameters, and recursively expands the
   selected body under the active request budget.
7. Tracks the typed `SourcePageId:SourceRevisionId` identity in the active
   expansion chain for cycle detection.

`MySqlMediaWikiImportRepository` now canonicalizes explicit and bare template
   inputs and matches both stored `Example` and `Template:Example` title forms
   while retaining `SourceNamespaceId = '10'`. This corrects the mismatch
   between the import schema's full `OriginalTitle` and the expander's bare
   lookup title without changing the persistence contract.

## Findings matrix

| Area | Classification | Finding |
| --- | --- | --- |
| Bare/explicit Template titles, underscores, Unicode | `VERIFIED_CORRECT` | Supported forms normalize deterministically and retain the existing repository boundary. |
| Leading-colon main-namespace transclusion | `UNSUPPORTED_BEHAVIOR` | Preserved and diagnosed as `UnsupportedTemplateSyntax`; the engine does not reinterpret a main-namespace page as a template. |
| Non-Template namespace syntax | `UNSUPPORTED_BEHAVIOR` | Diagnosed as `UnresolvedTemplateNamespace`; no namespace alias is invented. |
| Stored template title form | `CONFIRMED_DEFECT` then fixed | MySQL lookup could compare a bare lookup only against a full stored title. Both canonical stored forms are now matched. |
| Eligible historical revision | `VERIFIED_CORRECT` | Timestamp eligibility is `source timestamp <= viewed timestamp`; exact boundary is eligible and future revisions are excluded. Selection is timestamp-based, not revision-ID-based. |
| Missing page vs no eligible revision | `VERIFIED_CORRECT` | Repository statuses remain distinct: `Missing` maps to `MissingTemplate`; an existing page without an eligible revision maps to `RestrictedTemplate`. |
| Resolved revision without source body/identity | `CONFIRMED_DEFECT` then fixed | It was previously reported as `MissingTemplate`; it now reports `TemplateSourceUnavailable` without fabricating content. |
| Resolver exception | `CONFIRMED_DEFECT` then fixed | Exceptions escaped rendering and could expose internal failure behavior. They now produce sanitized `TemplateResolutionFailed`, retain dependency evidence, and stop later dependency work. |
| Direct/transitive/diamond dependencies | `VERIFIED_CORRECT` | Reached dependencies use the current historical context and typed identities. |
| Repeated dependencies | `VERIFIED_CORRECT` | Completed repeated references do not produce false cycle diagnostics. |
| Active cycles | `VERIFIED_CORRECT` | Self, multi-node, and parser-function cycles terminate through the existing active-chain check and budgets. |
| Lazy branches | `VERIFIED_CORRECT` | Unselected dependencies and cycles are not resolved for `#if`, `#ifeq`, and `#switch` paths. |
| Dynamic title and repeated whitespace semantics | `EVIDENCE_INSUFFICIENT` | No new title grammar or competing normalization algorithm was introduced; unsupported semantics remain explicit. |

## Diagnostic behavior

Ordinary missing templates remain recoverable: the original invocation is
preserved, `MissingTemplate` is emitted, and surrounding source continues to
render. A restricted or ambiguous repository result retains its existing
typed diagnostic. A resolved target with missing body or source identity is
classified as `TemplateSourceUnavailable`.

Unexpected resolver exceptions are sanitized to
`TemplateResolutionFailed`; the exception text is not copied into the
diagnostic or rendered HTML. The dependency record uses
`ResolutionFailed`, and the private expansion context becomes terminal so a
secondary timeout or unrelated dependency cannot replace the first cause.
Cancellation remains cancellation: `OperationCanceledException` is not
converted into a resolver failure. The existing linked 500 ms expansion
deadline still becomes `ExpansionTimeLimitExceeded`.

The current repository status model does not expose separate deleted,
suppressed, and no-eligible-revision enum values. The MySQL query excludes
deleted/suppressed revisions; when none remain, the existing `Restricted`
status is retained. A future provenance-schema/API change would be required
to distinguish those cases without inference.

## Limits and security

The existing limits remain unchanged: 500 ms, 10,000 expansion operations,
depth 16, bounded input/output, and at most 100 diagnostics. Dependency
tracking remains request-scoped and bounded by the existing expansion work;
there is no static mutable cache or unrestricted graph traversal.

No Lua was executed in-process, Cargo was not activated, production databases
and MediaWiki were not accessed, the strict migration planner was unchanged,
and worker/host security settings were untouched. Original Wikitext, Lua,
historical references, Cargo snapshots, and authenticated captures remain
unchanged.

## Evidence

Focused synthetic coverage is in
`TightWiki.Tests/Unit/MediaWikiWikitextRendererTests.cs`, including title
normalization, historical boundary selection, resolver failures, source-body
availability, diamond/repeated dependencies, cycle handling, lazy branches,
diagnostic limits, and cancellation. The machine-readable result is
`artifacts/validation/mediawiki-template-dependency-resolution-v1.json`.

The evidence is implementation and synthetic compatibility evidence only. A
disposable MariaDB run with a complete historical export remains the next
step for authentic source/revision verification.
