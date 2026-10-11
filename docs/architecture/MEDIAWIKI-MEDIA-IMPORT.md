# MediaWiki media import (CW-M3-003B)

CW-M3-003B imports MediaWiki file metadata and offline binaries through the
existing `PageFile`, `PageFileRevision`, and `PageRevisionAttachment` storage.
It does not contact MediaWiki, rewrite article Wikitext, create uploader
accounts, or introduce an external object store.

## Manifest and source layout

The manifest is JSON and is intentionally separate from the MediaWiki XML
export because a page export does not contain the original file bytes.

```json
{
  "ManifestVersion": "cw-m3-003b-1",
  "SourceWikiIdentity": "site|base-url|generator",
  "Entries": [{
    "OriginalFileName": "Example.png",
    "SourceFilePageId": "600",
    "MimeType": "image/png",
    "Size": 12,
    "Sha256": "...",
    "Sha1": "source-sha1-if-supplied",
    "RelativeBinaryPath": "media/example-v2.bin",
    "OriginalTimestamp": "2025-03-02T01:00:00Z",
    "Uploader": { "Kind": "NamedUser", "SourceId": "21", "DisplayName": "Media author" },
    "ArchiveMetadata": "optional source archive information",
    "Revisions": [{
      "SourceMediaRevisionId": "6002",
      "RevisionOrdinal": 2,
      "Timestamp": "2025-03-02T01:00:00Z",
      "Uploader": { "Kind": "AnonymousIp", "IpAddress": "192.0.2.51" },
      "MimeType": "image/png",
      "Size": 12,
      "Sha256": "...",
      "RelativeBinaryPath": "media/example-v2.bin"
    }]
  }]
}
```

`RelativeBinaryPath` is resolved only below the explicitly supplied
`--media-root`. Absolute paths, `..` traversal, reparse points/symlinks,
directories in place of files, missing files, size mismatches, SHA-256
mismatches, and reads over the 512 MiB bounded-reader limit are rejected.
PNG, JPEG, GIF, WebP, and PDF declarations receive lightweight content-signature
checks; other MIME types remain subject to the existing application policy.

## Planning and execution

Create and validate a media-enabled plan without database access:

```text
dotnet run --project MediaWikiInventory -- plan \
  --input <export.xml> --output <plan.json> \
  --media-manifest <manifest.json> --media-root <offline-root>
dotnet run --project MediaWikiInventory -- validate \
  --plan <plan.json> --input <export.xml> \
  --media-manifest <manifest.json> --media-root <offline-root>
```

The planner reports duplicate/case-colliding filenames, missing or mismatched
file pages, missing binaries, MIME warnings, estimated binary size, historical
revision count, and every unresolved `File:`/`Image:` reference. References
are metadata only; original Wikitext remains byte-for-byte unchanged.

Execute against an explicitly disposable MariaDB 11.8 target:

```text
MediaWikiImport provision --target-config <target.json>
MediaWikiImport preflight --input <export.xml> --plan <plan.json> \
  --target-config <target.json> --media-manifest <manifest.json> --media-root <offline-root>
MediaWikiImport import --input <export.xml> --plan <plan.json> \
  --target-config <target.json> --media-manifest <manifest.json> --media-root <offline-root>
```

The importer streams and validates each declared binary before inserting it in
the same transaction as its `PageFileRevision` and provenance records. Existing
target attachments cause a collision failure; they are never overwritten.
Target file revisions use `Guid.Empty` as the import actor marker. Historical
uploader kind, source ID/name/IP, source timestamps, SHA-1, SHA-256, MIME, size,
archive metadata, and source-to-target IDs are stored in
`MediaWikiImportMedia` and `MediaWikiImportMediaRevision`. File references are
recorded in `MediaWikiImportMediaReference`, while file-page revisions use the
existing `PageRevisionAttachment` association.

The import batch is `Completed` only after media and page reconciliation. A
missing file, changed manifest, hash mismatch, interrupted stream, collision,
or failed association rolls back the target transaction and records a
`RolledBack` batch. A repeated completed source/mapping combination is rejected
by preflight.

## Recovery and limits

`scripts/verify-m3-003b.sh` creates synthetic PNG, JPEG, and Unicode-named
fixtures; imports three files and four file revisions; restores a MariaDB
logical backup into a distinct disposable database; compares restored binary
SHA-256 values; checks the existing `File/Binary` route; and removes anonymous
Read permission to verify the route returns a redirect instead of the binary.
The normal MariaDB backup tooling remains authoritative: attachment bytes live
inside `PageFileRevision.Data` and are covered by the existing dump/restore
procedure.

The pipeline does not implement MediaWiki image thumbnail generation, SVG or
active-document sanitization beyond the existing application policy, remote
downloads, archive extraction, advanced MediaWiki rendering, or article-body
rewriting. CW-M3-004A consumes the staged mappings for basic safe file/image
links; file-reference resolution still does not claim full MediaWiki rendering
compatibility.
