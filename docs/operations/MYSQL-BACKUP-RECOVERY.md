# MariaDB backup and recovery

## Current CW-M2-006B MariaDB rehearsal

The current supported rehearsal target is disposable MariaDB
`docker.io/library/mariadb:11.8`, observed as `11.8.9-MariaDB-ubu2404`.
Use the MariaDB client tools and keep credentials in an option file or secret
store. The complete SQLite-to-MariaDB migration and restored-data HTTP evidence
is in [`SQLITE-TO-MARIADB-MIGRATION.md`](SQLITE-TO-MARIADB-MIGRATION.md).

The 2026-10-09 MariaDB recovery artifact was 2,671,009 bytes with SHA-256
`6f2753023b85af81d81de4ff9b457f6b4031e34d8d10f66043c8bb97fe62a863`.
It was restored into a distinct database and the restored application passed
all 17 required REC checks. The machine-readable results are archived under
`artifacts/validation/cw-m2-006b/`.

CW-M2-006B uses an operator-invoked logical dump. The web administration backup action remains intentionally unsupported; it must not imply that a backup was made.

CW-M2-006C additionally rehearsed a pre-smoke logical backup of the migrated
disposable database, restored it into a distinct database, and reconciled page
and revision counts. The machine-readable result is
[`backup-rollback-rehearsal.json`](../../artifacts/validation/cw-m2-006c/backup-rollback-rehearsal.json).
The rollback rehearsal explicitly records that post-cutover writes are not
automatically preserved and require a write journal or manual reconciliation.

## Backup

Use MariaDB client tools from a host that can reach the database. Supply credentials through the client option file or secret store; do not put passwords in the command line or logs.

```sh
umask 077
mkdir -p /secure/continuumwiki-backups
mariadb-dump --defaults-extra-file=/secure/continuumwiki-mariadb.cnf \
  --single-transaction --routines --events --triggers --hex-blob \
  --default-character-set=utf8mb4 --skip-comments \
  --databases continuumwiki > /secure/continuumwiki-backups/continuumwiki-$(date -u +%Y%m%dT%H%M%SZ).sql
```

The command must be run with `--single-transaction` for the InnoDB snapshot. It does not make non-InnoDB tables or concurrent DDL transactional, and it is not point-in-time recovery: binary-log capture and replay are not implemented here. Check the exit code and delete a partial output before treating the dump as a backup. Generate a sidecar JSON manifest with `MySqlBackupManifest`, including its SHA-256 checksum; never put connection strings, passwords, tokens, or user secrets in that manifest.

The MySQL persistence adapter stores page bodies, revision history, comments, and attachment binary content in MySQL tables. No external upload directory is part of this provider path. If a future plugin stores files outside MySQL, add that path to the backup plan before deployment.

## Restore rehearsal

Create a new disposable database and verify the target identifier differs from the source before any destructive SQL. Verify the manifest checksum, then restore into the fresh target:

```sh
mariadb --defaults-extra-file=/secure/continuumwiki-mariadb.cnf \
  --database=continuumwiki_restore_YYYYMMDD \
  < /secure/continuumwiki-backups/continuumwiki-YYYYMMDDTHHMMSSZ.sql
```

Run schema compatibility validation and compare a stable integrity snapshot (row counts, page/revision IDs and current links, users/profiles/roles, attachment hashes, comments, translations, themes/menus, and schema versions) between source and target. Start a disposable application instance against the target using the normal startup validation. Exercise login, page/revision history, search, tags/backlinks, attachment downloads, comments, theme/menu/localization loading, and administrator authorization.

Never pipe a dump into the source database, production database, or a database selected from untrusted input. A restore is complete only after the checksum, integrity comparison, and application smoke test succeed.

## Failure handling

Reject missing or tampered artifacts, unsafe/same-database targets, incompatible schema versions, connection failures, and permission errors. Remove partial dump files. Record only the tool exit status and sanitized error text; credentials must not appear in logs.

## Historical CW-M2-006A.1 Oracle MySQL drill record (2026-10-09)

An isolated `docker.io/library/mysql:8.4` Podman container was used; no production endpoint was contacted. The source and target databases were `continuumwiki_source` and `continuumwiki_restore`. The source dump was 2,336,619 bytes with SHA-256 `30ec81a61b9a1424264f04717809b3947240aeb0d8cd7f883e63f3e0490ba234`.

