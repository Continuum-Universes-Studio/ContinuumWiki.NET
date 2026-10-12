# CW-M5-020 — MariaDB Concurrency and Crash-Replay Certification

## Classification

`CATEGORY_CONCURRENCY_CRASH_REPLAY_DATABASE_VERIFIED_BOUNDED`

The bounded live certification passed against the explicitly authorized,
disposable `operator4` MariaDB target. The result does not certify a process,
container, or MariaDB-server crash, and it does not claim atomicity across the
category-membership transaction and the separate recovery-durability store.

Production migration and repository activation remain `NO_GO`. The rejecting
category repository and rejecting recovery store remain the defaults. No Lua,
Cargo, worker, systemd, or production configuration was changed.

## Authorization and target boundary

The live run used the existing v2 authorization manifest and nonconnecting
preflight:

```bash
scripts/preflight-m5-019.sh \
  --manifest "$PWD/secure/evidence/cw-m5-019-authorization.json" \
  --report "$PWD/secure/evidence/cw-m5-020-preflight.json"
```

The preflight classified the source as verified with zero database connections
and zero database mutations. The live test gate then revalidated the exact
loopback endpoint, database, application identity, target identity, manifest
version, approval ID, expiry, and required `Connect`, `CategoryMutations`,
`ConcurrencyAndCrashTests`, and `RetainSanitizedEvidence` permissions.

The target was `continuumwiki-m5-019-operator4`, database
`cw_m5_019_operator4`, endpoint `127.0.0.1:13321`, application identity
`continuumwiki_m5_019_app`, MariaDB `11.8.9`. The credential remained in the
owner-only operator environment file and was not written to this report.

## Implemented coordinator correction

`TwCategoryDurableRecoveryService` now has an optional, unregistered
`ITwCategoryRecoveryFaultInjector` seam. Its four boundaries are:

1. before category mutation;
2. after the category service returns and before the outcome audit;
3. after the durable outcome audit and before checkpoint CAS; and
4. after checkpoint commit and before caller acknowledgement.

The seam is application-layer simulation only. It cannot kill an OS process,
container, or database server. A fresh coordinator now resumes an operation
that was durably left in `Running` rather than attempting an invalid
`Running -> Running` transition. A checkpoint is still advanced only after a
recorded terminal outcome and successful generation CAS.

## Evidence-backed coverage

The live class
`MediaWikiCategoryConcurrencyCrashReplayAuthorizationTests` uses separate
unpooled physical connection factories, bounded barriers, unique retained
identifiers, and no cleanup:

| Case | Live result |
| --- | --- |
| Concurrent category replacement has no torn membership/index state | passed |
| Stale revision rejected after an earlier authoritative read | passed |
| Concurrent checkpoint CAS has one winner and durable read-back | passed |
| Concurrent identical audit append is idempotent; conflicting payload fails closed | passed |
| Application interruption after checkpoint commit resumes from MariaDB evidence | passed |

The focused source suite covers all four interruption boundaries, replay,
redaction, authorization, event-key conflicts, checkpoint ordering, terminal
state protection, and the rejecting default. It passed 16 tests.

The following remain explicitly unexecuted in this bounded milestone:

- an actual OS-process, container, or MariaDB-server crash;
- lease-owner or lease-expiry races, because the current contract has no lease;
- a live injected SQL error inside the category mutation transaction;
- live 4,096-event budget exhaustion (the fail-closed budget remains covered
  by source/provider tests); and
- any cross-store atomicity claim.

Because cleanup is not authorized, unique test rows and recovery records remain
in the disposable database. No schema, container, or volume deletion was
attempted.

## Reproduction

After independently checking the current manifest and preflight, the operator
may run the scoped live class with credentials supplied only through the
owner-only environment file. The shell must not enable command tracing:

```bash
set -Eeuo pipefail
set -a
. /home/cdjensen94/.local/state/continuumwiki/cw-m5-019/operator4/application.env
set +a
connection="Server=127.0.0.1;Port=13321;Database=cw_m5_019_operator4;User ID=continuumwiki_m5_019_app;Password=${CW_M5_APP_PASSWORD};Allow User Variables=True;"
CONTINUUMWIKI_RUN_MYSQL_TESTS=1 \
CONTINUUMWIKI_M5_019_AUTHORIZED=1 \
CONTINUUMWIKI_MYSQL_TEST_CONNECTION="$connection" \
CONTINUUMWIKI_M5_019_TARGET_DATABASE=cw_m5_019_operator4 \
CONTINUUMWIKI_M5_019_TARGET_IDENTITY='container=continuumwiki-m5-019-operator4;database=cw_m5_019_operator4;endpoint=127.0.0.1:13321;volume=continuumwiki-m5-019-operator4-data;image=docker.io/library/mariadb@sha256:6422478cb8e159f080fb1d8ccf65101e26fe51385787fde7d16c3b165a331f15' \
CONTINUUMWIKI_M5_019_APPROVAL_PATH="$PWD/secure/evidence/cw-m5-019-authorization.json" \
dotnet test TightWiki.Tests/TightWiki.Tests.csproj -c Debug --no-restore -m:1 \
  --filter 'FullyQualifiedName~MediaWikiCategoryConcurrencyCrashReplayAuthorizationTests'
```

The command does not apply migrations and does not clean the target.
