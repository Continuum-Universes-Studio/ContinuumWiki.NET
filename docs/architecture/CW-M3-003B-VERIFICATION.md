# CW-M3-003B verification

Verification date: 2026-10-09. No production MediaWiki endpoint, database, or
real user media was accessed.

## Implemented

- Typed JSON media manifest with source file-page IDs, revision IDs, names,
  timestamps, uploader provenance, MIME/size/SHA-256/SHA-1 metadata, archive
  metadata, and relative offline paths.
- Bounded offline resolver rejecting absolute/traversal paths, symlink/reparse
  escapes, missing files, substitutions, size mismatches, checksum mismatches,
  unsupported object types, and declared MIME signature mismatches.
- Deterministic planner diagnostics for file-page relationships, references,
  duplicate/case-colliding names, missing files, MIME warnings, historical
  revision counts, and estimated binary size.
- Version-2 MariaDB provenance tables for media, media revisions, and resolved
  Wikitext file references. Binary content remains in existing
  `PageFileRevision.Data` storage.
- Transactional import with source-order historical revisions, source
  timestamps/attribution, no uploader accounts, collision rejection, hash
  reconciliation, rollback status, and completed-batch retry rejection.
- Existing `File/Binary` route verification and anonymous permission denial.

## Evidence

- Focused media unit tests: 3 passed, 0 failed, 0 skipped.
- Focused disposable MariaDB 11.8 importer test: 1 passed, 0 failed, 0
  skipped. It imported 3 files and 4 historical media revisions, reconciled 6
  file references, verified restored binary SHA-256, provenance attribution,
  and duplicate-import rejection.
- `scripts/verify-m3-003b.sh`: completed against disposable MariaDB 11.8;
  verified plan/preflight/import, original and historical binary hashes,
  logical backup/restore binary preservation, existing HTTP download bytes,
  and a 302 authorization redirect after anonymous Read removal.
- Full default suite: 154 passed, 0 failed, 21 skipped, 175 total. The
  skipped tests are environment-gated MySQL integration tests; the media
  integration was separately executed with MariaDB.
- `dotnet restore TightWiki.sln`: passed.
- `dotnet build TightWiki.sln --configuration Debug -m:1`: passed, 0 warnings,
  0 errors.
- Required default publish command reached restore but exited 1 in this
  environment during workload resolution. The equivalent serial/no-restore
  publish with `-p:MSBuildEnableWorkloadResolver=false` passed and produced
  `artifacts/publish/`.

## Remaining limitations

MediaWiki thumbnail generation, full MediaWiki rendering, SVG/active-document
policy beyond the existing application policy, archive extraction, remote
downloads, and article-body rewriting remain out of scope. File-reference
resolution is preserved as migration metadata and does not claim MediaWiki
rendering compatibility.

Final verdict: `CW-M3-003B VERIFIED`.
