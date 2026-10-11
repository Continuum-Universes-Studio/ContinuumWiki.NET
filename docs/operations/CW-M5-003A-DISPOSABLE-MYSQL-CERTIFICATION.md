# CW-M5-003A/B — Disposable MySQL TemplateStyles Host Certification

## Current status

`HOST_AUTHORIZATION_BLOCKED`

CW-M5-003A prepared the real-cookie, persisted-Identity, MySQL-repository, and
TemplateStyles authorization test, but no disposable database was started or
contacted. CW-M5-003B hardens that procedure and adds an approval-file gate;
the current environment still contains no approved target or authorization
record, so host certification remains unexecuted.

Historical M5-003 evidence remains at
`artifacts/validation/mediawiki-templatestyles-host-authorization-v1.json`.
The M5-003A execution record remains at
`artifacts/validation/mediawiki-templatestyles-host-authorization-m5-003a-v1.json`.
The M5-003B report is
`artifacts/validation/mediawiki-templatestyles-host-authorization-m5-003b-v1.json`.

## Provider boundary

The repository uses `MySqlConnector` 2.6.2 for Dapper/repository connections
and `MySql.EntityFrameworkCore` 10.0.9 for persisted ASP.NET Core Identity.
CW-M5-003B selects Oracle MySQL 8.4 for this provider-level host test.

This does not alter the production readiness contract: `MySqlRuntimeReadiness`
still requires MariaDB 11.8. A successful M5-003B run means only that the
persisted Identity/repository and TemplateStyles authorization path passed
against the verified disposable MySQL 8.4 target. It is not MariaDB 11.8
application-readiness certification.

Lua, Cargo, production configuration, systemd, bubblewrap, and SELinux are
outside this milestone and remain unchanged.

## Why the lifecycle changed

The former M5-003A procedure opened a host-published connection as MySQL
`root`, created a wildcard schema grant, and let the test create and drop a
unique child database. MySQL database-level grants do not safely constrain
arbitrary `CREATE DATABASE` or `DROP DATABASE` operations by a name prefix.
Application-side prefix checks are not a database privilege boundary.

M5-003B therefore uses the narrowest verifiable arrangement:

1. Rootless Podman starts one fresh, uniquely labelled container.
2. The published port is bound only to `127.0.0.1`.
3. MySQL root exists only for the container's private Unix-socket setup path;
   no `MYSQL_ROOT_HOST` is configured and no host-side root connection is
   created.
4. The container-local setup creates one exact schema,
   `cw_m5_003_certification`, and one application identity.
5. The application identity receives only the required DDL/DML privileges on
   that exact schema, with no global privilege and no grant option.
6. The test uses the pre-created exact schema and never issues `CREATE
   DATABASE` or `DROP DATABASE`.
7. The operator script destroys only the container carrying its own run label.

This avoids claiming that a broadly privileged host lifecycle account is safe.
Container-local root is an implementation detail of the disposable database
bootstrap and is never a credential accepted by the application test gate.

## Approval contract

The test and operator script require all of the following:

- `CONTINUUMWIKI_RUN_MYSQL_TESTS=1`;
- `CONTINUUMWIKI_M5_003_HOST_AUTHORIZED=1`;
- a loopback, high-port application connection to the exact approved
  `cw_m5_003_...` schema using `continuumwiki_m5_003_app`;
- `CONTINUUMWIKI_M5_003_TARGET_DATABASE` and
  `CONTINUUMWIKI_M5_003_TARGET_IDENTITY` matching the connection and approval;
- `CONTINUUMWIKI_M5_003_APPROVAL_PATH` pointing to an approved
  `cw-m4-005-operator-approval-v1` Track B record.

The approval must explicitly authorize each required mutation in
`RequestedScope.MutationScope`: disposable database creation, synthetic
Identity records, synthetic wiki content, synthetic permission mutations,
disposable database deletion, container creation/removal, and sanitized
evidence retention. It must also set `RequestedScope.ReadOnly=false`, identify
the approver and time, record the exact target identity and retention/cleanup
policy, and keep production MariaDB, production Lua, systemd/bubblewrap
changes, worker deployment, and credential material excluded. The marker is an
execution control, not authorization by itself. No approval record is
fabricated by this repository change.

