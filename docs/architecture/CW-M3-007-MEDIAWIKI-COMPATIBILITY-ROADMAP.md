# CW-M3-007 MediaWiki compatibility roadmap

CW-M3-007A is an evidence and prioritization milestone. It does not rewrite
the renderer, alter the Lua worker, expand the Cargo provider, or change the
strict import planner. The machine-readable inventory is
`artifacts/validation/mediawiki-compatibility-gap-inventory-v1.json`.

## Baseline

The current Debug suite passes 259 tests with 22 intentional skips and no
failures. The Release build passes with zero warnings and errors. The
disposable MediaWiki import is verified, and the authorized Unix-domain worker
responds through `/run/continuumwiki/scribunto-worker.sock`. These are
implementation, storage, and transport results; they are not historical
MediaWiki parity.

Six independent MediaWiki success references have corresponding ContinuumWiki
outputs and comparison reports. All six are currently `FAILED`. The two
remaining reference fixtures are `REFERENCE_ERROR` on MediaWiki itself and
are retained as negative evidence, not successful rendering targets.

CW-M3-006G established that the only authenticated Cargo rows are the
current-state snapshot `cargo-authentic-current-20261011-1`. No
revision-correlated or time-bounded historical Cargo dataset exists in the
authorized evidence. Historical parity therefore remains unavailable.

CW-M3-007B characterization is now implementation-verified. Finite template
and parser-function expansion completes within the unchanged budgets; direct,
mutual, longer, parser-function, and nested Scribunto cycles terminate through
existing recursion checks; and deterministic operation and wall-clock limits
remain explicit. Typed Scribunto error results now stop later expansion within
the current context, preserving the primary failure and source text without
changing the public renderer contract. The four historical positive outputs
that already contain timeouts were not rewritten or rerun by this milestone,
so post-correction timeout reproducibility remains open.

## Root-cause findings

### Historical data and typed Cargo shapes

The positive fixture failures report
`CargoProviderUnavailable: no authorized historical Cargo dataset is
configured`. This is an evidence boundary, not proof of a missing Lua API or
a parser defect. The current provider supports bounded, explicitly supplied
Cargo results; helper-backed relationship fields and `Divines.Children` still
lack a complete typed result contract.

The next Cargo work should be synthetic/current-state-labeled relationship
result testing. It must preserve table and field allowlists, bounded joins,
provenance-bearing bundles, no arbitrary SQL, no writes, and the rejecting
production default. It must not substitute current rows for historical rows.

### Source dependencies and revisions

The Jovan diagnostic output identifies missing `Template:Divine` and related
unresolved links in the disposable source subset. The authorized invocation
inventory does contain `Template:Divine@9469` (source page 520), so the current
blocker is failure to select/import that exact revision, not proof that the
template is absent. This is a dependency-complete import issue, not a
demonstrated renderer defect. `Module:DivineMeta` also has no inventoried
revision at or before the Template:Divine@9469 timestamp; the nearest revision
9470 is later and must not be selected silently.

The MediaWiki reference errors are independent: `Template:Mbox` reports the
missing `Module:Message box/configuration`, while `Template:Extension` reports
missing `Template translation` and two TemplateStyles pages. Those failures
remain negative-test evidence. Missing source content must not be fabricated.

### Expansion limits

Several positive outputs also report `ExpansionTimeLimitExceeded`. The renderer
has finite input, output, depth, operation, diagnostic, and wall-clock limits.
Current evidence proves bounded termination after unavailable Cargo or source
dependencies, but does not prove that a finite workload would exceed the same
budget with complete inputs. A focused characterization milestone must separate
finite-workload behavior from recursive/cyclic expansion and must retain
fail-closed limits. Production budgets must not be raised merely to make the
fixtures pass.

### Infobox and extension behavior

