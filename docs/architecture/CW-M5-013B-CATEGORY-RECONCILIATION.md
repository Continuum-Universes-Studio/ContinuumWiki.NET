# CW-M5-013B — Category Indexing Reconciliation and Failure Recovery

## Classification

`CATEGORY_RECONCILIATION_SOURCE_ONLY_VERIFIED`

This milestone adds typed recovery and reconciliation behavior around the
CW-M5-013A post-commit application service. It does not certify durable
category persistence or transactional page/category consistency.

## Existing post-commit behavior

The page controller performs this sequence:

```text
[Authorize] -> RequirePermission(Create/Edit)
  -> posted revision and protected-page checks
  -> PageMetadataRebuilder
  -> CreatePageAsync/UpdatePageAsync commit
  -> category IndexAfterCommittedMutationAsync
  -> existing success response/redirect
```

The page repository is authoritative for the committed page revision. The
category call is deliberately after the page commit. A non-success category
result is logged with structured page ID, revision, disposition, consistency,
recovery eligibility, and stable diagnostic code; it does not change the
already-committed page response into a failed publication. No source body,
credentials, or private exception details are logged.

The confirmed limitation is the post-commit gap, not an incorrect rollback:
there is no shared durable category transaction or outbox. Retry was not
previously present, and no automatic retry worker has been added.

## Consistency states

`TwCategoryIndexConsistencyState` is an observation, not a claim of database
certification:

- `Current` — the repository membership read matched the authoritative
  revision and desired set, producing a typed no-change result.
- `Pending` — reserved for a future authoritative operational mechanism; this
  source-only service never invents it.
- `Stale` — revision evidence shows the attempted index work is superseded or
  its replacement precondition is rejected.
- `Incomplete` — bounded extraction did not produce an eligible replacement.
- `Failed` — a replacement or repository operation failed.
- `Unavailable` — the required persistence boundary is unavailable.
- `Unknown` — evidence is insufficient, including a repository-reported
  `Applied` result without a durable read-back.

An `Applied` result from a fake or future repository is not independently
promoted to `Current`. Source-only tests verify how that result is classified;
they do not certify persistent state.

## Reconciliation contract

`ITwCategoryMembershipApplicationService.ReconcileAsync` accepts a
`TwCategoryReconciliationRequest` containing page identity, optional expected
revision, explicit mutation kind, and actor identity, plus the existing
authorization callback.

The operation:

1. validates canonical navigation and rejects historical reads;
2. requires an explicit authorized maintenance/import/mutation context;
3. resolves the current authoritative page revision;
4. rejects an expected revision that has been superseded;
5. re-runs the existing bounded renderer and category planner against the
   current source;
6. refuses incomplete plans;
7. delegates only complete changed sets to the existing replacement contract;
8. returns typed consistency and recovery eligibility; and
9. performs no queueing, timer scheduling, or background retry.

The request never supplies authoritative source text or a captured membership
set. A retry always re-reads the current page and re-extracts categories.
There is no external MVC reconciliation endpoint in this milestone.

## Recovery eligibility

`TwCategoryRecoveryEligibility` is deterministic:

- `EligibleAfterNewAuthorization` covers cancellation, transient persistence
  failure, and unavailable repository cases;
- `EligibleAfterDependencyRecovery` covers incomplete extraction or source
  dependency recovery, but only after a new authorized request;
- `RequiresCurrentRevision` covers superseded or stale work;
- `RequiresAuthorization` covers unauthorized work; and
- `NotEligible` covers invalid requests and successfully current/no-change
  work.

The service never blindly replays stale membership data. It does not claim
that an eligible retry will succeed while the rejecting repository or an
incomplete dependency remains unchanged.

## Supersession and idempotency

The source adapter first resolves the current revision. If revision A is
requested after revision B is authoritative, reconciliation returns
`Superseded`/`Stale` and does not extract or replace A. If the current revision
changes between reconciliation's initial read and its indexing pass, the
second authoritative read returns `RevisionConflict` before replacement.

Repeated current-revision reconciliation uses the existing replacement policy:
duplicate directives remain deduplicated, sort keys and provenance remain
typed, and an unchanged membership read returns `NoChange`. A future durable
repository must still enforce atomic compare-and-swap; these fakes cannot
prove that database property.

## Incomplete extraction and cancellation

Missing templates, unavailable source, rejected Lua, unavailable Cargo,
unsupported extensions, resolver failures, and renderer budget exhaustion
remain non-authoritative. They produce `Incomplete`/`IncompleteExtraction`,
preserve existing memberships, and cannot submit an empty replacement.

Cancellation is propagated before authorization, through current-revision
resolution and extraction, and into repository calls. A canceled operation
returns `Cancelled` with unknown consistency and requires a new authorized
request; it is never reported as applied or pending.

## Repository unavailability and startup

`RejectingTwCategoryMembershipRepository` remains registered in SQLite and
isolated MySQL DI paths. It throws a known unavailable error and cannot report
success. Startup therefore remains compatible without a category database.
No schema, SQL, database connection, migration, or provider readiness guard
was changed.

## Regression coverage and limits

The focused recovery tests use fakes and cover current-revision reconciliation,
authorization and historical rejection, superseded revisions, revision change
before replacement, no-change/current classification, repository conflicts,
incomplete extraction, unavailable/failed persistence, cancellation, and
post-commit ordering. Existing renderer/planner tests remain the extraction
coverage.

No database operation, provisioning, background service, Lua/Cargo activation,
migration-planner change, or host-security change was performed. Durable
pending state, recovery history, CAS, retry scheduling, and category MVC remain
future authorized persistence work.
