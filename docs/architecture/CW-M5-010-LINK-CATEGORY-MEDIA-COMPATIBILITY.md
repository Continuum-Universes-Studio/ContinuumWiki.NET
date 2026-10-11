# CW-M5-010 Internal links, categories, files, and heading anchors

## Scope and classification

`LINK_CATEGORY_MEDIA_COMPATIBILITY_PARTIALLY_VERIFIED`.

This milestone covers the existing bounded MediaWiki renderer using the
application's canonical page-navigation and imported-media contracts. It does
not claim complete MediaWiki navigation, category persistence, thumbnail, or
historical HTML parity.

## Existing architecture

`MediaWikiTemplateExpander` runs before the typed inline/block renderer.
Internal links resolve through `ITwMediaWikiImportRepository.ResolvePageAsync`
and use the existing `TwNamespaceNavigation`-aligned target navigation returned
by the import repository. URLs are assembled by the renderer's existing
base-path helper with escaped navigation components; no parallel routing system
was introduced.

Imported media resolves through `ResolveMediaAsync` and the existing
permission-checked `/File/Binary/{page}/{file}/{revision}` route. The renderer
does not fabricate media metadata, filesystem paths, or new file endpoints.

## Supported subset

| Area | Classification | Behavior |
| --- | --- | --- |
| Internal page links | `VERIFIED_CORRECT` | Basic, piped, namespace-qualified, Unicode, underscore/space-normalized, and template-expanded links use imported page resolution and canonical target navigation. |
| Fragment links | `VERIFIED_CORRECT` | Local `#fragment` and page `Title#fragment` links preserve page identity and encode the fragment once. Fragment existence is not asserted without target heading metadata. |
| Heading anchors | `VERIFIED_CORRECT` | Imported headings receive deterministic text-derived IDs; duplicate base anchors receive `_1`, `_2`, and so on. IDs preserve Unicode and remove unsafe punctuation. |
| Categories | `VERIFIED_CORRECT` | `[[Category:Name|Sort key]]` is a per-render typed directive with optional sort-key metadata; exact duplicate directives are deduplicated. |
| Explicit category links | `VERIFIED_CORRECT` | `[[:Category:Name]]` is rendered as a page link and is not added to membership directives. |
| Embedded files | `VERIFIED_CORRECT` | `[[File:...]]`/`[[Image:...]]` use the existing imported-media resolver, bounded options, safe captions, and existing Binary route. |
| Explicit file-page links | `VERIFIED_CORRECT` | `[[:File:Name]]` is treated as a page link and does not invoke media embedding. |
| External links | `VERIFIED_CORRECT` | Existing HTTP/HTTPS-only URL validation, encoding, and `rel` policy remain active. |
| Advanced media/category behavior | `UNSUPPORTED_BEHAVIOR` | Thumbnail-generation parity, category persistence/index pages, remote media, full MediaWiki namespace aliases, and restricted metadata disclosure remain outside the subset. |

## Corrected behavior

The previous renderer discarded fragments after resolving only the page-title
portion of an internal link. It also emitted headings without IDs, so local
fragments had no matching target. Fragment-aware page URLs and deterministic
heading anchors now share the same bounded normalization rules.

Category directives were previously projected using the bare category title,
which could resolve the wrong page. Category membership now remains typed
per-render metadata, while the optional safe projection resolves the explicit
`Category:` page title. A leading colon distinguishes explicit category and
file-page links from directives and embeds.

Media filename path safety is enforced at the renderer boundary before the
repository resolver is called. Invalid path segments produce a safe placeholder
and diagnostic; no filesystem path or unreviewed media URL is emitted.

## Heading and fragment rules

Heading anchor text comes from rendered semantic inline text, not raw HTML.
Letters, digits, hyphens, colons, and bounded underscore separators are kept;
unsafe punctuation is removed. A per-render counter disambiguates duplicate
anchors deterministically. Fragment values are bounded by
`MaxFragmentCharacters` (512 by default); malformed or excessive fragments are
rejected with `InvalidFragment`.

This is an explicit compatibility subset, not a claim that every MediaWiki
heading-anchor historical algorithm is reproduced.

## Security and resource bounds

Display labels, captions, IDs, URLs, and media attributes are HTML-encoded.
Only `http` and `https` external URLs are accepted. Event-like markup,
unsafe schemes, control characters, traversal segments, arbitrary filesystem
paths, and unrestricted remote media remain rejected or inert.

Existing input/output, parser-depth, expansion, diagnostic, cancellation,
media-dimension, and template budgets are unchanged. The new fragment bound is
an additional finite rejection boundary; no recursive or second expansion pass
was introduced. Lua and Cargo remain disabled/rejecting by default.

## Known limitations and independent gates

The application has no MediaWiki category-membership persistence contract in
this renderer path, so category directives are returned as per-render metadata
and safely projected only when the category page resolves. Media processing
uses imported original bytes and existing routes; generated thumbnail parity is
not claimed. Historical source/reference parity, production authorization,
Cargo/Lua parity, host certification, and migration readiness remain separate
gates. No production system, database, MediaWiki service, worker setting, or
authenticated evidence was modified.
