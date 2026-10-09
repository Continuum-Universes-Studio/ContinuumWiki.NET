# MVC and administrative consumer migration matrix

CW-M2-005C audited the complete application-owned consumer surface. Legacy
repository contracts remain available for plugins, but the following table
records which consumers still prevent a safe MySQL application cutover.

| Consumer area | Legacy dependency | Provider-neutral replacement | Status |
| --- | --- | --- | --- |
| `TranslationStringLocalizer` / factory | Direct `TranslationDatabase` SQLite access | `ITwApplicationTranslationRepository` | Migrated; SQLite adapter preserves read-only behavior |
| `EmailSender` | `ITwConfigurationRepository` and typed group model | `ITwApplicationConfigurationRepository` group reads | Migrated |
| `TwController`, `TwPageModel`, `TwSessionState` | `ITwDatabaseManager`, `ITwPageRepository` | Neutral Identity/configuration/page/page-support services resolved from the request provider | Session/profile/security hydration, page hydration, tags, instructions, and permission lookup are neutral; the legacy manager remains only in explicit administrative/compatibility paths |
| `PageController` display/edit/revision actions | `ITwPageRepository` | `ITwApplicationPageRepository` covers page CRUD, retrieval, current revision, templates, feature templates, revision-checked save, derived metadata, tags, references, and deletion | Reader-facing display/sidebar/template/token/revision flows, built-in internal-link rendering, and create/edit/save/rename/revert persistence are migrated |
| `PageController` search/autocomplete | `ITwPageRepository` | `ITwApplicationPageRepository` search and autocomplete operations | Migrated for exact search/autocomplete; MySQL fuzzy behavior is documented |
| `PageController` comments | `ITwPageRepository` | `ITwApplicationPageSupportRepository` covers insert/delete/paged comments | Reader-facing retrieval and mutations migrated |
| `ProfileController` | Configuration, page, users, database manager | `ITwApplicationIdentityRepository` profile/avatar operations and `ITwApplicationConfigurationRepository` themes | Migrated; page history remains legacy |
| `FileController` | Emoji, page, database manager | `ITwApplicationPageRepository` page lookup plus `ITwApplicationPageSupportRepository` attachment metadata/content/revision, association, upload, and detach operations | Reader-facing and upload page lookups migrated; image processing remains application-owned |
| `TagsController` | Page repository, database manager | `ITwApplicationPageRepository` tag-page and associated-tag operations | Migrated |
| `AdminController` | Emoji, logging, page, statistics, database manager | Neutral configuration, identity lookup, deleted-page lookup/list/delete/restore, deleted-revision lifecycle calls, metadata rebuild, attachment administration, event-log reporting/mutation, page-statistics reporting/mutation, and provider-aware maintenance operations | Database status/actions, default-content/translation/menu seed dispatch, and backup routes use the maintenance contract; SQLite preserves existing behavior, while MySQL reports health/schema status, imports supported defaults, and rejects unsupported backup/restore operations |
| `AdminSecurityController` | Page and database manager | Neutral configuration, profile, role, membership, permission, and native Identity operations | Migrated for security/profile operations; page autocomplete remains neutral and the base controller still owns legacy database access |
| Identity account pages | Configuration and users repositories | ASP.NET Core `UserManager`/`SignInManager` plus neutral profile lookup | Authentication remains native Identity; profile existence/culture lookup migrated |
| `PageController` localization selection | Legacy users repository | `ITwApplicationIdentityRepository` profile lookup/update | Migrated; Unicode return paths are escaped before redirect headers |
| `DatabaseLogger` / provider | `ITwLoggingRepository` | Operations contract lacks paged log query and table lifecycle operations | Blocked |
| `WikiConfigurationManager` | `DatabaseManager` and legacy configuration model | `ITwApplicationConfigurationRepository` group/theme/menu reads | Migrated; emoji/database metrics still use legacy operations |
| `SelfDocument` and engine | `ITwDatabaseManager` and legacy page methods | No neutral seed/document authoring contract | Blocked |
| Plugin loader | Plugin-defined legacy repository usage | `ITwPluginPersistenceCompatibility` | Migrated; incompatible legacy plugins reject in MySQL mode |

| `AdminSecurityController` page/namespace autocomplete | `ITwPageRepository` | `ITwApplicationPageRepository` autocomplete operations | Migrated |

## Page/search/file method inventory

The remaining legacy calls found in the core consumers are:

- SQLite seed orchestration and unrelated administrative reporting still use
  legacy compatibility services; database status/actions and backup routes use
  the provider-aware maintenance boundary;
- administrative statistics/reporting, template seeding, comments outside the
  reader-facing PageController paths, and export/backup/maintenance surfaces
  not included in this slice; and
- plugin-owned legacy repository access, which remains intentionally supported
  for SQLite compatibility.

The neutral MySQL implementations now cover page CRUD/revision history,
current-revision lookup, template-page and feature-template reads, exact-token
search, autocomplete, tag-page queries, associated-tag queries,
tags/references replacement, comment CRUD, processing-instruction replacement,
page-view and administrative statistics, typed configuration reads, event-log
filtering/detail/purge, and exact token replacement/find operations. MySQL fuzzy search uses a
documented token-prefix strategy rather than SQLite's Double Metaphone ranking.
Deleted-page purge and unrelated maintenance remain unsupported for
application consumers; deleted-revision archive/list/retrieve/restore/purge
operations are implemented for MySQL and SQLite. PageController display/sidebar,
comments, search-token display, template lookup, create/edit/save/rename/revert
persistence, parser-derived metadata, typed display configuration, page-view
throttling/statistics, attachment page lookups, and current-editor presence now
use neutral contracts. Security-admin role/permission/profile operations also
use neutral contracts.
Restore preserves the
MySQL soft-deleted row and revision history; SQLite restoration continues to
use the legacy archival store through the adapter.

The SQLite implementation is `SqliteApplicationPageRepository`, a thin adapter
over the unchanged `ITwPageRepository`. The MySQL implementation is
`MySqlPageRepository` using the existing `IDatabaseConnectionFactory` session
boundary. `PageMetadataRebuilder.BuildSnapshotAsync` performs parser work
before saving; both page adapters then persist the page row, revision,
attachment associations, and supplied tags, references, processing
instructions, and search tokens in the provider transaction. Cache invalidation
occurs only after the provider save/delete/restore operation commits. The
neutral search consumer is wired into `PageController`; tag
browse/tag-cloud is wired into `TagsController`; page and namespace
autocomplete is wired into `PageController` and `AdminSecurityController`.

## Cutover decision

The production MySQL startup safety gate remains enabled. The isolated
development branch registers the neutral contracts already implemented in
`TightWiki.Plugin.Interfaces.Persistence` and uses explicit legacy gates for
controller construction where supported actions have not yet been migrated.
Those gates throw clear unsupported-operation errors and never instantiate
SQLite or fabricate successful results. The contracts are not widened with
fake methods merely to satisfy construction.

The next implementation slice must complete the missing domain operations in
the existing contracts (or extend those contracts narrowly), add equivalent
SQLite adapters, and then migrate consumers by domain. Only after controller
and Razor-page resolution succeeds against the same provider should the startup
readiness validation be relaxed.
