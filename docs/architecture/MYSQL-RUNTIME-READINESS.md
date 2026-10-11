# MariaDB runtime readiness

## CW-M3-001 current status

ContinuumWiki.NET is MariaDB-native when `Database:Provider=mysql` is selected.
The supported target is MariaDB 11.8. The runtime initializes the MariaDB
schema and canonical embedded defaults, configures Identity and provider-neutral
services, validates installation readiness, and never constructs or opens the
legacy SQLite database path. Production startup requires the explicit
`Database:MySql:ProductionReady=true` operations gate plus persistent Data
Protection keys and the disposable readiness evidence; this task does not
authorize or perform production deployment.

Fresh MariaDB 11.8 installation, 161/161 configured integration tests,
REC-01 through REC-17, authenticated page create/edit/revision workflows,
homepage/tags/localization/theme smoke checks, backup/restore reconciliation,
Debug build, and Release publish were verified against disposable
infrastructure. The machine-readable HTTP result is
`artifacts/validation/cw-m3-001/recovery-http.json`.

## Current provider target: MariaDB

CW-M2-006B validated the development-only MySQL-compatible runtime against a
disposable `docker.io/library/mariadb:11.8` host (`11.8.9-MariaDB-ubu2404`).
The historical CW-M2 migration and cutover reports remain available for
development provenance. CW-M2-006C is not applicable to production deployment:
the production source is MediaWiki and the future migration path is
MediaWiki → ContinuumWiki.NET.
The older Oracle MySQL 8.4 results below remain useful historical baseline
records but must not be read as MariaDB verification.

CW-M2-005H.0 is retained as historical implementation context. CW-M3-001
adds the installable MariaDB runtime and explicit production-readiness gate.

## Activation boundary

The MariaDB path is selected when `Database:Provider=mysql`. Development mode
supports disposable administrator bootstrap; non-development mode additionally
requires `Database:MySql:ProductionReady=true`.
The connection string may be supplied as `ConnectionStrings:MySql` or
`Database:MySql:ConnectionString`. No SQLite `DatabaseManager` is constructed
in this branch; invalid or incomplete settings fail rather than falling back.

Example (credentials are intentionally omitted):

```bash
ASPNETCORE_ENVIRONMENT=Development \
Database__Provider=mysql \
Database__MySql__DevelopmentAdminPassword='<disposable-password>' \
ConnectionStrings__MySql='Server=127.0.0.1;Port=3306;Database=continuumwiki;User ID=<user>;Password=<password>;Allow User Variables=True;' \
dotnet run --project TightWiki/TightWiki.csproj --configuration Debug
```

The initializer performs non-destructive schema compatibility checks, creates
the supported MySQL schema, imports canonical configuration, translation,
menu, and default content, configures MySQL EF Identity, and creates the
requested disposable administrator when needed.

## Startup dependency classification

| Area | MySQL development status |
| --- | --- |
| Connection/session, configuration, pages/revisions, page support, identity profiles/permissions, operations, defaults, translations, maintenance health | Mandatory provider-neutral services |
| ASP.NET Core Identity, cookie authentication, authorization, profile bootstrap | Mandatory MySQL-specific services |
| Localization, menus, cache, MVC, Razor Pages, plugin loading | Mandatory application services with compatibility checks |
| Legacy SQLite-shaped repository interfaces | Explicit gates that reject unsupported calls; they never create SQLite services |
| Emoji preload, SQLite file maintenance, legacy logging UI, uncovered statistics/maintenance, backup/restore | Unsupported or intentionally limited in MySQL development mode |

Authorization is not bypassed. Supported neutral consumers must not use a
legacy gate, and SQLite retains its original repository behavior.

## Readiness checklist

- [x] Provider selection and MariaDB 11.8 activation are explicit.
- [x] Schema compatibility and required initialization run before requests.
- [x] Configuration, translations, menus, and supported page seed content use
  the MySQL seed boundary.
- [x] MySQL Identity, profile, role membership, and administrator bootstrap
  use consistent identifiers.
