# MySQL development runtime readiness

CW-M2-005H.0 adds an isolated, development-only MySQL application path. It
does not enable production MySQL hosting or claim backup and restore support.

## Activation boundary

The MySQL path is selected only when `Database:Provider=mysql`, the ASP.NET
Core environment is `Development`, and `Database:MySql:DevelopmentOnly=true`.
The connection string may be supplied as `ConnectionStrings:MySql` or
`Database:MySql:ConnectionString`. No SQLite `DatabaseManager` is constructed
in this branch; invalid or incomplete settings fail rather than falling back.

Example (credentials are intentionally omitted):

```bash
ASPNETCORE_ENVIRONMENT=Development \
Database__Provider=mysql \
Database__MySql__DevelopmentOnly=true \
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

- [x] Provider selection and development-only activation are explicit.
- [x] Schema compatibility and required initialization run before requests.
- [x] Configuration, translations, menus, and supported page seed content use
  the MySQL seed boundary.
- [x] MySQL Identity, profile, role membership, and administrator bootstrap
  use consistent identifiers.
- [x] No SQLite fallback is registered or opened by the MySQL branch.
- [x] Legacy plugin compatibility remains enforced.
- [ ] Production cutover, backup, restore, migration rehearsal, rollback, and
  operational monitoring remain separate prerequisites.

HTTP smoke results must be recorded from an actual disposable MySQL host. A
successful build or filtered repository test run is not evidence that login,
page CRUD, search, attachments, or administrative routes work over HTTP.

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

Before production activation, separately verify backup and restore, source-data
migration, user/profile/role and revision integrity, attachment hashes,
rollback, deployment secret handling, monitoring, and the complete supported
and unsupported administrative surface. This development path does not provide
or imply those capabilities.
