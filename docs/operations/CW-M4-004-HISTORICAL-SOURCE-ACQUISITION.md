# CW-M4-004 historical source acquisition

CW-M4-004 is currently classified **Source investigation verified; acquisition
blocked**. Existing local evidence contains revision metadata and recorded
hashes, but no authenticated raw Wikitext/Lua package for the required
historical records. This milestone does not perform a disposable import.

## Authorization gate

Local read access to `/home/cdjensen94/continuumwiki-validation` authorizes only
inspection of the files already supplied there. It does not authorize:

- new MediaWiki API requests;
- production database queries;
- backup/archive access;
- unrestricted export generation; or
- privilege, credential, service, or policy changes.

Before any external acquisition, an operator must approve the source system,
account scope, exact revision-content request, rate/timeout bounds, private
storage location, and integrity-verification procedure. Credentials must remain
outside Git and outside validation reports.

## Current evidence

The required identities are recorded as metadata:

- `Template:Divine@9469`, page `520`, `2026-03-13T05:24:23Z`;
- `Module:DivineFamily@9467`, page `2033`, `2026-03-13T05:19:46Z`;
- `Jovan@9482`, page `534`;
- `Module:DivineTree@9481`, page `2035`.

No raw body is currently available for those records. `Module:DivineMeta@9470`
is later than the required historical context and must not be substituted. No
earlier eligible revision has been authenticated.

Repository XML files are synthetic test fixtures. The local
`@Utility/tightwikidata.db.sql` file is not a MediaWiki export and is excluded
from source acquisition.

## Permitted acquisition methods

Use the smallest separately authorized method supported by the existing
operator workflow:

1. An existing immutable XML export containing the exact records;
2. An explicitly approved historical backup with established source identity;
3. A bounded read-only MediaWiki revision-content request; or
4. A separately approved read-only database query only if the source contract
   cannot supply the records.

No method was used during this milestone because authorization was not
established.

## Integrity and private storage

For every acquired record, preserve the original bytes before decoding and
record page ID, namespace, title, revision ID, timestamp, content model,
content format, raw byte length, raw SHA-256, acquisition identity, and export
or response integrity. If decoding transforms the bytes, record decoded-content
identity separately; do not normalize before hashing.

Store raw records and manifests only in an operator-approved private directory.
Do not commit raw source, overwrite an earlier package, print bodies in logs, or
place credentials in the tracked acquisition report.

## Closure and follow-on work

After authenticated bodies are available, inspect direct and transitive
dependencies using revision-aware timestamps. Keep static-only references
separate from reached dependencies, preserve missing/data/extension diagnostics,
and reject later revision substitution. Update the eight-fixture eligibility
matrix without changing the six-success/two-error reference classification.

Only a later, separately authorized milestone may run the existing disposable
import harness. Worker certification and historical Cargo parity remain
independent gates.

## Verification

The tracked report is
`artifacts/validation/mediawiki-historical-source-acquisition-v1.json`.
For this documentation-only blocked result, the required checks are:

```bash
python3 -m json.tool \
  artifacts/validation/mediawiki-historical-source-acquisition-v1.json

git diff --check
```