- [x] No SQLite fallback is registered or opened by the MySQL branch.
- [x] Legacy plugin compatibility remains enforced.
- [x] Production startup fails closed unless the explicit readiness gate,
  persistent Data Protection keys, supported server, schema, Identity, and
  seeded-content checks pass.
- [ ] Actual production deployment, MediaWiki migration, final backup/restore
  drill, rollback, and operational monitoring authorization remain separate
  operations work.

Historical CW-M2-006A.1 additionally restored a disposable Oracle MySQL 8.4 logical dump into a
distinct disposable database and reconciled deterministic counts, page/revision
hashes, and attachment binary hashes. HTTP recovery smoke coverage remains
separate and is not implied by this result.

HTTP smoke results must be recorded from the actual target MariaDB host. A
successful build or filtered repository test run is not evidence that login,
page CRUD, search, attachments, or administrative routes work over HTTP.

Historical CW-M2-006A.2 verified the restored application path on 2026-10-09 using a
fresh disposable Oracle MySQL 8.4 source/restore pair. The restored process started
with the development-only gate, authenticated the runtime-created synthetic
administrator, returned HTTP 200 for the protected database administration
route, home, and search, and returned HTTP 302 for anonymous administration
access. No SQLite provider was configured or opened in that run.

Historical CW-M2-006A.3B added `scripts/mysql-recovery-drill.sh` as the repeatable
disposable lifecycle wrapper. It keeps the MySQL 8.4 container alive through
fixture/test phases and collects logs before cleanup. Its readiness probe uses
TCP after MySQL initialization, preventing the prior temporary-socket race.
The three page-support tests passed 3/3 and the configured MySQL integration
filter passed 19/19 under this lifecycle.

Historical CW-M2-006A.3D.1 completed the restored-data HTTP pipeline on 2026-10-09.
REC-01 through REC-17 passed against pre-backup fixture records after restore,
including page/revision routes, current and historical attachment downloads,
comments, localization/theme/profile/menu routes, administrator authorization,
unsupported backup rejection, and logout. No SQLite fallback was configured.

The 2026-10-09 disposable-host smoke run started the application on
`127.0.0.1:5085` against MySQL 8.4, loaded the login page with HTTP 200,
authenticated the disposable administrator, reached the protected database
administration page with HTTP 200, and returned the profile avatar with HTTP
200. The root route returned its existing redirect and the final notification
page returned HTTP 200. Logout was submitted and the protected admin route
then redirected to login. Page creation/editing, revision retrieval, search,
and attachment upload were not claimed by that smoke run.

CW-M2-005H.1 extended this on a fresh disposable MySQL 8.4 database. The
authenticated HTTP workflow exercised:

- `/Page/Create`, page display, `/Edit`, `/Revisions`, a specific revision,
  `/Compare/{revision}`, stale-revision rejection, rename, and `/Page/Search`;
- Unicode title/body, `@@Tags`, heading metadata, and an internal `[[Wiki
  Help]]` link;
- `/Comments` POST and retrieval;
- `/File/ManualUpload`, `/File/PageAttachments`, `/File/Binary`, and
  `/File/Revisions`, including replacement and retrieval of both file revisions;
- `/Profile/My` theme selection (`Darkly`) and `/Page/SetLocalization` (`fr`);
- protected admin access and confirmed MySQL backup rejection; and
- logout followed by unauthorized admin redirection.

The workflow exposed and fixed two defects: built-in internal-link rendering
now uses the provider-neutral page repository, and localization redirects
escape Unicode paths before writing the HTTP `Location` header. No production
database or credentials were used.

## Production prerequisites

Before production activation, separately verify deployment secret handling,
HTTPS or a trusted reverse proxy, persistent key permissions, monitoring,
rollback, MediaWiki source migration, and the complete supported and
unsupported administrative surface. The application validates its MariaDB
server, schema, Identity, seeded content, and key-path prerequisites; it does
not itself authorize or perform production deployment.
