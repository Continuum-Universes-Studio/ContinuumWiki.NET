# MariaDB-native installation

ContinuumWiki.NET uses MariaDB 11.8 as its native application database. A new
installation starts with an empty MariaDB database; it does not open or import
TightWiki SQLite files. The existing SQLite compatibility path and the optional
`SqliteToMariaDb` utility remain available for historical development work, but
are not part of this installation path. Migration from the existing production
MediaWiki site is a separate milestone.

## Configuration

Do not commit credentials. Supply the connection string through a secret store
or environment variable:

```bash
ASPNETCORE_ENVIRONMENT=Production
Database__Provider=mysql
ConnectionStrings__MySql='Server=127.0.0.1;Port=3306;Database=continuumwiki;User ID=continuumwiki;Password=<secret>;'
Database__MySql__ProductionReady=false
```

The runtime requires MariaDB 11.8, initializes the provider schema, validates
schema compatibility, seeds configuration, translations, themes, menus, and
feature templates, and configures ASP.NET Core Identity. The first isolated
installation may set `Database__MySql__DevelopmentAdminPassword` temporarily
to bootstrap an administrator; production secrets must be provisioned outside
the repository.

`ProductionReady=true` is an explicit operations authorization gate. It does
not deploy or modify system services. Set it only after validating HTTPS or a
trusted reverse proxy, persistent Data Protection keys, writable attachment and
backup paths, logging, health/readiness monitoring, and a tested MariaDB backup
restore into a separate database.

The checked-in [`appsettings.Production.template.json`](../../TightWiki/appsettings.Production.template.json)
contains placeholders only. Supply the connection string and secrets through
environment variables or an external secret store, restrict the Data Protection
key directory to the application identity, and put the app behind HTTPS or a
trusted reverse proxy. The MariaDB runtime exposes `/health/live` for process
liveness and `/health/ready` for MariaDB, version, and schema readiness.

## Verification and recovery

Use the existing MariaDB backup/recovery harness and verify the SHA-256
manifest, restore into a separate disposable database, start the application
against that restore, and execute REC-01 through REC-17. Never point the
harness or installation commands at the live MediaWiki or production MariaDB
systems during this milestone.

Unsupported provider values and non-MariaDB or non-11.8 servers fail closed;
there is no SQLite fallback when `Database:Provider=mysql` is selected.

## Roadmap

CW-M2-006C is historical development evidence and is **NOT APPLICABLE — LEGACY
SQLITE CUTOVER** to this installation. The production source is MediaWiki, and
the future migration path is MediaWiki → ContinuumWiki.NET.
