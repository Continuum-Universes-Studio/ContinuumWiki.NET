# CW-M5-015 — Native Category Pages and Lifecycle Integration

## Classification

`CATEGORY_PAGES_LIFECYCLE_SOURCE_VERIFIED`.

The native read-side category route, typed browse states, and page mutation
lifecycle hooks are implemented and source-tested. MariaDB behavior remains
unverified because this milestone performed no database connection,
provisioning, schema initialization, or mutation.

## Design decision

Native category browsing is exposed at `GET /Category/{categoryTitle}`. The
route accepts a category title or an existing `Category::title` navigation and
normalizes it through `TwNamespaceNavigation` via `TwCategoryIdentity`. The
renderer emits native category-route links for category membership links;
ordinary page navigation remains unchanged.

The browse service first validates the bounded request and category namespace,
then reads category-wide index readiness through the repository boundary. It
does not treat repository failure, unindexed pages, or stale/inconsistent
index state as an empty category. Only a current index state permits the
visibility-filtered member query. The provider applies page visibility before
pagination and exposes no unrestricted total count. Razor output uses default
encoding and sanctioned MVC URL generation for member links and pagination.

The category GET route has no mutation, reconciliation, queue, or indexing
side effect. Category membership never grants page-read permission; the
category namespace is authorized before repository browse data is requested,
and member visibility is enforced again by the repository query.

## Lifecycle integration

Create and edit already call the existing post-commit category indexing
service. This milestone adds:

- page-controller deletion invalidation after a successful exact-revision
  delete;
- administrator deletion invalidation after a successful exact-revision
  delete; and
- administrator restoration indexing after the committed current revision is
  reloaded and metadata is rebuilt.

Deletion uses a new typed application-service boundary. The durable provider
must lock the page row, require `IsDeleted = TRUE` and the exact authoritative
revision, remove visible membership rows, and write an `Unknown` index-state
row in one transaction. Restore reuses the existing bounded extraction and
revision-aware replacement path. A failed post-commit index operation is
logged as a distinct outcome and never changes the already-successful page
mutation result.

The deleted-revision archive/restore operation does not publish an active page
and therefore is not treated as a category browse lifecycle event. No web
application import entry point was found; authorized import remains available
only through the existing explicit application-service contract.

## State handling

The read model distinguishes `InvalidCategory`, `InvalidRequest`,
`RepositoryUnavailable`, `Unindexed`, `IndexedEmpty`, `Populated`, and
`Inconsistent`. `Stale`, `Incomplete`, `Failed`, and `Unknown` durable states
are represented as `Inconsistent` with their typed consistency state. Empty
visible results are presented as “no accessible pages”; hidden rows cannot
affect a displayed count or page boundary.

## Safety and verification

The SQLite default and MySQL development startup paths still register
`RejectingTwCategoryMembershipRepository`. No automatic category provider
activation, startup migration, background task, PageTag reuse, Lua change, or
permission relaxation was made. Historical page reads do not invoke indexing;
all lifecycle indexing uses the committed authoritative revision and existing
CAS/recovery rules.

Focused category/renderer tests: 99 passed, 0 failed, 0 skipped.

Full Debug tests: 365 passed, 28 skipped, 0 failed. The skipped tests are
existing database-gated integration tests; no MariaDB connection was opened.

Release build: 0 warnings, 0 errors.

`python3 -m json.tool` and `git diff --check` passed for the milestone
artifacts. Database connections and mutations: 0.

Track B remains `DATABASE_AUTHORIZATION_NOT_ESTABLISHED`; production
migration remains `NO_GO`. Real MariaDB transaction, category-wide readiness,
delete/restore CAS, visibility SQL, and live route verification remain
operator-authorized database/runtime gates.