Selected page, page-support, and maintenance integration setup passed 13/13 tests and created representative defaults, pages, revisions, references, tags, configuration, menus, translations, Identity, and attachment data. The dump restored into the distinct target. Deterministic counts matched: Page 12, PageRevision 14, PageFile 2, PageFileRevision 2, PageReference 3, PageTag 6, AspNetUsers 1, ConfigurationEntry 73, MenuItem 3, and Translation 932. Page/revision logical hashes matched. Both attachment revision binary hashes matched: `039058c6...01cfb81` and `9f64a747...b6a806a`.

Appending a byte to a copied dump produced checksum `06e9f735ed469f0ae30d1a1c4be935e22552fe387b776c3a0b69ef6a0dbe7717`, which did not match the manifest checksum. Existing safety tests passed 3/3. This drill did not claim restored application HTTP startup/login coverage; that requires a separate launch of the development-only MySQL application path. The dump is not point-in-time recovery because binary-log capture/replay is not implemented.

## Historical CW-M2-006A.2 Oracle MySQL restored application verification (2026-10-09)

A fresh disposable source was initialized by the normal development-only
runtime, creating `recovery-admin` through ASP.NET Core Identity. It was dumped
and restored into `continuumwiki_restore` in the same isolated MySQL 8.4
container. This artifact was 2,675,462 bytes with SHA-256
`08c650159d84a99aa7259ed9bd4f20d46822ffdee92fe1be579fb55624cd2984`.

The restored application was started with `ASPNETCORE_ENVIRONMENT=Development`,
`Database:Provider=mysql`, and `Database:MySql:DevelopmentOnly=true` on
`127.0.0.1:5086`. Schema validation completed and the process reported healthy
startup. HTTP checks passed: login 302, authenticated `/Admin/Database` 200,
search 200, home 200, logout 200, and anonymous `/Admin/Database` 302. The
restored Identity row retained a password hash. Source and restore durable
counts matched after startup: Page 104, PageRevision 104, PageFile 0,
PageFileRevision 0, AspNetUsers 1, Profile 1, MenuItem 3, Translation 932.

The original failed login attempt against the earlier fixture was not treated
as an application result: that fixture contained a test-created administrator
with no password hash. The successful run used a fresh runtime-created
synthetic account and did not mutate restored data to make authentication work.

## Historical CW-M2-006A.3B Oracle MySQL lifecycle correction (2026-10-09)

The premature cleanup root cause was orchestration across separate shell
invocations: the container was removed at the end of one command before the
next fixture/test command ran. `scripts/mysql-recovery-drill.sh` now owns one
container for all supplied phases, waits for TCP MySQL readiness, exports the
disposable test connection, and uses an EXIT trap to collect sanitized logs
before removal on success or failure. It rejects production-like names and
identical source/restore database names.

The corrected entry point executed all three page-support tests with the same
live Oracle MySQL 8.4 container: 3 passed, 0 failed, 0 skipped. The complete
configured Integration filter then passed 19/19. The run exposed and fixed a
real provider defect: direct MySQL repository consumers lacked Dapper's Guid
handler for `CHAR(36)` Identity values. Registration now occurs at the MySQL
connection boundary, so application and integration consumers share the same
mapping.

## Historical CW-M2-006A.3D.1 Oracle MySQL complete recovery pipeline (2026-10-09)

The single-command pipeline used `scripts/mysql-recovery-drill.sh` with
`scripts/mysql-recovery-fixture.sql` and the existing HTTP driver. It started
Oracle MySQL 8.4 in Podman, initialized the source through the development-only
application, created the recovery article/revisions/tags/link/comment,
replacement attachment, French/theme claims, and menu fixture before backup,
created a logical dump, restored it into `continuumwiki_restore`, started the
application against that restore, ran REC-01 through REC-17, and cleaned up the
application/container after evidence collection.

The final source dump checksum was recorded in `/tmp/cw-3d-source.sha256` and
the machine-readable HTTP evidence was saved at
`/tmp/continuumwiki-recovery-3d1-results.json`. All 17 checks passed. Current
attachment SHA-256 matched `f31f6e882548760d827c06cb1af9871f1fc0762d5f19fdb8cccfe2c88d258b17`; historical revision 0 matched
`df6b383223c3616989564fcd78d21c10bdea11dbb45dafdc5abe71e4a931b0fa`.
