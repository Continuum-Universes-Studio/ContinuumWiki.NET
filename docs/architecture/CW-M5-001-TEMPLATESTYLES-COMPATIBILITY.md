# CW-M5-001 — Native TemplateStyles Compatibility

Status: **TemplateStyles core verified; delivery integration blocked.**

CW-M5-001 adds a bounded native interpretation of the canonical
<templatestyles src="Template:Example/styles.css" /> syntax. It resolves
the source through the imported MediaWiki template/revision repository,
sanitizes a small CSS subset, and returns immutable typed stylesheet assets on
the render result. It does not execute MediaWiki PHP, fetch CSS, use Lua or
Cargo, or modify imported source.

## Integration point

MediaWikiWikitextRenderer.Parser.TryParseTag recognizes the extension tag and
creates a TemplateStylesNode. Resolution occurs during the existing
asynchronous inline-render pass, alongside page, template, media, reference,
and infobox resolution. The repository's existing
ResolveTemplateAsync(importBatchId, sourceTitle, asOfUtc) contract is used;
the template target now also carries the imported revision content model.

The render result exposes:

    IReadOnlyList<MediaWikiStylesheet> Stylesheets

The collection is created per render request. It is ordered by first accepted
reference and deduplicated by import batch, source page, source revision, and
source CSS hash. No application-wide mutable registry is used.

The current page controller consumes only HtmlResult, so it does not yet
deliver these assets into a page head or CSP-approved stylesheet endpoint.
The renderer intentionally does not append a style element or raw CSS to
HTML. Delivery is therefore a separate blocked integration gate.

## Source and revision semantics

- src must be a local Template: page reference.
- External URLs, filesystem paths, traversal segments, control characters,
  empty titles, and unsupported attributes are rejected.
- The source is resolved through the imported batch and the rendering
  context's SourceTimestampUtc.
- A historical render selects only a source revision eligible at that
  timestamp. A later revision is never silently substituted.
- Missing pages produce TemplateStylesSourceUnavailable.
- A missing eligible revision, restricted target, or ambiguous target produces
  TemplateStylesSourceRevisionUnavailable.
- The supported source content models are css, sanitized-css, and text/css.
  Other models produce TemplateStylesUnsupportedContentModel.
- Each accepted asset records source title, source page ID, source revision ID,
  target revision, source timestamp, content model, source SHA-256, and a
  deterministic cache identity.

## CSS subset and scoping

CSS is parsed as bounded top-level rules and declaration blocks. The supported
subset includes ordinary rules, type/class/ID selectors, basic descendant and
child/sibling combinators, and an allowlist of layout, spacing, typography,
color, border, display, sizing, alignment, and overflow properties.

Every accepted selector is prefixed with the existing page-content root:

    .wiki-page-body .example { color:#fff; }

Selectors containing html, body, head, :root, :host, :global, attribute
selectors, nesting, escape syntax, or unsupported selector syntax are
rejected. This prevents a stylesheet asset from targeting application
navigation, login controls, editor controls, or the shell.

The sanitizer rejects at-rules including @import, external url(...),
expression(...), JavaScript schemes, legacy behavior/binding constructs, CSS
variable/attribute/environment indirection, !important, unsafe properties,
and unsafe values. Sanitization is structural and allowlisted; it does not
use a removal-only regular expression.

## Resource budgets

The defaults are explicit in MediaWikiWikitextRenderOptions:

| Budget | Default |
| --- | ---: |
| Source CSS characters | 100,000 |
| Stylesheet references per render | 32 |
| CSS rules | 256 |
| Selectors | 512 |
| Declarations | 2,048 |
| Selector complexity | 16 |
| Sanitized output characters | 200,000 |
| Dependency depth | 1 |

Budget exhaustion produces
TemplateStylesCssResourceLimitExceeded. @import is prohibited, so CSS
dependency loading never recurses or performs external I/O.

## Diagnostics

The first applicable failure is preserved as a typed renderer diagnostic:

- TemplateStylesMissingSource
- TemplateStylesInvalidSource
- TemplateStylesSourceUnavailable
- TemplateStylesSourceRevisionUnavailable
- TemplateStylesUnsupportedContentModel
- TemplateStylesCssSyntaxInvalid
- TemplateStylesCssSecurityRejected
- TemplateStylesCssResourceLimitExceeded

Valid TemplateStyles produces no HTML marker and contributes one typed asset.
Repeated references to the same verified revision are silent duplicates.

## Tests and limitations

Synthetic regression coverage verifies self-closing and whitespace/quote
variants, missing and invalid sources, content-model rejection, historical
revision selection, future-revision rejection, safe selector scoping,
deduplication, request isolation, reference budgets, malformed CSS, unsafe
@import/URL behavior, declaration limits, and the absence of inline style
injection.

This is not full MediaWiki TemplateStyles parity. It does not support CSS
at-rules, external resources, arbitrary selectors, custom properties,
attribute selectors, nested CSS, or browser-specific behavior. A future
milestone must add a CSP-compatible asset consumer or endpoint before these
typed assets can affect rendered pages.

Production Lua, Cargo, databases, worker isolation, the strict migration
planner, historical evidence, and existing Wikitext execution budgets remain
unchanged.