The bounded ContinuumInfobox adapter is now implemented in
`MediaWikiInfobox.cs`: native `#cuinfobox` and well-formed legacy
`<infobox>` fragments are represented as typed, bounded tokens and rendered
through the existing safe inline pipeline. It is deliberately a schema subset,
not PHP execution or a claim of full extension parity. The adapter accepts
surrounding Wikitext, rejects malformed XML back to the existing unsupported
tag path, and preserves source text in the import store. Schema JSON is
versioned (`schemaVersion: 1`) and validates field/group/alias bounds,
allowlisted layouts, dotted/bracket field paths, inheritance depth, missing
parts, duplicate definitions, and inheritance cycles. XML processing disables
DTD/entity resolution, applies element/depth/attribute bounds, and classifies
panels, navigation, arbitrary attributes, and unknown tags explicitly. The
Jovan output also diagnosed `<mermaid>` as unsupported; CW-M3-007E now
preserves that source through the safe static fallback while still diagnosing
actual diagram rendering as unavailable. `#cargo_store` is recognized but
intentionally not evaluated; adding it would introduce write semantics
inconsistent with the current read-only Cargo boundary. These are extension or
feature gaps, not permission to execute MediaWiki PHP or arbitrary extension
code.

Static `mw.ext`, `mw.getCurrentFrame`, and `os.*` references are inventory
evidence only. The explicit diagnostic `mw.ext.cargo` path is not evidence that
all `mw.ext` behavior is implemented, and lexical presence alone must not drive
new API implementation.

## Prioritized sequence

1. **CW-M3-007B — bounded Wikitext expansion compatibility.** COMPLETE for
   characterization and targeted diagnostic correction. No budget increase or
   historical parity claim was made.
2. **CW-M3-007C — dependency-complete template and infobox subset.** The
   ContinuumInfobox implementation audit is complete at the bounded adapter
   level. Native `#cuinfobox` and a well-formed legacy `<infobox>` subset now
   use immutable JSON-safe schema records and the existing bounded renderer;
   PHP is not executed. The Jovan dependency context remains partially blocked:
   `Template:Divine@9469` is present in the source inventory but was not selected
   by the disposable context, and no valid `Module:DivineMeta` revision exists
   at or before that historical timestamp. See
   `artifacts/validation/mediawiki-continuuminfobox-compatibility-v1.json`.
3. **CW-M3-007D — typed Cargo relationship result shapes.** IMPLEMENTATION
   VERIFIED; authentic helper data unavailable. The typed bounded relationship
   contract, synthetic projector, schema-only helper catalog, and fail-closed
   provider hook are implemented. The authenticated snapshot contains no helper
   rows, so no relationship is positively activated and `Divines.Children`
   remains unsupported.
4. **CW-M3-007E — bounded extension compatibility.** IMPLEMENTATION VERIFIED;
   safe static fallback only. The observed Mermaid source now uses a typed,
   bounded, HTML-encoded static representation with explicit
   `ExternalRendererUnavailable` diagnostics. No SVG, browser, JavaScript,
   network, or external renderer was introduced. `#cargo_store` remains
   unsupported and write-free; TemplateStyles remains a separate reference-side
   error/source-evidence gap.
5. **CW-M3-007F — compatibility regression and evidence reconciliation.**
   EVIDENCE RECONCILIATION VERIFIED; host certification blocked; historical
   parity pending. Fresh focused/full regression passed (115/0/0 and
   285/22/0), Release build passed with 0 warnings and 0 errors, the eight
   private references and six existing comparison pairs were integrity-checked,
   and the two reference-error fixtures remain negative evidence. Current Cargo
   evidence remains non-historical; no production or security defaults changed.
   Fresh real-module execution and dedicated-identity host certification remain
   operator gates. See
   `artifacts/validation/mediawiki-compatibility-certification-v1.json`.

Historical Cargo parity is not a prerequisite for unrelated synthetic or
current-state implementation tests, but no such result may be reported as
historical parity.

### CW-M5-003/B — TemplateStyles host authorization

HOST AUTHORIZATION BLOCKED; DISPOSABLE LIFECYCLE HARDENED. The actual MySQL
path registers ASP.NET Core Identity backed by the MySQL EF store, then
executes the normal routing, authentication, and authorization middleware.
The TemplateStyles action is intentionally anonymous at the MVC attribute
level but performs a signed-identity, live-source, current-Read-permission,
historical-revision, sanitizer-policy, and exact-hash check on every request.

