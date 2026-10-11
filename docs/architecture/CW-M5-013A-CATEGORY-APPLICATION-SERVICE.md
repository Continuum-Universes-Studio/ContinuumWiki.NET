# CW-M5-013A — Category Membership Application-Service Integration

## Classification

`CATEGORY_APPLICATION_SERVICE_SOURCE_ONLY_VERIFIED`

This milestone adds the application boundary around the existing bounded
MediaWiki category planner. It is source-only verified. The category
repository is still an explicit fail-closed implementation, so this does not
certify SQL persistence, transactional durability, category-page MVC, or
production readiness.

## Existing lifecycle audit

The current page mutation path is:

```text
POST Page/Edit or Page/Create
  -> ASP.NET [Authorize]
  -> SessionState.RequirePermission(Create/Edit)
  -> protected-page Moderate check
  -> posted revision comparison / navigation uniqueness checks
  -> PageMetadataRebuilder snapshot
  -> ITwApplicationPageRepository.CreatePageAsync or UpdatePageAsync
  -> repository commits the authoritative Page/PageRevision mutation
  -> editor state/session redirect work
```

`CreatePageAsync` creates revision 1 and `UpdatePageAsync` uses the posted
expected revision and creates the next revision transactionally. The existing
page repository remains the authority for the committed revision. Moves are
ordinary authorized edits that preserve `Page.Id`; revert publishes a new
revision through the same update path. Delete has its own `Delete` permission
and revision check. Restoration is an administrative repository operation.

There is no existing category hook, event bus, category repository, or shared
page/category transaction. The new seam is therefore deliberately post-commit:
the controller invokes the application service only after a successful page
create/update call. A category failure is logged as an incomplete indexing
outcome and cannot turn the successful page mutation into a false indexing
success. Durable atomicity and retry/outbox behavior remain future work.

GET rendering and historical reads do not invoke the service. They remain
read-only. Import and maintenance callers must provide their own explicit
authorized mutation context; no historical read is accepted as authority.

## Application-service contract

`TwCategoryMembershipApplicationService` accepts:

- `TwCategoryIndexingRequest` — page ID, canonical page navigation, expected
  committed revision, mutation kind, and optional actor ID;
- a mandatory authorization callback — the caller supplies the existing page
  mutation authorization decision; and
- the request cancellation token.

The service rejects missing authorization, historical reads, invalid page or
revision values, and navigation mismatches. It does not accept source text or
client-provided revision content as authoritative input.

The callback receives `TwCategoryIndexAuthorizationContext`. The MVC page
save adapter uses `SessionState.HoldsPermission(..., TwPermission.Edit)`
after the controller has already performed `RequirePermission`. Import and
maintenance callers must supply an equivalent explicitly authorized context;
there is no implicit allow-all path.

## Authoritative source and extraction

`ApplicationTwCategoryIndexPageSource` re-reads the current page revision from
`ITwApplicationPageRepository`, verifies it equals the requested revision,
verifies the canonical navigation, and resolves MediaWiki provenance through
`ITwMediaWikiImportRepository`.

`MediaWikiCategoryIndexExtractor` calls the existing
`MediaWikiWikitextRenderer`, then passes its bounded result to
`MediaWikiCategoryIndexPlanner`. No second parser or expansion pipeline was
introduced. Complete plans retain canonical category identity, sort keys, and
source provenance. Missing templates, rejected Lua, unsupported extensions,
budget exhaustion, unavailable source, and other planner blockers prevent
replacement. Native TightWiki pages are explicitly classified as
`UnsupportedSourceFormat` by this imported-MediaWiki extractor rather than
being silently treated as having no categories.

An incomplete plan never reaches the repository. In particular, an empty
category result cannot erase a previously verified index after a failed or
partial expansion.

## Revision and idempotency behavior

The service passes the authoritative revision in
`TwCategoryMembershipReplacementRequest`. It reads the existing membership
set, uses `TwCategoryMembershipReplacementPolicy` for source-only no-change
and stale-revision decisions, and delegates an actual replacement only for a
complete changed set.

The future repository must enforce the same expected-revision compare-and-swap
atomically. The fakes verify request ordering and decisions only; they do not
claim durable concurrency correctness.

Outcomes are typed as:

- `Applied`
- `NoChange`
- `IncompleteExtraction`
- `RevisionConflict`
- `UnauthorizedMutation`
- `InvalidRequest`
- `Cancelled`
- `RepositoryUnavailable`
- `PersistenceFailure`

Exceptions are sanitized into stable diagnostic codes. Protected source text,
credentials, and raw exception details are not returned by the service.

## Repository and DI behavior

The service is registered in both SQLite and isolated MySQL application DI
paths, together with:

- `ApplicationTwCategoryIndexPageSource`;
- `MediaWikiCategoryIndexExtractor`; and
- `RejectingTwCategoryMembershipRepository`.

The rejecting repository is intentional: no category SQL implementation or
provider registration exists yet, and it throws a known unavailable failure
that the service maps to `RepositoryUnavailable`. It never reports `Applied`
and never opens a database by itself. Existing provider readiness guards and
schema initialization are unchanged.

## Page-save, import, and maintenance boundaries

The page controller invokes indexing after successful create/update commit,
including ordinary page moves. A failed page save does not reach the service.
Delete, restore, and controlled import still require explicit future
maintenance seams because the current category repository cannot invalidate or
replace durable state. The design requirements are:

- deletion must invalidate/cascade through an authorized repository operation;
- restoration must perform a fresh authorized current-body extraction;
- import must preserve source page/revision provenance and reject incomplete
  plans; and
- historical reads must never mutate current category state.

No distributed transaction or general event bus was added. A future durable
implementation needs a reviewed retry/reconciliation mechanism for the
post-commit gap.

## Regression coverage

`MediaWikiCategoryApplicationServiceTests` uses fakes only and covers:

- authorized page mutation and authorized import contexts;
- missing/rejected authorization and historical-read rejection;
- authoritative revision conflict before extraction;
- complete extraction and replacement ordering;
- incomplete extraction with no repository call;
- no-change idempotency;
- unavailable and failing repositories;
- cancellation before work and cancellation propagated to the source; and
- explicit typed non-success outcomes.

Existing renderer/planner tests continue to cover direct, transcluded,
conditional, unsupported, and resource-limited category extraction behavior.

## Verification limits

This milestone performs no database provisioning, connection, schema creation,
membership write, Lua activation, Cargo activation, migration-planner change,
or host-security change. The rejecting Lua executor and production-disabled
configuration remain unchanged.

The required full Debug test, Release build, JSON validation, and whitespace
checks are the source-only gates. A future authorized category persistence
milestone must separately verify MariaDB version readiness, schema migration,
atomic repository behavior, visibility-safe member queries, deletion/restore,
and category MVC.
