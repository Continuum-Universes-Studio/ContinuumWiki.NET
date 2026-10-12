# CW-M5-016 — Controlled Category Reindexing and Recovery Operations

## Source-only result

The source implementation is classified
`CATEGORY_REINDEX_RECOVERY_SOURCE_PARTIAL`. The typed recovery boundary is
implemented and regression-tested, but database authorization, durable
category audit storage, and live provider verification remain unavailable.
Production migration remains `NO_GO`.

No database connection, provisioning, migration, category write, startup
activation, or MariaDB test was performed for this milestone. The
`RejectingTwCategoryMembershipRepository` remains the registered default in
both SQLite and MySQL development startup paths.

## Design

`ITwCategoryRecoveryService` exposes two explicit service-only operations:

* `PlanAsync` performs a read-only preview over one or a bounded batch of
  operator-supplied page IDs and optional navigation assertions.
* `ExecuteAsync` requires a new operation authorization and a fresh page
  authorization for every target, then delegates the write through the
  existing `ITwCategoryMembershipApplicationService.ReconcileAsync` pathway.

Targets are sorted by page ID, limited to 100, and rejected when duplicated or
invalid. They contain no client revision. The page source resolves the active
page and current revision from the existing application page repository, then
reuses `ITwCategoryIndexPageSource` for authoritative provenance and revision
checks. An optional checkpoint is only a caller-held SHA-256 scope fingerprint
and next index, hashing the sorted IDs and navigation assertions; it is not an
authorization token or durable job record.

Planning distinguishes `NoChange`, `NeedsReindex`, `Unindexed`,
`Inconsistent`, `IncompleteExtraction`, `RevisionChanged`, unavailable, and
unauthorized outcomes. Incomplete extraction never produces a replacement
request. The execution operation does not trust a prior plan: it resolves the
current page again and lets the existing service perform its revision-aware
reconciliation and repository CAS checks.

Batch execution returns a per-target result, continues after ordinary
per-target failures, and reports cancellation without claiming global
atomicity. Unauthorized, unavailable, missing, and invalid targets are
redacted in the result so the service does not turn a recovery preview into a
private page-existence oracle. Structured logs contain only the aggregate
disposition; no page titles, content, credentials, or membership totals are
written.

The returned checkpoint points to the next sorted target. Completed targets are
not revisited when that checkpoint is resumed; a new execution over the
original target list is allowed and remains idempotent through the existing
reconciliation contract. A changed page revision is handled by fresh
resolution and CAS rather than by checkpoint chronology.

No public or administrative mutation route was added. The existing generic
logging repository is not a category audit ledger, so durable audit/event
records and resumable checkpoint persistence remain explicit follow-up work
requiring an authorized contract. Callers must retain the returned plan and
execution results if operational evidence is needed.

## Contract inventory and threat model

The existing `ITwCategoryMembershipRepository` supplies consistency read-back,
page membership reads, atomic replacement, visibility-filtered category reads,
and deletion invalidation. `TwCategoryMembershipApplicationService` supplies
the mandatory authorization callback, current-revision source check,
bounded-render extraction, incomplete-expansion protection, and the typed
`ReconcileAsync` pathway. `ITwApplicationPageRepository` is used only through
the recovery page adapter to load an active current page; revision numbers are
not compared to infer chronology. The existing MVC admin convention uses
authorization guards, but the generic `ITwLoggingRepository` has no typed
category operation/audit contract, so an interactive recovery route would not
meet the milestone's audit requirement.

The main threats are unauthorized preview disclosure, forged page or revision
selection, preview-to-execution TOCTOU, stale checkpoint reuse, racing
edit/delete/restore operations, incomplete template expansion, partial batch
failure, and accidental activation of the rejecting repository. The service
addresses these by redacting denied or unavailable targets, omitting client
revisions, hashing the sorted bounded scope, re-authorizing on execution,
re-resolving the current page before each reconcile, preserving incomplete
membership state, returning per-target outcomes, and retaining rejecting DI.

## Verification boundary

The focused service tests cover authorization and redaction, bounded and
duplicate targets, no-op versus reindex-required planning, incomplete
extraction without a mutation call, fresh revision use, checkpoint scope
validation, partial batch failure, and typed cancellation. They use fakes and
do not connect to a database.

The following remain independent gates:

* `DATABASE_AUTHORIZATION_NOT_ESTABLISHED`: no real category provider or
  transaction/CAS evidence.
* `CATEGORY_REINDEX_RECOVERY_HOST_UNAVAILABLE`: no host/provider execution
  was attempted; the default rejecting adapter prevents accidental writes.
* `CATEGORY_REINDEX_RECOVERY_AUDIT_INCOMPLETE`: no durable category-specific
  audit and checkpoint contract exists.
* `CATEGORY_REINDEX_RECOVERY_SOURCE_PARTIAL`: source architecture is
  complete for the safe service-only boundary, but the operational recovery
  gate is not verified.