The hardened operator path uses one exact pre-created `cw_m5_003_...` schema,
container-local root over the private Unix socket, an exact-schema application
grant, loopback-only publishing, immutable MySQL 8.4 image identity, an
approval-file gate, and run-labelled cleanup. No host-side root connection or
prefix-wildcard lifecycle grant remains in the M5-003B procedure. No database
or identity records were accessed or written. The authorization gate and
static tests pass, while the real host test remains skipped because the
environment has no approved target or execution marker. Existing M5-002
controller and loopback MVC checks remain synthetic/partial evidence; they do
not establish real cookies, persisted identities, revocation,
conditional-request ordering, or concurrent host isolation. See
`artifacts/validation/mediawiki-templatestyles-host-authorization-v1.json`
and
`artifacts/validation/mediawiki-templatestyles-host-authorization-m5-003b-v1.json`.

### CW-M4-002 — dependency-complete disposable migration diagnostics

SOURCE-ONLY DIAGNOSTICS VERIFIED; IMPORT BLOCKED; DEPENDENCY CLOSURE PARTIAL.
The revision-aware investigation confirms the Jovan chain
`Jovan@9482 -> Module:DivineTree@9481 -> Template:Divine@9469` and identifies
`Module:DivineFamily@9467` as temporally eligible. The available inventory does
not contain importable source bodies, and there is no valid inventoried
`Module:DivineMeta` revision at or before the `Template:Divine@9469` context;
the later `9470` revision is not substituted. The expected raw XML export is
absent, so no plan or disposable database write was attempted. The six
reference-success and two reference-error classifications remain unchanged,
and existing comparison artifacts remain historical evidence only.

The next permitted step is to obtain and hash a complete source-preserving
export, independently authorize a `cw_m3_*` disposable target, and then run the
existing `scripts/verify-m3-006c-diagnostic.sh` entry point. Host worker
certification and historical Cargo parity remain independent acceptance gates.

### CW-M4-003 — controlled migration readiness

READINESS PLAN VERIFIED; CONTROLLED MIGRATION CONDITIONAL; PRODUCTION MIGRATION
BLOCKED. The machine-readable assessment is
`artifacts/validation/mediawiki-migration-readiness-v1.json`, with the
operational procedure in
`docs/operations/CW-M4-003-CONTROLLED-MIGRATION-READINESS.md`.

The current evidence supports additional bounded source-only diagnostics. It
does not authorize a disposable import: the complete historical source bodies,
independent disposable-target authorization, fresh import verification, worker
host certification, and historical Cargo provenance remain incomplete. The
authenticated Cargo snapshot is current-state only; helper relationships remain
inactive. Production migration is `NO_GO`, and no earlier milestone
classification is elevated by this plan.

### CW-M4-004 — authorized historical source acquisition

SOURCE INVESTIGATION VERIFIED; ACQUISITION BLOCKED. The required local/private
inventory contains metadata for `Template:Divine@9469`,
`Module:DivineFamily@9467`, `Jovan@9482`, and `Module:DivineTree@9481`, but no
authenticated raw source bodies or historical source package. No new MediaWiki
API, database, backup, Cargo, worker, or production access was attempted.
`Module:DivineMeta@9470` remains temporally ineligible and was not substituted.

The acquisition report is
`artifacts/validation/mediawiki-historical-source-acquisition-v1.json`; the
operator procedure is
`docs/operations/CW-M4-004-HISTORICAL-SOURCE-ACQUISITION.md`. The six
reference-success/two-reference-error classifications and historical Cargo
blocker remain unchanged. A later source-complete package may improve import
readiness, but it does not establish historical rendering parity by itself.

## Security and acceptance gates

The rejecting Scribunto executor remains the default, production Lua remains
disabled, and the installed systemd/bubblewrap worker remains isolated. Lua
receives no database, filesystem, or network access. The strict migration
planner and all resource budgets remain unchanged. Original Wikitext, Lua,
reference captures, current Cargo rows, and schema inventories remain outside
the roadmap's mutation scope.

CW-M3-007A is investigation-verified when this baseline, the eight-fixture
classification, root-cause inventory, evidence limitations, and prioritized
follow-on sequence are recorded. It does not claim compatibility parity or
host certification.

### CW-M4-005 — operator evidence acquisition handoff

