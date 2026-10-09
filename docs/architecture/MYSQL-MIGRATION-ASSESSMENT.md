# MySQL migration assessment

Status: assessment only. SQLite was not changed.

The migration is substantial because SQLite is embedded in public abstractions (`SqliteManagedFactory`/`SqliteManagedInstance` in repository interfaces), SQL is split across many SQLite-specific scripts and version upgrades, `DatabaseManager` owns multiple database files, and Identity is separately configured through EF Core SQLite. The translation DB and defaults DB are additional direct SQLite consumers.

Recommended approach:

1. Freeze and inventory the current DDL, seed data, foreign keys, collations, indexes, SQL parameter names, and behavior with characterization tests; keep the current SQLite path as the oracle during transition.
2. Introduce provider-neutral repository/application contracts that do not leak SQLite connection types, while retaining adapters for the existing implementation. Do not change public routes or domain behavior in this phase.
3. Select and pin one .NET 10-compatible MySQL EF/Dapper provider after a Linux CI compatibility proof. Build a new MySQL schema/migration set; do not translate SQLite DDL mechanically.
4. Convert one bounded area at a time, starting with configuration/logging or statistics, then pages/files, then users/Identity. Preserve transaction boundaries and UTC/text/collation semantics.
5. Provide an idempotent, resumable SQLite-to-MySQL export with row counts, checksums, foreign-key validation, attachment hashes, and rollback/cutover rehearsal. Keep SQLite read-only until acceptance.

Key risks are SQLite-specific SQL (`AUTOINCREMENT`, pragmas, collations, paging), multiple-file transaction boundaries, case/Unicode search behavior, binary/text handling, Identity schema alignment, and seed/upgrade ordering. No schema migration should be attempted in CW-M0.
