# CW-M5-022 — Administrative Category Recovery API and Authorization Integration

## Classification and boundary

`CATEGORY_ADMIN_RECOVERY_API_SOURCE_VERIFIED`

This milestone is source-only. It made zero database connections and zero
database mutations. It did not apply schemas, activate a category provider,
reuse the operator4 authorization, run recovery, modify production services,
or change Lua, Cargo, worker, container, or volume state.

Production migration and category/recovery repository activation remain
`NO_GO`.

## Existing identity and authorization boundary

The API reuses the existing ASP.NET Core `[Authorize]` convention and the
hydrated `TwSessionState`. Administrator status is obtained from the existing
`ITwApplicationIdentityRepository.IsUserAdministratorAsync` path; the API does
not introduce an `IsAdmin` claim, role shortcut, or caller-supplied actor ID.
The actor passed to recovery is the server-derived profile user ID.

Administrator authentication is not operation authorization. Because the
repository has no existing category-recovery operation approval contract, the
new `ITwCategoryAdministrativeAuthorization` adapter is fail-closed and the
registered default rejects every preview and execute operation. A future
explicit adapter must authorize the exact operation, trusted actor, and
normalized target scope at request time.

Page authorization is delegated to the existing session permission evaluator
using the authoritative page navigation and `TwPermission.Edit`. The existing
recovery services resolve the current revision and invoke that callback again
for execution.

## Gate and routes

The configuration key
`ContinuumWiki:CategoryRecovery:AdministrativeApi:Enabled` must parse as
`true`. Missing, malformed, or false configuration is disabled. The MySQL
development runtime registers the API, evidence source, and rejecting
operation-authorizer. The default SQLite controller dependency is optional and
returns a fail-closed 404 when the API is not registered.

Routes are:

| Method and route | Contract | Behavior |
| --- | --- | --- |
| `GET /Admin/CategoryRecovery/readiness` | authenticated administrator | returns only the redacted typed readiness snapshot |
| `POST /Admin/CategoryRecovery/preview` | authenticated administrator plus preview authorization | invokes existing read-only `PlanAsync` |
| `POST /Admin/CategoryRecovery/execute` | authenticated administrator plus fresh execute authorization and allowed readiness | invokes existing durable recovery service |

Preview and execute require `ValidateAntiForgeryToken`, matching the existing
cookie-authenticated MVC mutation convention. Anonymous requests are handled
by `[Authorize]`; ordinary authenticated users receive `Forbid`; disabled
routes return `404` without invoking work or exposing target details.

The request body contains only an optional server-validated operation ID and
bounded recovery targets (`PageId` and optional expected navigation). It has no
actor, revision, database target, approval verdict, or authorization field.
Execute requires a nonempty operation ID so the existing durable replay and
scope/actor conflict rules remain usable. The durable service still owns audit
budget enforcement, checkpoints, CAS, replay, and indeterminate outcomes.

## Readiness, preview, and execution behavior

The default evidence source reports rejecting providers, source-only evidence,
and no database contact. Consequently, default execution returns
`MutationGated` before durable work. The source deliberately does not probe a
provider or infer readiness from connectivity.

Preview is side-effect-free and calls only `ITwCategoryRecoveryService.PlanAsync`.
It cannot create an operation, append an audit event, advance a checkpoint, or
invoke `ReconcileAsync`. Its typed result preserves invalid input, provider
unavailability, incomplete expansion, revision conflict, inconsistent index,
unsupported behavior, unauthorized targets, and cancellation.

Execution requires an `AllowedForReview` readiness decision, then constructs a
server-owned administrative request and delegates to
`ITwCategoryDurableRecoveryService`. That service performs fresh operation and
per-page authorization, authoritative revision resolution, CAS, audit, replay,
and recoverable two-phase handling. The API maps completed, partial,
indeterminate, conflict, cancellation, provider, authorization, and persistence
outcomes without converting failure into success or an empty result.

No direct SQL, repository write, checkpoint write, background worker, retry,
lease, cross-store transaction, or automatic activation was added.

## Redaction and observability

Unauthorized or disabled responses have no plan, execution entries, page
navigation, revision, membership count, or readiness evidence. Successful
admin responses use the existing authorized recovery projection. The existing
bounded operational event contract remains the only generic readiness
telemetry; it contains only UTC time, enum kind, enum outcome, and enum reason.
No connection string, credential, raw Wikitext, hidden title, or unrestricted
count is added.

## Source-only verification

Focused API tests passed 15/15. They cover the missing gate, anonymous and
ordinary-user rejection, authentication-versus-operation authorization,
default readiness gating, forged request-field absence, preview nonmutation,
redacted unauthorized results, revision/incomplete/inconsistent outcomes,
batch 101 rejection, durable delegation, partial/cancellation outcomes, and
anti-forgery route attributes.

The full Debug and Release results are recorded in the companion validation
report. No live database or HTTP mutation endpoint was activated or invoked.

## Remaining independent gates

- A separately authorized, explicitly registered operation-authorization
  adapter is required before preview or execution can be used.
- Retained non-source readiness evidence and an explicitly activated provider
  are required before execution can pass the mutation gate.
- No production deployment, production authorization, MariaDB certification,
  crash certification, or host-worker operation was performed here.