OPERATOR HANDOFF VERIFIED; AUTHORIZATIONS PENDING. The four evidence tracks
are recorded independently: historical source recovery, disposable migration
authorization, Scribunto host certification, and historical Cargo provenance.
All fourteen CW-M4-003 readiness gates are reconciled in
`artifacts/validation/mediawiki-operator-evidence-handoff-v1.json`, with
approval states defaulting to `NOT_REQUESTED` and `UNVERIFIED`.

The operator procedure is
`docs/operations/CW-M4-005-OPERATOR-EVIDENCE-HANDOFF.md`, and the approval
template is
`docs/operations/templates/CW-M4-005-OPERATOR-APPROVAL-TEMPLATE.json`.
This milestone performed no external acquisition, database import, worker
activation, deployment, permission change, production mutation, or Cargo row
export. It does not elevate CW-M4-003, establish historical parity, or certify
the host. The next step is CW-M4-006, limited to the first explicitly
authorized track.

### CW-M5-001 — native TemplateStyles compatibility

TEMPLATESTYLES CORE VERIFIED; DELIVERY INTEGRATION BLOCKED. The renderer now
recognizes canonical self-closing TemplateStyles references, resolves CSS
through the existing imported page/revision repository, preserves historical
revision provenance, applies bounded structural CSS sanitization, scopes
selectors to the page content root, deduplicates assets per render request,
and emits typed first-cause diagnostics for source, revision, content-model,
syntax, security, and resource-limit failures.

The implementation and evidence are recorded in
docs/architecture/CW-M5-001-TEMPLATESTYLES-COMPATIBILITY.md and
artifacts/validation/mediawiki-templatestyles-compatibility-v1.json.
The prior MVC delivery gap was addressed separately by CW-M5-002 without
changing the sanitizer, source evidence, migration planner, or security
defaults. Existing migration-readiness decisions, production NO_GO, rejecting
provider defaults, Lua/Cargo boundaries, and worker isolation remain
unchanged.

### CW-M5-002 — CSP-compatible TemplateStyles asset delivery

IMPLEMENTATION VERIFIED; AUTHORIZATION VERIFICATION PARTIAL; FULL MEDIAWIKI
PARITY NOT CLAIMED. The MVC page path
now propagates immutable TemplateStyles identities through the existing page
cache, emits encoded same-origin stylesheet links, and serves only after
current source-page permission, live-source, exact historical revision/hash,
sanitizer-policy, and re-sanitization checks. Invalid, stale, restricted,
deleted, suppressed, and unauthorized assets share a non-cacheable `404`
response with `nosniff`; no raw CSS, arbitrary file loading, inline-style CSP
relaxation, production database change, Lua/Cargo activation, worker change,
or strict-planner change was made.

The implementation design is recorded in
`docs/architecture/CW-M5-002-TEMPLATESTYLES-ASSET-DELIVERY.md`; the machine
readable evidence is
`artifacts/validation/mediawiki-templatestyles-delivery-v1.json`. Focused
renderer coverage remains green and app compilation succeeded. Full Debug,
Release, and any host-dependent end-to-end verification must be reported only
from the current run's actual output.

### CW-M5-004 — native Wikitext parser compatibility

NATIVE_PARSER_COMPATIBILITY_PARTIALLY_VERIFIED. The bounded imported-Wikitext
renderer now has synthetic regression coverage for `#if`, `#ifeq`, `#switch`,
`#expr`, `#ifexpr`, nested template arguments, lazy branch selection,
malformed input, recursion, and finite expansion budgets. A confirmed
`#switch` label-only fall-through defect was corrected without changing source
Wikitext, revision selection, dependency resolution, budgets, Lua, Cargo, or
the strict migration planner.

The focused renderer suite passed 51/51, the full Debug suite passed 308/308
with 23 intentional skips, and the Release build passed with zero warnings and
errors. Evidence is recorded in
`docs/architecture/CW-M5-004-NATIVE-PARSER-COMPATIBILITY.md` and
`artifacts/validation/mediawiki-native-parser-compatibility-v1.json`.

The result is a verified bounded subset only. Unsupported extension parser
functions remain diagnosed and unevaluated; synthetic tests do not establish
full MediaWiki 1.45 behavior, exact historical HTML parity, Lua/Cargo parity,
worker host certification, or migration readiness. Those gates remain
independent and unchanged.

