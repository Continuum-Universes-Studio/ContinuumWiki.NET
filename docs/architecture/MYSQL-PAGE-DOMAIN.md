# MySQL page domain migration

CW-M2-003 adds an independently constructible Oracle MySQL page slice. It does not replace `ITwPageRepository`: that public plugin contract still exposes SQLite factories and methods whose implementation depends on SQLite `ATTACH`, parser services, Identity profiles, files, search indexes, and separate deleted-page databases.

## Operation coverage matrix

| Domain operation | Existing SQLite implementation | MySQL implementation | Dependencies and transaction boundary | Coverage |
| --- | --- | --- | --- | --- |
| Create page and initial revision | `PageRepository.SavePage` plus `CreatePage.sql` and `InsertPageRevision.sql` | `MySqlPageRepository.CreatePageAsync` | Page and revision insert are one transaction; author GUIDs are retained without an Identity FK | MySQL integration |
| Retrieve current/specific revision | `GetPageRevisionById.sql`, `GetPageRevisionByNavigation.sql` | `GetPage.sql`, `GetPageByNavigation.sql` | Page-to-revision FK; optional deleted visibility is explicit | MySQL integration |
| Edit and revision history | `UpdatePage.sql`, `UpdatePageRevisionNumber.sql`, `InsertPageRevision.sql` | `UpdatePageAsync`, `GetRevisionHistoryAsync` | Row lock plus expected revision check; page update and immutable revision insert are one transaction | MySQL integration |
| Rename/navigation identifier | `SavePage` derives `TwNamespaceNavigation.CleanAndValidate` | `UpdatePageAsync` stores current and historical navigation | `ActiveNavigation` is unique and nullable only while deleted; historical navigation stays on each revision | MySQL integration |
| Delete/restore | Moves rows between SQLite `pages`, `deletedpages`, and `deletedpagerevisions` databases through the adapter | `DeletePageAsync`, `RestorePageAsync`, `GetDeletedPageByIdAsync`, and `GetDeletedPagesAsync` | Active navigation is released on delete and reoccupied on restore; state change is transactional; stale revisions return false and restore conflicts roll back | MySQL integration |
| Tags | `UpdatePageTags.sql`, `GetPageTagsById.sql` | `ReplaceTagsAsync` | Replace and insert set is one transaction; parser remains application-owned | Schema/transaction integration |
| Outbound references/backlinks | `UpdatePageReferences.sql`, `GetBacklinkPagesPaged.sql` | `ReplaceReferencesAsync`; `PageReference` target is nullable and resolved in MySQL | Replace set is one transaction; target is `ON DELETE SET NULL` | Schema/transaction integration |
| Files/comments/search/statistics/cache | Multiple SQLite repositories/scripts and sibling databases | Page-support schema plus neutral metadata/content/revision/association operations | Attachment administration listing/orphan purge and cache invalidation are covered; token ranking and statistics reporting remain | Partial and explicitly unsupported at application level |

## Schema and semantics

`Scripts/MySql/Pages/V1__PageSchema.sql` is an explicit, repeatable version-one initializer. It creates InnoDB tables with UTF8MB4 and `utf8mb4_0900_ai_ci` comparison, explicit keys, foreign keys, indexes, and UTC `DATETIME(6)` values. `ActiveNavigation` is unique for active pages and becomes `NULL` for deleted pages, allowing a replacement page to use a deleted page's navigation while retaining the deleted row for restoration.

`PageRevision` is immutable from the repository API. It stores the page name, namespace, navigation, content, checksum, change summary, author ID, and timestamp for every revision. `Page.Revision` identifies the current revision. Revision updates require the caller's expected revision; a stale writer receives `MySqlPageConcurrencyException` rather than silently overwriting a newer revision.

`Scripts/MySql/Pages/V2__PageSupportSchema.sql` adds InnoDB tables for comments, file metadata/revisions, revision attachments, processing instructions, search tokens, and current editors. `MySqlPageSupportRepository` provides transactional comment CRUD, file revision upsert, processing-instruction replacement, and exact-token replacement/find operations. It does not yet claim full legacy method parity or fuzzy/ranked search parity.

Names and navigation are normalized with the existing `TwNamespaceNavigation.CleanAndValidate` rules. The repository validates the 128-character legacy limit before writing. User/profile names are intentionally not joined because Identity and the repository user/profile tables are not yet migrated; author GUIDs are retained as audit values without a cross-domain MySQL fallback.

## Explicit boundaries

This slice is not application-wired and does not claim full `ITwPageRepository` parity. It does not migrate Identity, permissions, deleted-revision purge, statistics, logging, or SQLite deleted-database export/import. Parser-driven metadata rebuild orchestration and attachment administration listing/orphan purge are now provider-neutral; SQLite keeps its legacy storage behavior while MySQL uses transactional replacement and cleanup. Core attachment metadata/content/revision/association operations retain binary content in the existing page-file revision tables. Page deletion/restoration clears the existing page cache after a successful commit. Selecting MySQL for the application remains rejected by `DatabaseProviderConfiguration`, preventing a mixed SQLite/MySQL runtime.

## Test setup

Opt-in tests require `CONTINUUMWIKI_RUN_MYSQL_TESTS=1` and `CONTINUUMWIKI_MYSQL_TEST_CONNECTION`. The connection must point to a dedicated disposable Oracle MySQL database. Tests use unique page names and never reset, truncate, or drop an unverified target. They cover Unicode content/titles, create/read/edit/history/specific revision, rename/navigation uniqueness, soft-delete/restore, optimistic concurrency, parameter-looking content, referential cleanup, rollback, and connection cleanup.
