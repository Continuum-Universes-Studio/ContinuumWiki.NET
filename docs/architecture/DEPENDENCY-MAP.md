# Dependency map

## Project graph

```text
TightWiki (MVC/Razor host)
  -> TightWiki.Engine -> TightWiki.Library -> TightWiki.Plugin
  -> TightWiki.Repository -> TightWiki.Library / TightWiki.Plugin
  -> TightWiki.Plugin.Default -> TightWiki.Plugin
TightWiki.Tests -> Engine, Repository, Test.Library
Test.Library -> Engine, Repository, Plugin.Default
Plugin.Example -> NuGet TightWiki.Plugin
Tools -> selected Engine/Repository/Plugin projects
```

## Runtime dependency groups

- Framework: ASP.NET Core 10, Razor Pages/MVC, Identity, EF Core SQLite.
- Persistence: `NTDLS.SqliteDapperWrapper`, Dapper, `Microsoft.Data.Sqlite.Core`, SQLitePCL; SQL scripts are the repository API.
- Rendering: `TightWiki.Engine` reflection-discovered functions and handlers, regex matching, `TwPluginResult`, DiffPlex for revision comparison.
- Infrastructure: Autofac, NTDLS helpers/thread pool, MailKit, ImageMagick, directory-services protocols, runtime caching.
- Build/test: xUnit, VSTest, coverlet, EF tooling.

The current dependency map has no MySQL provider and no MediaWiki parser/importer. `LocalizerScan`'s `net10.0-windows` target and `VPT.Einkr.Client` are isolated tool concerns, but prevent an all-project Linux-only claim.