### CW-M5-005 — historical template dependency resolution

TEMPLATE_DEPENDENCY_RESOLUTION_PARTIALLY_VERIFIED. The bounded template path
was audited for title normalization, namespace handling, historical timestamp
selection, direct/transitive/diamond dependencies, repeated references,
cycles, lazy branches, resolver failures, cancellation, and resource limits.

Two confirmed defects were corrected. The MySQL resolver now matches the
import schema's full `Template:` titles as well as canonical bare lookup
titles. Resolver exceptions and resolved targets without source bodies are now
represented by sanitized, typed diagnostics with preserved dependency
evidence; resolver exceptions become terminal first-cause failures. No future
historical revision is selected, and no source content is fabricated.

The implementation and evidence are recorded in
`docs/architecture/CW-M5-005-TEMPLATE-DEPENDENCY-RESOLUTION.md` and
`artifacts/validation/mediawiki-template-dependency-resolution-v1.json`.
The current status contract intentionally retains `Restricted` when deleted,
suppressed, and no-eligible-revision cases cannot be distinguished. This
milestone does not alter migration readiness, production Lua/Cargo defaults,
worker security, historical reference captures, or production data.

### CW-M5-006 — template title semantics and namespace compatibility

TEMPLATE_TITLE_SEMANTICS_PARTIALLY_VERIFIED. The renderer now parses a
bounded, planner-aligned namespace/title subset: bare and explicit Template
titles, leading-colon and Main namespace transclusion, configured aliases,
underscore/whitespace normalization, Unicode-preserving names, dynamic title
expansion, invalid-title rejection, and historical revision lookup after
normalization. The MySQL import repository exposes this through a typed
namespace-aware transclusion boundary while retaining timestamp-aware,
fail-closed revision selection.

Evidence is recorded in
`docs/architecture/CW-M5-006-TEMPLATE-TITLE-SEMANTICS.md` and
`artifacts/validation/mediawiki-template-title-semantics-v1.json`. This is
not complete MediaWiki title parity, exact HTML parity, migration readiness,
Lua/Cargo certification, or host-worker certification; disposable MariaDB
execution remains an independent evidence gate.

### CW-M5-007 — historical revision visibility and restriction diagnostics

HISTORICAL_REVISION_VISIBILITY_PARTIALLY_VERIFIED. The imported revision
schema retains authoritative `IsDeleted`, `IsSuppressed`, `SourceTextPresent`,
`SourceTextState`, `SourceTimestamp`, and content-model metadata. The resolver
now preserves internal reasons for no eligible revision, deleted, suppressed,
and unavailable source-body cases while retaining the existing conservative
public `RestrictedTemplate` and TemplateStyles not-found behavior. Missing
source text can no longer be executed as an empty target placeholder.

The evidence is recorded in
`docs/architecture/CW-M5-007-HISTORICAL-REVISION-VISIBILITY.md` and
`artifacts/validation/mediawiki-historical-revision-visibility-v1.json`.
No historical requester-authorization timeline exists, so AccessDenied is
not inferred from lookup failure. This milestone does not change strict
migration readiness, production data, Lua/Cargo defaults, worker isolation,
or TemplateStyles authorization.

### CW-M5-008 — structural Wikitext and HTML rendering compatibility

STRUCTURAL_WIKITEXT_RENDERING_PARTIALLY_VERIFIED. The existing typed renderer
was audited without introducing a parallel parser. A confirmed mixed-list
defect was corrected so marker runs such as `*#` preserve ordered child
semantics, and definition-list markers now produce bounded `<dl>`/`<dt>`/
`<dd>` structures instead of paragraph text. Existing heading, table,
attribute-security, link, emphasis, paragraph, template-expanded structure,
and resource-limit behavior remains covered by the bounded renderer tests.

Evidence is recorded in
`docs/architecture/CW-M5-008-STRUCTURAL-WIKITEXT-RENDERING.md` and
`artifacts/validation/mediawiki-structural-wikitext-rendering-v1.json`.
This does not establish complete MediaWiki HTML parity, historical parity,
Lua/Cargo parity, host authorization certification, or migration readiness.

### CW-M5-009 — MediaWiki table structure and attribute compatibility

