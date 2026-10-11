# CW-M5-007 Historical revision visibility and restriction diagnostics

## Scope and classification

`HISTORICAL_REVISION_VISIBILITY_PARTIALLY_VERIFIED`.

This milestone audits the imported MediaWiki revision contract and improves
internal diagnostic precision where the imported metadata is authoritative.
It does not claim complete MediaWiki deleted-revision or suppression parity,
database-backed authorization certification, or production migration
readiness.

## Persistence evidence

The MediaWiki import schema stores the following authoritative fields per
source revision in `MediaWikiImportRevision`:

| Field | Meaning retained by import | Classification |
| --- | --- | --- |
| `SourceRevisionId` | Source revision identity | `AUTHORITATIVELY_DISTINGUISHABLE` |
| `SourceTimestamp` | Historical cutoff input | `AUTHORITATIVELY_DISTINGUISHABLE` |
| `IsDeleted` | Deleted state supplied by the source export | `AUTHORITATIVELY_DISTINGUISHABLE` for imported evidence |
| `IsSuppressed` | Suppressed state supplied by the source export | `AUTHORITATIVELY_DISTINGUISHABLE` for imported evidence |
| `SourceTextPresent` | Whether source text was present | `AUTHORITATIVELY_DISTINGUISHABLE` |
| `SourceTextState` | Imported state label (`Present`, `Missing`, `Deleted`, `Suppressed`) | `AUTHORITATIVELY_DISTINGUISHABLE` as stored import metadata |
| `ContentModel` | Source content model | `AUTHORITATIVELY_DISTINGUISHABLE` |
| `PageRevision.Body` | Target-side stored body, required by the application schema | `AMBIGUOUS` by itself; it is not evidence that source text existed |
| Current `Page.IsDeleted` and permission services | Present-day target access | `AUTHORITATIVELY_DISTINGUISHABLE` for current access only, not historical visibility |

There is no separate requester-aware revision authorization field in the
MediaWiki import contract, and no historical authorization timeline. The
repository therefore does not infer `AccessDenied` from a failed lookup and
does not reconstruct historical access from current permissions.

## Internal resolution contract

`TwMediaWikiTemplateResolutionStatus` remains compatible: `Missing`,
`Resolved`, `Restricted`, and `Ambiguous` are unchanged. The immutable target
now carries an internal `TwMediaWikiRevisionResolutionReason` when imported
metadata supports a more precise audit classification:

- `NoEligibleRevision`: no source revision is at or before the requested
  cutoff.
- `SourceBodyUnavailable`: metadata is present, but source text or the safe
  target body is unavailable.
- `DeletedRevision`: an eligible candidate is marked deleted.
- `SuppressedRevision`: an eligible candidate is marked suppressed; this has
  deterministic precedence over `DeletedRevision` when both flags are set.
- `AccessDenied`: reserved for an authoritative permission-aware resolver;
  the current import repository does not emit it.
- `UnsupportedContentModel`: reserved for a typed content-model boundary;
  strict planning and TemplateStyles validation remain the current gates.
- `ResolutionFailed`: reserved for a typed persistence failure; renderer
  exceptions remain sanitized as before.

Deleted and suppressed rows remain non-renderable. The MySQL resolver now
queries their retained metadata only to classify the internal reason; it does
not return their body. A missing source-text flag is converted to a null
target body, preventing an imported empty placeholder from being executed as
valid Wikitext or Lua.

## Historical cutoff semantics

The resolver continues to apply `SourceTimestamp <= viewed timestamp` (with
the established null-timestamp handling), then orders eligible revisions by
source timestamp descending and target revision descending. It never falls
back to a future revision. If all matching revisions are after the cutoff, the
internal reason is `NoEligibleRevision`. Exact timestamp boundaries and
revision-ID/order independence remain covered by existing tests.

The retained deletion/suppression flags are source-export facts. They are not
treated as a reconstruction of what a different requester could see at the
historical time. Current target-page deletion and current permission checks
remain separate present-day authorization gates.

## Public diagnostic policy

The public renderer continues to map all restricted protected cases to the
existing generic `RestrictedTemplate` diagnostic. TemplateStyles continues to
map them to `TemplateStylesSourceRevisionUnavailable`, and its HTTP delivery
path returns `404 NotFound` for invalid, stale, deleted, suppressed,
restricted, or unauthorized assets.

The internal reason is not copied into rendered HTML, public diagnostic text,
signed asset payloads, or CSS responses. Source revision IDs, timestamps,
bodies, exception details, and hidden title existence are not added to public
responses. Resolver exceptions remain sanitized and first-cause preservation
remains unchanged.

## Cross-feature security

TemplateStyles still re-resolves the exact historical source identity,
rechecks the live source page and `Read` permission, validates the signed
asset hashes, and rejects suppressed or deleted sources before CSS delivery.
The new internal reason does not bypass those checks. Lua remains disabled by
default through `RejectingMediaWikiScribuntoExecutor`; Cargo remains rejecting
by default; strict migration planning and all expansion/resource budgets are
unchanged.

## Regression evidence and limitations

Synthetic renderer tests cover missing templates, restricted templates,
source-body unavailability, resolver failures, historical cutoff behavior,
protected-reason redaction, TemplateStyles content/revision failures,
namespace transclusion, dynamic names, cycles, cancellation, and budgets.

The schema and SQL path are source-reviewed and compiled. No production
database, production MediaWiki, backup, or host authorization system was
accessed. Disposable MariaDB execution and database-backed authorization
certification remain separate gates. Because no independent historical
requester-visibility timeline exists, full deleted/suppressed parity remains
unverified.
