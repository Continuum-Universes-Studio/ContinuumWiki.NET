# CW-M5-024 — Durable Administrative Grants and Atomic Authorization Consumption

## Classification

`CATEGORY_ADMIN_GRANT_DURABILITY_SOURCE_VERIFIED`

This milestone is source-only. It made no database connections, applied no
migrations, activated no provider, and did not reuse the M5-019 operator4
authorization. Production migration and administrative repository activation
remain `NO_GO`.

## Durable contract

`ITwCategoryAdministrativeGrantStore` is a separate persistence boundary for
administrative grants. A grant record contains immutable grant/approval and
issuer evidence references, server-bound actor identity, explicit preview or
execute operation, the existing canonical recovery scope fingerprint and
policy version, UTC validity, replay mode, version, revocation metadata, and
consumption metadata.

The definition-only `V3__CategoryAdministrativeGrants.sql` migration creates
`TwCategoryAdministrativeGrant` and
`TwCategoryAdministrativeGrantAudit`. The initializer and readiness proof are
explicit and are never called by application startup. The opt-in activation
method requires MySQL, `Database:CategoryAdministrativeGrants:Enabled=true`,
schema version `3`, a validated target-bound readiness proof, and an existing
connection-factory registration.

The default graph remains rejecting. No grant store or positive grant provider
is registered by default, and the administrative API remains disabled by
configuration.

## Atomic state transitions

`MySqlCategoryAdministrativeGrantStore.TryConsumeAsync` opens a transaction,
locks the grant row with `FOR UPDATE`, revalidates version, state, actor,
operation, exact scope, UTC validity, and single-use mode, then advances the
version and inserts the redacted `Consumed` audit event before committing. A
stale, revoked, expired, consumed, mismatched, or malformed grant fails
closed. The grant row and its transition audit are atomic within the grant
database.

`RevokeAsync` uses the same serialization rule. If revocation commits first,
consumption returns `Revoked`; if consumption commits first, later revocation
returns `GrantAlreadyConsumed`. Revocation never retroactively undoes a
committed recovery. The database lock order establishes the race winner.

Execution authorization consumes a single-use grant before the existing
durable recovery coordinator is called. A process interruption after consume
and before recovery completion leaves the grant consumed; it is not silently
reissued. The API records a separate execution outcome when the process
returns. If that outcome cannot be recorded, the caller receives
`RecoveryIndeterminate`. This is not exactly-once end-to-end recovery and does
not claim atomicity across the grant database and category recovery stores.

Preview never consumes an execute grant. A retry with the same single-use
grant returns `GrantReplayed` and cannot start another execution. Outcome
events are keyed by the operation correlation ID and are idempotent; audit
growth is bounded at 128 events per grant.

## Evidence and activation gates

The durable provider is only available through the explicit application-layer
activation method. It requires an independently authorized target-bound schema
proof. Source-only, disposable, and operator4 readiness evidence cannot be
promoted to production readiness. No grant issuance endpoint, public mutation
route, scheduler, worker, production migration, or automatic activation was
added.

## Verification

Source-only tests model separate competing consumers, serialized revoke versus
consume, reconstructed-store persistence, idempotent execution outcome audit,
preview non-consumption, missing correlation, malformed and mismatched grants,
and the existing administrative API security contract. The real MariaDB tests
are authorization-gated and skip before opening a connection. If separately
authorized later, they use separate connections, retain diagnostic rows, and
do not delete schemas, containers, or volumes.

## Remaining gates

- independent M5-024 operator authorization and target-bound readiness proof;
- real MariaDB V3 schema and separate-connection certification;
- restart and acknowledgment-loss testing beyond the modeled reconstruction
  test;
- production-bound readiness evidence and explicit activation approval.

No OS/server crash certification is claimed. No atomicity between grant
consumption and category mutation is claimed.