MEDIAWIKI_TABLE_COMPATIBILITY_PARTIALLY_VERIFIED. The existing bounded typed
table parser was retained. A confirmed tokenizer defect was corrected so
quoted attribute values containing structural pipe characters no longer
corrupt cell boundaries. Safe caption `class`, `id`, and `aria-label`
attributes are now validated and emitted, duplicate captions are diagnosed
with deterministic last-caption-wins behavior, and unclosed tables retain
bounded balanced output with typed diagnostics.

Evidence is recorded in
`docs/architecture/CW-M5-009-MEDIAWIKI-TABLE-COMPATIBILITY.md` and
`artifacts/validation/mediawiki-table-compatibility-v1.json`. Existing row,
cell, nested-template, nested-link, span, output, cancellation, and security
bounds remain in force. This does not establish complete MediaWiki HTML or
historical parity, production migration readiness, Cargo/Lua parity, or host
certification.

### CW-M5-010 — internal links, categories, files, and heading anchors

LINK_CATEGORY_MEDIA_COMPATIBILITY_PARTIALLY_VERIFIED. The bounded imported
renderer now preserves local and page-qualified fragments, emits deterministic
semantic heading IDs with duplicate disambiguation, distinguishes category
membership directives from leading-colon category links, preserves category
sort-key metadata per render, and distinguishes explicit file-page links from
embedded media references. Unsafe media path material is rejected before
repository resolution.

Evidence is recorded in
`docs/architecture/CW-M5-010-LINK-CATEGORY-MEDIA-COMPATIBILITY.md` and
`artifacts/validation/mediawiki-link-category-media-compatibility-v1.json`.
The existing canonical navigation, imported-media resolver, HTML/URL
sanitization, expansion budgets, production Lua/Cargo defaults, and migration
readiness decisions remain unchanged. Category persistence, generated
thumbnail parity, historical parity, and host certification remain separate
gates.

### CW-M5-011 — native category membership indexing and category pages

CATEGORY_MEMBERSHIP_SOURCE_ONLY_VERIFIED. The imported renderer now enriches
category directives with canonical `Category::` identity, sort keys, source
revision provenance, and deterministic duplicate handling. A bounded,
read-only `MediaWikiCategoryIndexPlanner` produces an immutable membership plan
and preserves existing membership whenever template/dependency/runtime or
resource-limit diagnostics make the expansion incomplete. Rendering and
historical reads do not write category state.

The repository audit found only the unrelated `PageTag` feature; no native
category table, category repository, category-member query, visibility-aware
category listing, or category MVC route exists. No schema or production
database change was authorized or performed. Durable persistence, transactional
save/import integration, authorization-aware pagination, category pages,
historical category evidence, and MediaWiki parity remain blocked gates.
Evidence is recorded in
`docs/architecture/CW-M5-011-CATEGORY-MEMBERSHIP-INDEXING.md` and
`artifacts/validation/mediawiki-category-membership-indexing-v1.json`.

### CW-M5-012 — disposable category persistence authorization and integration preparation

CATEGORY_PERSISTENCE_PREPARED_AUTHORIZATION_BLOCKED. The persistence audit
confirmed SQLite remains the default, MySqlConnector 2.6.2 and
MySql.EntityFrameworkCore 10.0.9 are the MySQL-capable dependencies, and the
actual application runtime guard requires MariaDB 11.8.x. No category table,
repository, visibility policy, or category MVC route exists, and the existing
PageTag feature is not interchangeable with MediaWiki categories.

The source-only boundary now includes typed category identity, membership,
replacement, bounded member-query contracts, typed mutation outcomes,
idempotency/revision-conflict/incomplete-index policy, and an explicit
authorization gate test that never opens a database. A two-table design is
proposed using the existing Page identity: PageCategoryIndexState and
PageCategoryMembership. No schema migration, provider registration,
provisioning command, database connection, or database mutation was executed.

Database authorization is `AUTHORIZATION_NOT_ESTABLISHED`; production
migration remains `NO_GO`. Evidence is recorded in
`docs/architecture/CW-M5-012-CATEGORY-PERSISTENCE-READINESS.md` and
`artifacts/validation/mediawiki-category-persistence-readiness-v1.json`.

### CW-M5-013A — category membership application-service integration

