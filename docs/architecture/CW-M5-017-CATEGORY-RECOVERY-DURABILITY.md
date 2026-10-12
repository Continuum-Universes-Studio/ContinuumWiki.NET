# CW-M5-017 — Durable Recovery Contracts and Audit Integrity

## Classification and boundary

`CATEGORY_RECOVERY_DURABILITY_SOURCE_VERIFIED` is the strongest source-only
classification supported here. The typed contracts, state machine, replay
coordinator, fail-closed adapter, and synthetic fault-injection tests are
implemented. This does not certify MariaDB durability, category CAS, crash
consistency, or production operation.

Database authorization remains `DATABASE_AUTHORIZATION_NOT_ESTABLISHED` and
production migration/activation remains `NO_GO`. No database was connected to,
provisioned, inspected through a live connection, migrated, or mutated.
`RejectingTwCategoryMembershipRepository` and
`RejectingTwCategoryRecoveryDurabilityStore` remain the startup defaults.

## Inventory and transaction decision

The existing recovery service owns bounded ordering, current-page resolution,
per-page authorization, incomplete-expansion protection, and delegation to
`ITwCategoryMembershipApplicationService.ReconcileAsync`. The category
repository owns the page lock, authoritative revision check, membership
replacement, index-state update, and its own transaction. The existing generic
logging repository is a SQLite-shaped application log, not a typed recovery
operation journal, and it cannot participate in the category repository's
transaction.

Therefore this milestone does not add a pretend shared transaction or new SQL
tables. The selected design is a recoverable, at-least-once two-phase workflow:

1. Create or replay an immutable operation and scope.
2. Record intent, authorization/revision observations, and reconciliation
   attempt events through the durability store.
3. Invoke the existing reconciliation/CAS pathway.
4. Append the outcome event. `Reindexed` is marked `VerifiedCommit` only when
   the existing reconciliation contract reports its committed result;
   no-change, conflicts, incomplete extraction, unavailable, cancellation,
   and failures retain non-commit confidence.
5. Advance the checkpoint conditionally only after the outcome event is
   recorded. A generation and last-outcome sequence prevent stale or competing
   advancement.

If the process crashes after intent, after reconciliation, or after outcome but
before checkpoint advancement, the position remains recoverable and is replayed
through the idempotent reconciliation pathway. A checkpoint never skips an
uncertain page. Duplicate event keys are idempotent; conflicting payloads are
rejected. This is a source-level replay contract, not evidence of a durable
commit until an authorized store and live transaction behavior are certified.

## Typed contracts

`TwCategoryRecoveryDurabilityModels.cs` defines:

* immutable operation identity, actor provenance, policy version, normalized
  bounded scope, and SHA-256 scope fingerprint;
* `Planned`, `Running`, `Interrupted`, `Recoverable`, `Completed`,
  `PartiallyCompleted`, `Failed`, and `Cancelled` operation states with legal
  transitions and terminal-state protection;
* deterministic page work identity, attempt identity, observed revision,
  typed outcome, and commit confidence;
* versioned checkpoints containing operation ID, scope fingerprint, committed
  position, generation, and last durable outcome sequence;
* immutable audit event kinds for intent, authorization, revision resolution,
  attempt, verified commit, no-change, conflict, incomplete expansion,
  unavailable, failure, cancellation, and resume;
* typed idempotent-create, append, conditional-advance, transition, progress,
  scope-conflict, generation-conflict, incompatible-version, and unavailable
  results.

`ITwCategoryRecoveryDurabilityStore` deliberately has no unchecked `SaveAsync`
or direct category-row write. A checkpoint advance requires the matching scope,
expected generation/position/sequence, a forward bounded position, a supported
schema version, and a recorded outcome event key/sequence. The progress
projection contains only status, checkpoint and typed aggregate outcomes; it
does not expose page identities or private failure metadata.

## Orchestration and authorization

`ITwCategoryDurableRecoveryService` is separate from the existing volatile
source-only service. A caller must provide a stable operation ID, explicit
bounded targets, operation authorization, and page authorization. Durable mode
does not fall back to volatile execution; the existing service explicitly
rejects `DurableExecutionRequested` and the durable coordinator returns typed
unavailable results when its store is rejecting.

On every resume, the coordinator revalidates operation authorization, scope,
checkpoint version/generation, page authorization, current revision, and
repository readiness through the existing service. Hidden or unauthorized page
details are redacted from returned results and progress. No admin route,
hosted service, queue, timer, retry loop, or automatic activation was added.

## Threat table

| Threat | Source control |
| --- | --- |
| Crash before intent | Position remains unchanged; replay creates the intent. |
| Crash after intent/before reconciliation | Position remains unchanged; replay is explicit and bounded. |
| Crash after reconciliation/before outcome | No checkpoint advancement; reconciliation is replayed idempotently. |
| Crash after outcome/before checkpoint | Stable outcome key replays idempotently, then conditional advance retries. |
| Duplicate delivery | Immutable event keys and existing reconciliation idempotence. |
| Competing operator/worker | Operation generation and checkpoint CAS reject stale advancement. |
| Stale revision or edit/delete/restore race | Current revision is re-resolved and existing repository CAS decides. |
| Forged checkpoint or changed scope | Version, operation ID, fingerprint, generation and position are validated. |
| Cancellation or uncertain work | Page is not advanced unless a safe outcome was recorded; interrupted/recoverable state is returned. |
| Authorization revocation | Operation and page callbacks run again on resume; denied details are redacted. |
| Incomplete expansion | Existing reconciliation rejects replacement and checkpoint does not advance. |
| Private audit leakage | Scoped progress has no page identity or failure-detail fields. |

## Verification and remaining gates

The source-only durability suite covers idempotent operation replay and scope
conflict, illegal transitions, duplicate event keys, future checkpoint versions,
missing outcomes, backwards checkpoints, outcome-before-checkpoint ordering,
crash-boundary replay, authorization revocation, pre-cancellation, and the
rejecting-store fail-closed path. These tests use deterministic in-memory
doubles and fault injection; they do not prove MariaDB behavior.

No category persistence provider or migration was added. A future provider must
define an additive schema/versioning strategy, retention policy, authorization
and redacted progress projection, and a tested transaction/replay recovery
procedure before activation. Until then, durable execution remains unavailable
and production remains `NO_GO`.