## Hardened operator procedure

The implementation is `scripts/run-m5-003b-certification.sh`. Without
`--execute` it performs only a dry run. The execute path refuses mutable image
tags, production-like database names, reused containers, invalid approval
scope, non-loopback publication, non-MySQL-8.4 runtime identity, remotely
reachable root entries, and global/grantable application privileges.

The image must be an operator-verified immutable Oracle MySQL 8.4 digest. The
operator must fill the digest and approval record; do not copy credentials into
the repository, report, or shell command text:

```bash
mysql_image="${CW_M5_003B_MYSQL_IMAGE:?set to the operator-verified immutable docker.io/library/mysql@sha256:<64 hex> reference}"
scripts/run-m5-003b-certification.sh \
  --execute \
  --approval "$HOME/continuumwiki-validation/approvals/cw-m5-003b.json" \
  --image "$mysql_image"
```

The script generates ephemeral hexadecimal credentials in a mode-0600 temporary
env file, uses them only for container-local bootstrap and the child test
process, and removes the file on exit. It never prints them. The exact target
identity that must appear in the approval is:

```text
container=continuumwiki-m5-003b-mysql;database=cw_m5_003_certification;endpoint=127.0.0.1:13310;image=<the exact value of CW_M5_003B_MYSQL_IMAGE>
```

The script verifies `podman port`, `DATABASE()`, `VERSION()`,
`@@version_comment`, root host rows, and `SHOW GRANTS` before exporting the
test connection. Its grant is limited to the exact database and the DDL/DML
needed by the existing schema initializer and host fixture; no `GRANT OPTION`
or global privilege is accepted.

Cleanup is installed before container creation and handles `EXIT`, `INT`, and
`TERM`. It checks the run label before removing anything, saves sanitized
container diagnostics under `/tmp` if available, removes the secret env file,
and returns a cleanup failure rather than claiming success. A pre-existing
container with the requested name is never reused or deleted.

## Existing certification coverage

`TightWiki.Tests/Integration/MediaWikiTemplateStylesMySqlHostTests.cs` uses the
real application Identity and MySQL repositories against the pre-created exact
schema. It covers cookie issuance and recognition, persisted page/source
permissions, public and restricted stylesheet delivery, logout, historical
revision selection, suppression, source deletion, permission revocation,
old-URL reuse, conditional requests, concurrent identity isolation, CSS
sanitization, and security headers. The revocation assertions specifically
reject a stale cookie/asset URL and a stale conditional request.

The test also checks the runtime database identity as the application user.
It does not invoke the production MariaDB-only readiness startup path; that
distinction is recorded separately.

## Static verification performed

```bash
bash -n scripts/run-m5-003b-certification.sh
scripts/run-m5-003b-certification.sh
dotnet test TightWiki.Tests/TightWiki.Tests.csproj \
  -c Debug --no-restore -m:1 \
  --filter 'FullyQualifiedName~MediaWikiTemplateStylesHostAuthorizationTests|FullyQualifiedName~MediaWikiTemplateStylesMySqlHostTests'
dotnet test TightWiki.Tests/TightWiki.Tests.csproj -c Debug --no-restore -m:1
dotnet build TightWiki.Tests/TightWiki.Tests.csproj -c Release --no-restore -m:1
python3 -m json.tool artifacts/validation/mediawiki-templatestyles-host-authorization-m5-003b-v1.json
git diff --check
```

The focused authorization tests must pass while the real host test is skipped
when authorization is absent. A skipped real-host test is not certification.

## Independent readiness classification

`HOST_AUTHORIZATION_CERTIFIED` requires the operator execute path to pass the
identity/privilege/network preflight, the real-host test to run rather than
skip, all security assertions to pass, and cleanup to succeed. Until then the
strongest supported classification is `HOST_AUTHORIZATION_BLOCKED`, with
provisioning hardening and static checks reported separately.

Even a successful MySQL 8.4 provider-level run must be reported as:

> Identity/persistence authorization verified against MySQL 8.4; production
> application readiness is not certified by this run.

