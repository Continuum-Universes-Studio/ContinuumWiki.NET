# CW-M3-004D verification

CW-M3-004D uses synthetic MediaWiki XML, a disposable MariaDB 11.8 instance,
and a development-only local HTTP process. It does not access production wiki
content, credentials, or attachment storage.

## Automated evidence

The focused `MediaWikiWikitextRendererTests` suite covers typed page/template
context, title and namespace words, historical metadata, fixed-clock UTC/local
behavior, controlled string/format/path/time functions, lazy core functions,
unsupported extensions, limits, and renderer security. The disposable harness
is:

```bash
./scripts/verify-m3-004d.sh
```

It imports `magic-functions-fixture.xml`, checks native-page isolation,
current and historical page output, template context isolation across pages,
revision metadata, formatting/path functions, unsupported `#invoke`, and the
HTTP path used by the page cache.

Required repository validation remains:

```bash
dotnet restore TightWiki.sln
dotnet build TightWiki.sln --configuration Debug -m:1
dotnet test TightWiki.sln --configuration Debug --no-restore -m:1
MSBuildEnableWorkloadResolver=false dotnet publish TightWiki/TightWiki.csproj \
  --configuration Release --output ./artifacts/publish
```

The test host may require the documented loopback-socket approval in restricted
Linux sandboxes. The MariaDB harness is disposable and removes its container
and temporary files on exit.
