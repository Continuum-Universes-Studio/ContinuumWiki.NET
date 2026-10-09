# Plugin persistence compatibility

ContinuumWiki.NET retains the original `ITw*Repository` interfaces, including
their SQLite wrapper properties, for source and binary compatibility with
supported legacy plugins. Those interfaces are a compatibility boundary; they
are not the application contract for a MySQL runtime.

## Provider-neutral contracts

The provider-neutral contracts live in
`TightWiki.Plugin.Interfaces.Persistence` and contain domain operations only.
They do not expose `SqliteManagedFactory`, `SqliteManagedInstance`,
`MySqlConnection`, or another provider-specific connection type. The existing
M2-001B `IDatabaseConnectionFactory` / `IDatabaseSession` boundary remains the
only connection/session abstraction.

The current MySQL implementations implement these contracts directly:

- configuration, themes, and menus;
- pages, revisions, tags, and references;
- page support operations;
- profiles, roles, and permissions;
- logging, statistics, and emoji;
- defaults and canonical menu/translation seed import; and
- translations.

`AddMySqlApplicationPersistence` registers these services without registering
legacy SQLite repositories as substitutes.

## Plugin loading rules

`ITwPluginPersistenceCompatibility` is optional metadata. A plugin that
implements it declares supported providers and whether it still requires legacy
SQLite repositories. A pre-migration plugin with no declaration is accepted in
SQLite mode for compatibility and is rejected in MySQL mode with an actionable
diagnostic. A declared plugin is rejected when its provider list or legacy
repository requirement is incompatible.

The existing assembly identity, public type names, and `ITwPluginModule`
signature remain unchanged. No claim of universal binary compatibility is made:
the precompiled legacy case is supported by conservative SQLite acceptance and
explicit MySQL rejection. A plugin must be rebuilt and declare provider-neutral
compatibility before it can run against MySQL.

## Remaining migration boundary

The base-controller/session page hydration path and reader-facing application
consumers resolve provider-neutral services. Database status/actions,
default-content, menu, and translation seed dispatch, and backup routes now use
`ITwApplicationMaintenanceRepository`; SQLite delegates to the legacy manager
and MySQL provides read-only health/schema status plus an explicit/idempotent
supported-default importer while rejecting unsupported optimize/vacuum and
backup/restore operations. SQLite seed orchestration and some page-maintenance reporting still
consume legacy compatibility services; deleted-revision application calls use
the neutral page contract for both providers;
the consumer matrix records those boundaries. They remain on the verified
SQLite startup path until equivalent provider-neutral maintenance adapters are
implemented. Consequently, the production MySQL startup gate remains
intentional. The development-only branch uses explicit gates for unresolved
legacy consumers; it prevents a partial service graph from silently opening SQLite. Legacy
plugins remain accepted in SQLite mode and explicitly rejected in MySQL mode
when their compatibility metadata requires SQLite; no plugin is silently
redirected to SQLite.

## Migration guidance

New plugins should depend on the provider-neutral interfaces and implement
`ITwPluginPersistenceCompatibility` with the providers they test. Existing
SQLite-only plugins should continue to declare or remain on SQLite until their
repository calls are migrated to the new contracts. No plugin should construct
its own SQLite database when the application provider is MySQL.
