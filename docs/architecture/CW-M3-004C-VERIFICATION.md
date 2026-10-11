# CW-M3-004C verification

The 004C verification uses only synthetic offline XML, a disposable MariaDB
11.8 container, and a development-only local HTTP process. No production wiki,
database, or attachment store is accessed.

## Automated evidence

- `MediaWikiWikitextRendererTests`: 17 passed, 0 failed, 0 skipped.
- Full `TightWiki.sln` regression: 171 passed, 21 intentional skips, 0 failed, 192 total.
- `npm run check:css`: passed.
- `npm run build:css`: passed.
- `./scripts/verify-m3-004c.sh`: passed with `CW-M3-004C VERIFIED`.

The disposable harness imports six synthetic pages and eight revisions, stages
two media files, checks the native `Wiki_About` page, renders the current and
historical `Table_Article` revisions, asserts semantic table/caption/header
HTML, nested tables, template-generated infoboxes, media options, galleries,
Unicode filenames, missing-file diagnostics, and safe filtering of inline
event/style/URL content. It updates the persisted infobox template body and
hash and confirms that the current article cache changes while the historical
revision retains its prior template policy.

## Required build commands

The required commands are run with serialized MSBuild work where appropriate
for the Linux test environment:

```bash
dotnet restore TightWiki.sln
dotnet build TightWiki.sln --configuration Debug -m:1
dotnet test TightWiki.sln --configuration Debug --no-restore -m:1
MSBuildEnableWorkloadResolver=false dotnet publish TightWiki/TightWiki.csproj \
  --configuration Release --output ./artifacts/publish
```

The test host needs loopback socket permission in this environment; the
focused and full VSTest commands are therefore run with the repository's
documented escalation workaround when the sandbox denies the test host
listener. The publish command uses the established
`MSBuildEnableWorkloadResolver=false` workaround.

Remaining compatibility gaps are Scribunto/Lua, arbitrary extension tags,
full MediaWiki CSS/layout parity, generated thumbnail files, complex gallery
modes, magic words, and complete HTML table-layout parity.
