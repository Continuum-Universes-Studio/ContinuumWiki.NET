# TightWiki upstream baseline

Audit date: 2026-10-09. This is a source audit of the fork; no product feature or schema change was made.

## Verification

- Host: openSUSE Tumbleweed (2026-10-07), Linux x64; .NET SDK 10.0.401, runtime 10.0.12.
- `dotnet restore TightWiki.sln`: completed with no reported errors.
- `dotnet build TightWiki.sln --configuration Debug -m:1 --no-restore`: passed, 0 warnings, 0 errors. All 12 solution projects produced outputs. The default parallel invocation hit a sandbox named-pipe permission error, not a compiler error.
- `dotnet test TightWiki.sln --configuration Debug --no-restore -m:1`: test assembly built, but VSTest aborted before discovery/execution because the environment denied its loopback TCP listener (`SocketException (13): Permission denied`). Therefore no tests are claimed passed; counts are unavailable.
- Startup smoke test was not completed: the same environment prevents the application/test infrastructure from opening local IPC sockets, and startup would initialize/upgrade local databases. No production database was touched.

## Solution and runtime

The web entry point is `TightWiki/Program.cs`. It builds an ASP.NET Core MVC + Razor Pages host, uses Autofac, localization, static files, cookie/Identity authentication, optional Google/Microsoft/OIDC providers, and maps `Page/{action=Display}` plus Razor Pages. `TightWiki/Views/Shared/_Layout.cshtml`, `Views/Page/*`, `Areas/Identity/*`, `Pages/*`, `wwwroot/css/*`, `wwwroot/site.js`, themes, syntax highlighting, and image assets are the existing presentation surface.

Projects are `TightWiki` (web), `TightWiki.Engine` (markup engine), `TightWiki.Library` (shared models/services), `TightWiki.Repository` (SQLite/Dapper persistence), `TightWiki.Plugin` (contracts), `TightWiki.Plugin.Default` and `TightWiki.Plugin.Example` (extensions), `TightWiki.Tests` and `TightWiki.Test.Library`, plus the `Mssql2Sqlite`, `DummyPageGenerator`, `GenerateSeedData`, and `LocalizerScan` tools. All are .NET 10 except `LocalizerScan`, which targets `net10.0-windows`; it nevertheless compiled here and is a Linux portability issue for later work.

## Data model and access

`TightWiki.Repository/Helpers/DatabaseManager.cs` coordinates separate SQLite files for configuration, pages, deleted pages/revisions, users, logging, emoji, statistics, and defaults. `PageRepository`, `UsersRepository`, `ConfigurationRepository`, `LoggingRepository`, `EmojiRepository`, and `StatisticsRepository` create `SqliteManagedFactory` instances and execute embedded/file SQL through Dapper. `TightWiki/Translations/TranslationDatabase.cs` opens the read-only translation database directly. ASP.NET Identity uses `ApplicationDbContext` with `UseSqlite` in `Program.cs`.

Persistent aggregate areas and observed keys/relationships are:

| Area | Main records and keys | Relationship shape |
|---|---|---|
| Pages | Page, PageRevision, PageNavigation/namespace, tags, processing instructions, comments, references, search tokens | Page -> revisions; revisions -> author/files; pages link by navigation and references; deleted stores preserve page/revision history |
| Files | FileNavigation, PageFile, FileRevision and attachment links | page/revision/file many-to-many attachment records; revisioned uploads |
| Users | Identity users plus Profile, Role, AccountRole, AccountPermission, RolePermission, Permission, PermissionDisposition | user/profile and user-role; roles permissions; account-level overrides |
| Configuration | ConfigurationGroup, ConfigurationEntry, constraints, themes, menu items, feature templates | group -> entries; page-backed feature templates; configured theme/menu |
| Operations | Log, Severity, page statistics, compilation statistics | log -> severity; statistics -> page; current-editor join uses `(PageId, UserId)` |
| Content support | Emoji and emoji categories; VersionState | emoji/category association; schema upgrades tracked by version state |

Primary keys are predominantly SQLite `INTEGER PRIMARY KEY AUTOINCREMENT`; join tables and feature templates also use composite keys. Exact DDL and foreign keys remain in `TightWiki.Repository/Scripts`, applied by versioned upgrade scripts in `DatabaseManager`; this audit intentionally does not modify them.

## Reusable upstream capability

The fork already supplies page CRUD/history/revert/delete, namespaces, tags, backlinks, fuzzy search tokens, attachments and revisions, comments, themes/localization, admin screens, Identity/OAuth/LDAP-related dependencies, role and per-account permissions, seed data, plugin diagnostics, and regression tests. These are preservation assets for CW-M1.

License and attribution remain MIT; `LICENSE` credits Josh Patterson. No license file was changed.

## Baseline caveats

The repository contains tracked/local SQLite databases under `Data/`, test/tool data, and a translation database; future commits must keep local/production databases out of version control. `TightWiki/appsettings.json` contains a Windows-style relative data path (`..\\data\\`), which should be addressed only as a narrow Linux configuration compatibility fix. No secrets were added or observed in the inspected configuration.