CATEGORY_APPLICATION_SERVICE_SOURCE_ONLY_VERIFIED. The page-save lifecycle
was audited and the smallest post-commit integration seam was added. Existing
`SessionState` page-mutation authorization remains the authority; the service
requires an explicit authorization callback and independently re-reads the
current committed page revision before extraction. Historical reads and direct
indexing attempts without authorization are rejected.

`MediaWikiCategoryIndexExtractor` reuses the bounded imported MediaWiki
renderer and `MediaWikiCategoryIndexPlanner`. Incomplete expansion,
unsupported source/runtime behavior, unavailable source, cancellation, and
revision conflicts never submit a replacement. Complete changed sets use the
existing typed replacement contract; unchanged sets return `NoChange`.

The application service and source adapter are registered in both application
DI paths with an explicit `RejectingTwCategoryMembershipRepository`. No live
category SQL, schema migration, database connection, or provider activation
was introduced. Page create/edit calls the service only after the existing
page repository mutation returns successfully; indexing failure cannot be
reported as page/index success. Delete, restore, and controlled import remain
documented future maintenance seams.

Evidence is recorded in
`docs/architecture/CW-M5-013A-CATEGORY-APPLICATION-SERVICE.md` and
`artifacts/validation/mediawiki-category-application-service-v1.json`.
This source-only result does not certify category persistence, transaction
atomicity, visibility-safe member queries, category MVC, production
migration, Lua/Cargo behavior, or host-worker certification.

### CW-M5-013B — category indexing reconciliation and failure recovery

CATEGORY_RECONCILIATION_SOURCE_ONLY_VERIFIED. The post-commit audit confirms
that page publication remains successful when category indexing is unavailable,
incomplete, canceled, conflicted, or failed; those outcomes are now logged as
typed non-success results rather than being treated as current indexing.

The existing application-service boundary now exposes an explicit authorized
reconciliation operation. It re-resolves the current authoritative page
revision, rejects historical or superseded requests, reruns the existing
bounded renderer/planner, preserves incomplete memberships, and delegates only
eligible replacements to the existing repository contract. `Applied` without
durable read-back is `Unknown`, not `Current`; `Pending` is never fabricated.
Retry eligibility is deterministic and always requires a new authorized
request where applicable. No queue, timer, hosted worker, live SQL, schema,
database connection, Lua/Cargo activation, or security-policy change was
introduced.

Evidence is recorded in
`docs/architecture/CW-M5-013B-CATEGORY-RECONCILIATION.md` and
`artifacts/validation/mediawiki-category-reconciliation-v1.json`.
Durable CAS, recovery history, outbox/retry scheduling, category persistence,
delete/restore/import hooks, and category MVC remain separate authorized
milestones.

### CW-M5-014 — authorized category persistence and disposable verification

CATEGORY_PERSISTENCE_SOURCE_IMPLEMENTED_AUTHORIZATION_BLOCKED. The durable
MariaDB category repository and explicit category schema initializer are now
implemented behind the existing provider-neutral Dapper transaction boundary.
PageCategoryIndexState is independent from PageCategoryMembership, so verified
empty indexes have durable revision evidence. Replacement locks the
authoritative Page revision, rejects stale generations, replaces memberships
and state atomically, preserves incomplete indexes, and rolls back failures.
Consistency read-back is required before and after application-service
classification; membership rows alone cannot claim Current.

Visibility-safe category member queries apply the existing SQL Read permission
rules before bounded pagination, including page-specific deny precedence, and
do not expose source revision metadata or unrestricted counts. The rejecting
repository remains registered by default. Explicit activation requires MySQL,
an enabled category-persistence flag, approved schema version, and an existing
connection-factory registration; application startup never invokes the
category schema initializer or activation method.

Source-only tests and builds pass. The gated disposable MariaDB tests were
skipped because `DATABASE_AUTHORIZATION_NOT_ESTABLISHED`; no database
connection, schema mutation, production change, Lua/Cargo activation, or host
security change occurred. Evidence is recorded in
`docs/architecture/CW-M5-014-CATEGORY-PERSISTENCE.md` and
`artifacts/validation/mediawiki-category-persistence-v1.json`.
Database certification, concurrent real CAS, production activation, and
category MVC remain separate gates.
