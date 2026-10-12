# CW-M5-018 — Durable MariaDB Recovery Provider

## Classification

`CATEGORY_RECOVERY_MARIADB_SOURCE_IMPLEMENTED_AUTHORIZATION_BLOCKED`

This milestone prepares, but does not activate, a MariaDB durability provider for
the recoverable category-recovery protocol. Production migration and activation
remain `NO_GO`. No database connection, schema mutation, category mutation, Lua
activation, worker change, or production service change was performed while this
source implementation was prepared.

## Scope and activation boundary

The provider implements the existing `ITwCategoryRecoveryDurabilityStore`
contract. It is registered only by the explicit
`AddAuthorizedMySqlCategoryRecoveryDurability` extension after all of these
conditions are supplied by an operator:

1. `Database:Provider=mysql`.
2. `Database:CategoryRecoveryDurability:Enabled=true` and schema version `2`.
3. An existing `IDatabaseConnectionFactory` registration.
4. A read-only readiness proof from
   `MySqlCategoryRecoveryDurabilityReadiness.ValidateAsync`.
5. An independently approved disposable MariaDB 11.8 target and cleanup scope.

The normal `Program.cs` and `MySqlDevelopmentRuntime` graphs do not call the
extension. The existing rejecting category repository and rejecting durability
store therefore remain the defaults. A configuration value, a connection
string, or a successful build is not authorization.

## Durable schema

`TightWiki.Repository/Scripts/MySql/Category/V2__CategoryRecoveryDurability.sql`
is a definition-only migration. It must follow the existing category V1 schema
because it records version `category-recovery = 2` in
`MySqlCategorySchemaVersion`.

`TwCategoryRecoveryOperation` stores the immutable operation identity, canonical
scope JSON and fingerprint, actor binding, policy/schema version, operation
state, checkpoint cursor, checkpoint generation, last recorded outcome sequence,
last audit sequence, safe terminal diagnostic code, and timestamps.

`TwCategoryRecoveryAuditEvent` is separate from checkpoint state. Its composite
operation/sequence primary key, globally unique event ID, and per-operation
idempotent event key enforce deterministic append ordering and replay
deduplication. Appends fail closed at the existing bounded limit of 4096 audit
events per operation, while the recovery scope remains bounded to 100 pages.
Event fields are typed string representations of the existing
enums; diagnostics and terminal reasons accept only bounded safe tokens, never
free-form exception text.

The current contracts contain no lease owner or lease-expiry fields. No lease
semantics were invented. Live lease-race certification is therefore an explicit
unresolved gate, not a claimed capability.

## Store-local atomicity and replay

Each provider method owns one connection and one transaction where a
read/compare/write sequence is needed:

| Operation | Store-local guarantee |
| --- | --- |
| Create/replay | Primary-key idempotence, scope/actor/policy conflict detection, immutable operation row read-back |
| Append audit | Operation row lock, monotonic audit sequence, unique event-key replay, actor binding |
| Advance checkpoint | Recorded terminal outcome required, scope/generation/outcome CAS in the `UPDATE` predicate, no backward cursor |
| Transition | Valid state-machine transition plus status/generation CAS |
| Read progress | Scope/actor match and latest-outcome aggregation with no page identities in the projection |

An outcome is appended before the checkpoint CAS. If the CAS loses a race, the
operation remains recoverable and the caller must reload authoritative state.
The provider never mutates `PageCategoryMembership` and never advances a
checkpoint for an attempted, failed, incomplete, unauthorized, or indeterminate
outcome.

This is not a distributed transaction: category membership mutation remains in
the existing `MySqlCategoryMembershipRepository` transaction, while the
durability store uses its own transaction. A crash after category mutation and
before audit persistence remains an indeterminate state requiring the existing
`ReconcileAsync` path on replay. Passing store tests must not be described as
cross-resource atomicity.

## Certification runbook (operator-only)

Do not run these commands against production. The operator must first obtain an
approved M5-018 scope file, exact disposable target identity, MariaDB 11.8
proof, and a least-privilege application connection. Keep the password in the
operator's environment or secret mechanism; do not place it in a report or
command transcript.

The ordinary gated test command is:

```bash
CONTINUUMWIKI_RUN_MYSQL_TESTS=1 \
CONTINUUMWIKI_M5_018_AUTHORIZED=1 \
CONTINUUMWIKI_MYSQL_TEST_CONNECTION='(operator-supplied disposable connection)' \
CONTINUUMWIKI_M5_018_TARGET_DATABASE='cw_m5_018_(approved-suffix)' \
CONTINUUMWIKI_M5_018_TARGET_IDENTITY='(exact approved container/database identity)' \
CONTINUUMWIKI_M5_018_APPROVAL_PATH='/path/to/approved-scope.json' \
dotnet test TightWiki.Tests/TightWiki.Tests.csproj -c Debug --no-restore -m:1 \
  --filter 'FullyQualifiedName~MediaWikiCategoryRecoveryDurabilityAuthorizationTests'
```

The test gate verifies loopback, an unprivileged mapped port, the exact
`cw_m5_018_` database, the dedicated `continuumwiki_m5_018_app` identity, the
approval record, and MariaDB `11.8.*` before opening the application store.
It initializes the existing disposable base schema plus the approved target's
category and recovery schemas, executes the store-local round trip/CAS/replay
assertions, and deletes the test operation and audit rows in a `finally` block.
The operator must retain
sanitized evidence of target identity, engine version, test result, and cleanup;
the application test does not drop a database it does not own.

The live test currently skips because no M5-018 authorization was supplied.
The `M5HostCertificationGate` is reused through its scope-specific evaluator;
the pre-existing M5-003 gate behavior remains unchanged.

## Crash and concurrency matrix

The source protocol and unit coverage model these boundaries:

- before mutation: no terminal outcome and no checkpoint advance;
- after mutation before audit: authoritative re-resolution is required;
- after audit before checkpoint: replay sees the idempotent event and retries
  the checkpoint CAS;
- during checkpoint CAS: affected-row checking returns a generation conflict;
- after checkpoint commit before acknowledgement: reload returns the committed
  cursor and does not treat the request as a new scope;
- repeated operation, event, or scope submissions: replay or explicit conflict.

Real database certification still needs concurrent CAS races, transaction
rollback/failure injection, permission/visibility boundaries, and interruption
tests. There is no lease race test because the current contract has no lease.

## Required evidence before any stronger verdict

The following remain open:

- approved disposable MariaDB 11.8 identity and mutation/cleanup scope;
- schema deployment and read-back on that target;
- real provider transaction, CAS, replay, and cancellation evidence;
- real category membership reconciliation/revision-conflict evidence;
- crash-boundary and concurrent-writer evidence;
- authorized page-visibility and redaction evidence;
- cleanup evidence and retained sanitized report.

No production migration or activation is authorized by this document.
