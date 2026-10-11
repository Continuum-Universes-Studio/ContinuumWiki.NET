# MediaWiki module compatibility validation

CW-M3-006C adds an offline, read-only module inventory command:

```bash
dotnet run --project MediaWikiInventory/MediaWikiInventory.csproj -- modules \
  --input <offline-mediawiki-export.xml> \
  --output artifacts/validation/mediawiki-module-inventory-v1.json
```

The report is deterministic for the same export. It records each namespace-828
module, every imported revision identity and timestamp, a source hash, static
`require`/`mw.loadData` dependencies, dynamic-loader warnings, referenced
`mw.*`/`cw.*` APIs, suspicious unsupported references, `#invoke` sites, and a
deterministic representative selection. It performs no Lua execution.

CW-M3-006C.2 also provides a read-only invocation inventory:

```bash
dotnet run --project MediaWikiInventory/MediaWikiInventory.csproj -- invocations \
  --input <offline-mediawiki-export.xml> \
  --output artifacts/validation/mediawiki-invocation-inventory-v1.json
```

This records page/revision identity, module/function, arguments, raw source
expression, and context disposition. Namespace-828 examples are labeled
`DocumentationExample`; only page/template contexts are candidate real usage.
In the supplied `continuumwiki-modules.xml`, there are 31 documentation
examples and zero candidate page/template usages. The full export or an
approved MediaWiki reference source is therefore required for real usage
fixtures; documentation examples must not be promoted into parity cases.

For an approved source wiki, the operator may capture a read-only reference
using the bounded API utility. It requires an HTTPS API URL and never follows
redirects or logs the optional bearer token:

```bash
python3 scripts/capture-mediawiki-reference.py \
  --api-url 'https://approved.example.invalid/w/api.php' \
  --approved-origin 'https://approved.example.invalid' \
  --page 'ExistingPageTitle' \
  --oldid 12345 \
  --invocation '{{#invoke:ModuleName|function|arg=value}}' \
  --output /path/to/private-reference-capture.json
```

Historical captures send `oldid` without `page`: MediaWiki's `parse` API
rejects `page`, `pageid`, `oldid`, and `text` in combination. The corpus
wrapper passes each source fixture ID through to diagnostics and output
provenance. API errors report the fixture ID, sanitized API error code, and
sanitized message without logging credentials or raw response content.

Set `CW_MEDIAWIKI_BEARER_TOKEN` only in the operator environment when the
approved API requires it. The utility performs one bounded GET request,
rejects non-HTTPS URLs and redirects, records requested/rendered revision IDs,
the raw response hash, rendered HTML, warnings, and module metadata, and makes
the output mode `0600`. Review private captures before retaining them.

Static results are advisory. `StaticDependenciesResolved` means only that no
dynamic loader was observed; it is not proof that a worker can execute the
module. Dependency cycles and contextual resolution remain runtime/import
validation findings. The inventory never changes the source export or a
database.

The companion report command creates an explicit machine-readable baseline for
the selected representatives:

```bash
dotnet run --project MediaWikiInventory/MediaWikiInventory.csproj -- compatibility \
  --input <offline-mediawiki-export.xml> \
  --output artifacts/validation/mediawiki-module-compatibility-v1.json
```

Cases are emitted as `NOT_EVALUATED` until a meaningful reference and the
authorized disposable import/socket run are available. This prevents static
inventory evidence from being misreported as MediaWiki compatibility.

The repeatable disposable gate is:

```bash
scripts/verify-m3-006c.sh
```

It creates only the named `cw-m3-006c-verify` MariaDB container, runs the
storage-backed historical-resolution test, and removes the container on exit.
If `CW_SCRIBUNTO_SOCKET_PATH` is supplied, it additionally runs the existing
real-socket renderer test; otherwise it reports that portion as not evaluated.

## Authorized Konsole socket run

Run these commands from the authorized host session after confirming the
administrator-provisioned socket exists. Do not change its ownership or mode:

```bash
test -S /run/continuumwiki/scribunto-worker.sock
CW_SCRIBUNTO_SOCKET_PATH=/run/continuumwiki/scribunto-worker.sock \
  dotnet test TightWiki.Tests/TightWiki.Tests.csproj \
  -c Debug --no-restore -m:1 \
  --filter 'FullyQualifiedName~MediaWikiScribuntoSocketRendererIntegrationTests'
```

For the real selected-module workflow, first run the disposable harness with
the same environment. It will import the selected real modules into MariaDB,
then run the socket test only when the variable is present:

```bash
CW_SCRIBUNTO_SOCKET_PATH=/run/continuumwiki/scribunto-worker.sock \
  scripts/verify-m3-006c.sh
```

The harness does not interpret a successful socket invocation as parity. A
MediaWiki reference result is still required to classify a case as exact or
equivalent; otherwise it remains partial, unsupported, or not evaluated.

## Current evidence

`artifacts/validation/mediawiki-module-inventory-v1.json` is generated from
the supplied offline export at
`artifacts/imports/mediawiki/continuumwiki-modules.xml`. The current report
contains 30 real modules, 89 revisions, and 24 invocation sites. The
repository's synthetic MoonSharp fixture remains separate test evidence and
must not be confused with the real-export inventory.

The current compatibility report selects four real representatives, but they
remain `NOT_EVALUATED` because no invocation/reference pairs have been
captured. The separate
`artifacts/validation/mediawiki-real-socket-execution-v1.json` records one
authorized socket probe against a synthetic fixture; its failure is not a
real-module parity result.

The existing importer remains the only supported path for persistence-backed
validation: use an explicitly authorized disposable MariaDB target, preserve
source revision provenance, and resolve modules through
`ITwMediaWikiImportRepository`. The existing socket executor continues to use
the authorized Unix-domain worker, bounded protocol, capability checks, and
default rejecting executor. No compatibility result may activate production
Lua or weaken those boundaries.

## Reference comparison status

The current repository has synthetic compatibility expectations and socket
integration coverage. A real-export compatibility report must additionally
carry a captured MediaWiki result, trusted fixture result, or explicitly
reviewed expected output for each invocation. Until that input and an
authorized disposable import/render run are supplied, real-module cases are
**NOT EVALUATED**, not passed. Results should be classified as exact,
equivalent, partial, unsupported, failed, or not evaluated; security-relevant
markup must not be removed by normalization.

## Security checklist

- Lua is executed only by the existing dedicated worker through the authorized
  socket path; never inside ASP.NET Core.
- Imported source is untrusted and remains subject to capabilities, budgets,
  dependency identity, revision snapshots, and output sanitization.
- Inventory does not execute Lua and does not grant filesystem, network, shell,
  environment, reflection, CLR, or database access.
- Disposable import validation must reject non-disposable or non-empty targets
  and must not fall back to production connection settings.
