# CW-M5-002 — CSP-Compatible TemplateStyles Asset Delivery

## Status

TemplateStyles delivery is implemented through the existing MVC page path. The
renderer remains the only CSS parser and sanitizer. This milestone does not
claim full MediaWiki TemplateStyles parity, production migration readiness,
Lua/Cargo activation, or worker host certification.

## Renderer-to-MVC flow

`MediaWikiWikitextRenderer.RenderAsync` returns an immutable
`MediaWikiStylesheet` descriptor for each accepted, deduplicated reference.
The descriptor contains the imported batch, source page and revision, source
timestamp and hash, sanitizer-policy version, sanitized CSS hash, scope,
stable cache identity, and deterministic reference order. `PageController`
maps that descriptor to the smaller
`TwMediaWikiStylesheet` identity before putting it in `TwPageCache`; CSS bytes
are not stored in the MVC page cache.

`PageDisplayViewModel.StylesheetUrls` is populated on cache hits and misses.
`Views/Page/Display.cshtml` emits one encoded same-origin `<link
rel="stylesheet">` in the existing layout `Styles` section. Native TightWiki
pages continue to use an empty stylesheet collection.

## Asset identity and authorization

The link query value is a one-hour ASP.NET Data Protection token containing
only an opaque, signed stylesheet identity. It is not a bearer permission, a
content hash token, or a filesystem path. The endpoint is
`GET /Page/TemplateStyles?t=...`.

On every request the endpoint:

1. rejects missing, oversized, malformed, expired, or structurally invalid
   tokens;
2. resolves the source title through the imported MediaWiki provenance;
3. rejects a missing/deleted source target page;
4. re-evaluates `Read` permission through the existing `TwSessionState` page
   permission evaluator;
5. re-resolves the source with the recorded historical timestamp;
6. runs the existing TemplateStyles sanitizer again; and
7. requires exact source-page, source-revision, target-revision, source-hash,
   sanitized-hash, and sanitizer-policy matches before returning CSS.

Invalid, stale, restricted, unauthorized, deleted, suppressed, or internally
unresolvable assets all return the same `404` response. This avoids disclosing
private source existence or provenance. Suppressed imported revisions are
excluded by the existing MySQL revision resolver rather than substituted with
a later revision.

## Delivery and CSP

The response is `text/css; charset=utf-8` with `X-Content-Type-Options:
nosniff`, `Cache-Control: private, no-store`, and `Pragma: no-cache`. No raw
source CSS is accepted from the request and no arbitrary file path is read.
The page uses a same-origin stylesheet link; no `style-src 'unsafe-inline'`
directive or other CSP relaxation was added. The repository has no existing
global CSP header, so this milestone does not broaden one. TemplateStyles
selectors remain scoped by the CW-M5-001 sanitizer to `.wiki-page-body`.

## Caching and invalidation

The page cache stores only renderer-approved identity and provenance. Asset
responses are conservatively non-cacheable because the application does not
have a complete public/private publication policy for imported source pages.
Authorization is therefore evaluated independently on every asset request.
The signed URL expires after one hour; an expired page link is a safe stale
asset and returns `404`. A source revision, source hash, sanitizer policy, or
sanitized output change also fails the exact identity comparison. The policy
version is `cw-templatestyles-v1` and is part of both the asset identity and
the revalidation check.

## Failure diagnostics and limits

Renderer diagnostics remain typed and bounded under the CW-M5-001 budgets.
HTTP responses intentionally do not expose source bodies, private revision
metadata, sanitizer diagnostics, or exception details. Detailed failures are
logged through the existing controller logger without token or CSS contents.

## Security preservation

No database schema, historical source, strict migration planner, worker,
systemd unit, bubblewrap restriction, Lua configuration, or Cargo provider was
changed. The rejecting Scribunto and Cargo defaults remain unchanged, and no
in-process or production Lua execution was introduced.

## Verification

The focused renderer and delivery-boundary suite passes 52 tests with no
failures. It covers malformed and oversized identifiers, signed historical
re-resolution, sanitized CSS-only responses, current permission denial,
deleted source denial, stale revision identity denial, renderer provenance,
unsafe CSS, resource limits, deduplication, and request isolation. The full
Debug suite passes 298 tests with 22 intentional skips; the Release test build
and application build pass with zero warnings and errors.

The focused suite also starts a disposable loopback MVC host and routes the
actual `PageController.TemplateStyles` action over HTTP. It verifies route
binding, response headers, sanitized-only content, and authorized-versus-
unauthorized access through the existing permission evaluator. Broader
cookie-authenticated page-render and permission-revocation scenarios remain
an application-host verification gate because they require the application's
real identity and database fixtures. No test substitutes a permissive
authorization implementation.

## Limitations

The endpoint depends on imported source pages being mapped to the existing
TightWiki page permission model. A deployment that needs MediaWiki-specific
suppression or cross-tenant ACL semantics must extend that existing permission
architecture before enabling delivery; this implementation does not invent a
parallel ACL system. The supported CSS subset and historical source
availability remain those documented by CW-M5-001.
