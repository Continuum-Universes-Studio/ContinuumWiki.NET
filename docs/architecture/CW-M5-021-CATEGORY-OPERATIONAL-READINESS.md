# CW-M5-021 — Category Operational Readiness and Controlled Activation Design

## Classification

`CATEGORY_OPERATIONAL_READINESS_SOURCE_VERIFIED`

This milestone is source-only. It made zero database connections and zero
database mutations. It did not inspect, reuse, clean, migrate, activate, or
otherwise contact the previously certified disposable target. Production
migration and category/recovery repository activation remain `NO_GO`.

CW-M5-020's bounded disposable certification remains a separate claim:
`CATEGORY_CONCURRENCY_CRASH_REPLAY_DATABASE_VERIFIED_BOUNDED`. This milestone
does not upgrade that result or claim production certification.

## Registration and readiness architecture

The MySQL development path registers:

1. the provider-neutral category source, extractor, browse service, and
   recovery services;
2. `RejectingTwCategoryMembershipRepository`; and
3. `RejectingTwCategoryRecoveryDurabilityStore`.

The explicit MySQL activation extensions remain separate and are not invoked by
startup. `TwCategoryOperationalReadinessService` is a pure singleton evaluator;
it consumes retained typed evidence and never opens a connection, runs a
readiness probe, applies a migration, changes a registration, or activates a
provider.

The existing `CategoryController` remains anonymous/read-only and GET-only.
No administrative recovery route, write endpoint, hosted worker, scheduler, or
automatic activation path was added.

## Operational state matrix

| State | Meaning | Required evidence / safe behavior |
| --- | --- | --- |
| `DisabledByDefault` | Rejecting adapters are still registered | No browse provider or recovery mutation is inferred from configuration. |
| `AuthorizationMissingOrExpired` | Scoped approval is absent or expired | Deny activation review; do not connect. |
| `SchemaNotCertified` | Target/schema proof is missing or unknown | Keep mutation gated; do not infer readiness from provider code. |
| `ProviderUnavailable` | A required provider is unavailable or unknown | Report unavailable, never an empty category or successful recovery. |
| `ReadOnlyReady` | Category read evidence is complete while mutation/recovery remains gated | Read-only behavior only; no write recommendation. |
| `MutationGated` | Read-only evidence exists but one or more mutation prerequisites remain | Keep rejecting adapters and require more evidence. |
| `RecoveryIndeterminate` | A two-phase outcome is not authoritatively reconciled | Preserve indeterminate status; never mark confirmed commit. |
| `Degraded` | Browse may be available while recovery or another required capability is not | Expose bounded status/reason codes only. |
| `ReadyForOperatorReview` | Technical and authorization evidence is complete | Review-only decision; no automatic activation. |

The snapshot includes a UTC evaluation time, evidence origin, bounded enum
reason codes, whether the evaluation itself avoided a database connection, and
an explicit review-only flag. It contains no endpoint, credential, page ID,
category title, revision identifier, or hidden count.

## Activation and rollback decisions

`TwCategoryOperationalReadinessService.EvaluateActivation` returns
`AllowedForReview` only when all of the following are independently verified:

- valid scoped authorization and target isolation;
- category and recovery schema certification;
- category and recovery provider readiness;
- browse and recovery authorization behavior;
- bounded concurrency/replay evidence;
- rollback plan;
- the current lease assessment; and
- explicit production approval.

The method is pure and `WouldMutate` is always false. Missing, expired,
wrong-target, unavailable, indeterminate, or incomplete evidence returns
`RequiresMoreEvidence` or `Denied`. Test credentials and a healthy provider do
not imply production approval.

Rollback is likewise a decision only:

| Situation | Decision |
| --- | --- |
| No explicit operator rollback request | `ManualReviewRequired` |
| Explicit review after an allowed activation assessment | `RevertToRejectingAfterOperatorApproval` |
| Activation assessment is not allowed | `KeepRejecting` |

No decision mutates DI registrations, configuration, schema, data, services,
containers, or volumes.

## Administrative recovery contract

`TwCategoryRecoveryAdministrativeRequest` distinguishes `Preview` from
`Execute` without creating a route. Preview requests convert only to the
existing `TwCategoryRecoveryPlanRequest` and cannot convert to durable
execution. Execute requests require the existing durable service's fresh
operation and per-page authorization callbacks, authoritative revision
resolution, CAS, audit, and replay boundaries.

The existing recovery service remains the only mutation-capable seam. The
wrapper does not bypass `ReconcileAsync`, invent a checkpoint, expose page
targets in diagnostics, or convert an indeterminate outcome into a commit.

## Observability contract

`TwCategoryOperationalEvent` contains only UTC time, an enum event kind, an
enum outcome (`Attempted`, `ConfirmedCommit`, `Indeterminate`, or `Rejected`),
and a bounded enum reason. It deliberately excludes page/category titles,
revision IDs, operation/scope IDs, credentials, connection strings, and
unrestricted counts. Recovery outcomes remain distinct between attempted,
confirmed, indeterminate, and rejected.

Suggested operator thresholds are documentation-only: alert on repeated
`ProviderUnavailable`, any `RecoveryIndeterminate`, any authorization expiry,
and any unexpected transition away from `DisabledByDefault`. No monitoring
worker or scheduler was added.

## Lease necessity assessment

The current deployment model is a bounded, single-operator/CAS workflow. The
durable operation scope, actor binding, event-key idempotence, checkpoint
generation CAS, and operation-state CAS prevent stale checkpoint advancement
and conflicting audit payloads. They do not prevent duplicate work before a
mutation outcome is durably observed, which is already handled by replay and
authoritative reconciliation.

Lease ownership/expiry is not present in the current schema or contract. A
lease would be justified only for a future multi-worker or concurrent operator
deployment with an explicit owner/expiry protocol, fencing token, renewal,
failure recovery, and live certification. CW-M5-021 does not invent or
implement leases.

## Source-only verification and blockers

Focused readiness tests passed 9/9. They cover default rejection, missing and
expired authorization, provider/schema gaps, indeterminate recovery,
preview-versus-execute separation, review-only rollback, and secret/page
metadata exclusion from operational events.

The existing browse and recovery test suites continue to cover invalid,
unavailable, unindexed, indexed-empty, populated, inconsistent, redacted,
canceled, partial, and fresh-revision behavior. No live database or container
test was run for this milestone by design.

Remaining independent gates are production authorization, deployed schema and
provider activation evidence, operational authentication for any future
administrative endpoint, multi-worker lease requirements if deployment
changes, and the previously documented untested OS/container/MariaDB crash
and live mid-transaction fault scenarios.
