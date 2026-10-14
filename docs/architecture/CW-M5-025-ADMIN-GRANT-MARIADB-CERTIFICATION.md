# CW-M5-025 — Administrative Grant MariaDB Certification and Restart Recovery

## Current result

The explicitly authorized disposable target was certified successfully on
2026-10-12. The focused live suite completed with 8 passed, 0 failed, and 0
skipped tests after applying the existing base schema before category V1. The
target is retained because the authorization explicitly prohibits schema,
database, container, and volume cleanup. This is disposable-target evidence;
production migration and repository activation remain `NO_GO`.

## Classification

`CATEGORY_ADMIN_GRANT_MARIADB_CERTIFICATION_VERIFIED_DISPOSABLE_TARGET`

The source implementation and gated certification suite were executed only
after the target-specific authorization manifest and nonconnecting preflight
passed. The run used the new M5-025 target, not the M5-019/020 operator4
authority. Production migration and repository activation remain `NO_GO`.

## Independent authorization boundary

The operator handoff is recorded separately in
`docs/operations/CW-M5-025B-OPERATOR-AUTHORIZATION-WORKSHEET.md` and
`artifacts/validation/mediawiki-category-admin-grant-authorization-handoff-m5-025b-v1.json`.
The non-authorizing draft explicitly records that the existing M5-019
operator4 target is schema-incompatible with M5-025; it is not an approval or
a target substitution.

CW-M5-025 uses a separate `cw-m5-025-authorization-v1` contract. The existing
M5-019/020 manifests and the M5-024 gate are not accepted. The authorization
must identify the disposable container, volume, database, loopback endpoint,
MariaDB 11.8.x engine, dedicated application identity, approver, operator,
approval validity window, and exact nonsecret instance identity.

Required true permissions are:

- connect and verify target isolation;
- apply the V1, V2, and V3 prerequisite/schema operations;
- issue test grants and perform grant consume, revoke, and audit mutations;
- run separate-connection concurrency, restart-recovery, and
  acknowledgment-loss scenarios; and
- retain sanitized evidence.

`DatabaseCleanup`, `SchemaDeletion`, and `ContainerVolumeDeletion` were
explicitly false. The authoritative operator manifest is retained at
`secure/evidence/cw-m5-025-authorization.json` with owner-only permissions;
its connection credentials are not present in the manifest or reports.
The schema is documented at
`docs/operations/cw-m5-025-authorization-manifest.schema.json`; it does not
constitute approval.

The nonconnecting preflight is:

```bash
scripts/preflight-m5-025.sh \
  --manifest /private/operator/path/cw-m5-025-authorization.json \
  --report secure/evidence/cw-m5-025-preflight.json
```

It reads only the manifest and writes a redacted report. It must succeed
before the authorized test command is allowed to open a connection. The live
test gate separately compares the operator-provided connection and target
environment to the manifest before the first connection.

## Authorized operator command

After independently creating and validating the manifest, and only with the
connection string supplied through a protected operator environment variable,
the exact certification command is:

```bash
CONTINUUMWIKI_RUN_MYSQL_TESTS=1 \
CONTINUUMWIKI_M5_025_AUTHORIZED=1 \
CONTINUUMWIKI_M5_025_AUTHORIZATION_PATH=/private/operator/path/cw-m5-025-authorization.json \
CONTINUUMWIKI_M5_025_TEST_CONNECTION="$CONTINUUMWIKI_M5_025_TEST_CONNECTION" \
CONTINUUMWIKI_M5_025_TARGET_CONTAINER="$CONTINUUMWIKI_M5_025_TARGET_CONTAINER" \
CONTINUUMWIKI_M5_025_TARGET_VOLUME="$CONTINUUMWIKI_M5_025_TARGET_VOLUME" \
CONTINUUMWIKI_M5_025_TARGET_DATABASE="$CONTINUUMWIKI_M5_025_TARGET_DATABASE" \
CONTINUUMWIKI_M5_025_TARGET_ENDPOINT="$CONTINUUMWIKI_M5_025_TARGET_ENDPOINT" \
CONTINUUMWIKI_M5_025_TARGET_IDENTITY="$CONTINUUMWIKI_M5_025_TARGET_IDENTITY" \
dotnet test TightWiki.Tests/TightWiki.Tests.csproj -c Debug --no-restore -m:1 \
  --filter 'FullyQualifiedName~MediaWikiCategoryAdministrativeGrantMariaDbCertificationTests'
```

The command must not be run against the prior operator4 target unless a new
M5-025 approval explicitly identifies that exact target and all V3/test
operations. The tests never delete rows, schemas, containers, or volumes.

## Live certification matrix

The gated tests cover:

1. V1/V2 prerequisite and V3 migration read-back, InnoDB tables, primary and
   unique audit keys, and separate-connection single-use consumption.
2. Expired and not-yet-valid grants revalidated at transaction time.
3. Actor, action, exact-scope, and forged-identifier rejection.
4. Revoke-versus-consume serialization with one legal database-ordered winner.
5. Issued/rejected/revoked/consumed/outcome audit idempotence and conflict
   behavior.
6. Recreated store/service state rejecting replay of consumed grants.
7. Application acknowledgment-loss simulation after committed consumption;
   authoritative read-back reports consumed/indeterminate and does not permit
   a second consume.
8. Forced failure before transition audit commit, verifying grant state and
   transition audit roll back together.
9. Grant consumption remaining distinct from a later recovery outcome record.

The acknowledgment-loss case is an application-level response-loss
simulation, not an OS, container, or MariaDB crash. No process-kill test is
included. Cross-store exactly-once recovery is not claimed.

## Evidence and cleanup

The live report must contain sanitized target fingerprint, MariaDB version,
schema read-back, per-case pass/fail/skip, migration status, actual connection
and mutation accounting, and residual-row status. Since cleanup is explicitly
false in the required authorization, diagnostic rows remain and must be
reported. No secrets or connection strings belong in the manifest, report, or
repository.

## Recorded verification

The nonconnecting authorization preflight passed before the first live
connection. The live suite applied the base Page prerequisite, category V1,
recovery V2, and administrative-grant V3. It then passed schema read-back,
separate-connection CAS/concurrency, validity and binding rejection, revoke
versus consume serialization, audit idempotence/conflict, recreated-store
replay rejection, application acknowledgment-loss handling, transactional
rollback before transition audit, and separate recovery-outcome recording.

The first live attempt exposed a real setup defect: category V1 referenced
`Page` before the base schema was applied. A subsequent audit replay failure
was traced to the audit-row DTO declaring `ActorUserId` as `string` although
the connector materialized the CHAR(36) value as `Guid`. Both corrections are
covered by the passing focused and full regression runs. Evidence is recorded
in `artifacts/validation/mediawiki-category-admin-grant-mariadb-certification-v1.json`.
