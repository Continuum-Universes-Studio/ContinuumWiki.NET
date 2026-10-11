# MariaDB cutover decisions — CW-M2-006C.2

This is an operator-review package, not an approval record. It is preserved as
historical CW-M2-006C evidence and is **NOT APPLICABLE — LEGACY SQLITE
CUTOVER** to CW-M3 production readiness. The production source is MediaWiki,
the target is MariaDB 11.8, and the future migration path is MediaWiki →
ContinuumWiki.NET. No decision below grants production deployment approval.

## Evidence register

| Evidence | Result |
| --- | --- |
| [`original-integrity.json`](../../artifacts/validation/cw-m2-006c/original-integrity.json) | Original source integrity operation failed with four record-level findings |
| [`source-integrity-audit.json`](../../artifacts/validation/cw-m2-006c/source-integrity-audit.json) | SQLite integrity checks passed; four cross-store orphan findings remain |
| [`repair-rehearsal-hashes.json`](../../artifacts/validation/cw-m2-006c/repair-rehearsal-hashes.json) | Isolated copy changed only `deletedpages.db` and `statistics.db` |
| [`repaired-execute.json`](../../artifacts/validation/cw-m2-006c/repaired-execute.json) | Repaired-copy migration: 55 mappings, zero discrepancies |
| [`migrated-http.json`](../../artifacts/validation/cw-m2-006c/migrated-http.json) | Disposable migrated application: 17/17 HTTP checks passed |
| [`backup-rollback-rehearsal.json`](../../artifacts/validation/cw-m2-006c/backup-rollback-rehearsal.json) | Restore counts reconciled; post-cutover writes are not automatically preserved |
| [`default-provenance-comparison.json`](../../artifacts/validation/cw-m2-006c/default-provenance-comparison.json) | All 265 excluded default-store records exactly match the authoritative repository artifact at commit `35c534b37d1c8a013dd64c1afe8b93a70013a493` |

## Orphan-record decisions

The four rows are not the four default-store exclusions. They are distinct
source-integrity findings. The parent page identifier `128` is absent from
both live and deleted page stores, and no source evidence identifies a valid
replacement page, author, revision, or content.

| Issue | Exact record | Observed relationship | Recommended handling | Data-loss/security implication | Approval |
| --- | --- | --- | --- | --- | --- |
| `C1-SRC-001` | `deletedpages.db.PageToken` key `128:profiles` | Missing `Page.Id=128` | Omit this stale derived-metadata row only from the isolated migration copy; retain the original source and repair checksum | May lose stale search metadata; no page body, identity, permission, or authorship is discarded | `PENDING OPERATOR APPROVAL` |
| `C1-SRC-002` | `deletedpages.db.PageToken` key `128:sidebar` | Missing `Page.Id=128` | Omit this stale derived-metadata row only from the isolated migration copy; retain the original source and repair checksum | May lose stale search metadata; no page body, identity, permission, or authorship is discarded | `PENDING OPERATOR APPROVAL` |
| `C1-SRC-003` | `deletedpages.db.PageProcessingInstruction` key `128:hidefootercomments` | Missing `Page.Id=128` | Omit this stale derived-metadata row only from the isolated migration copy; retain the original source and repair checksum | May lose stale display metadata; no page body, identity, permission, or authorship is discarded | `PENDING OPERATOR APPROVAL` |
| `C1-SRC-004` | `statistics.db.PageStatistics` key `PageId=0` | Missing live/deleted `Page.Id=0` | Omit this stale recomputed counter row only from the isolated migration copy and recompute statistics after migration | Loses one unattached derived counter; retaining it would attach metrics to the wrong page or violate target relationships | `PENDING OPERATOR APPROVAL` |

### Alternatives

1. Approve the isolated-copy omissions above. This is the rehearsed option;
   it does not alter the original SQLite source.
2. Preserve the rows in a quarantined legacy-data store. This requires a new
   target quarantine design and reconciliation rules, and was not implemented
   or verified by CW-M2-006C. It is therefore not a current cutover option.
3. Reconstruct page `128` or page `0` relationships. This is rejected because
   no authoritative source record exists; inventing a page, revision, author,
   or relationship would violate source-preservation requirements.

## Four migration exclusions

These exclusions are independent of `C1-SRC-001` through `C1-SRC-004`.
The migration report identifies them as default-store domains proposed for
exclusion. Their canonical provenance is established below from the tracked
distribution artifact and its historical generation inputs, not inferred from
the current MariaDB seeder:

| Issue | Source domain | Rows | Recommended handling | Durable/user-authored risk | Approval |
| --- | --- | ---: | --- | --- | --- |
| `C1-EXC-001` | `defaults.db.DefaultConfiguration` | 73 | Exclude from import and seed the canonical target defaults | Acceptable only if every row is shipped canonical data; customized configuration must be migrated instead | `PENDING OPERATOR APPROVAL` |
| `C1-EXC-002` | `defaults.db.DefaultThemes` | 27 | Exclude from import and seed the canonical target themes | Acceptable only if no theme row is customized or user-authored | `PENDING OPERATOR APPROVAL` |
| `C1-EXC-003` | `defaults.db.DefaultFeatureTemplates` | 61 | Exclude from import and seed the canonical target templates | Acceptable only if no template row is customized or user-authored | `PENDING OPERATOR APPROVAL` |
| `C1-EXC-004` | `defaults.db.DefaultWikiPages` | 104 | Exclude from import and seed canonical Help/Include/Builtin pages | Acceptable only if no built-in page body is customized; customized content must be preserved separately | `PENDING OPERATOR APPROVAL` |

The four-store review is also recorded in
[`exclusion-review.json`](../../artifacts/validation/cw-m2-006c/exclusion-review.json).

### CW-M2-006C.2 provenance result

The read-only audit extracted deterministic JSON snapshots from all 265 source
rows and compared every identifier, value, body, formatting field, hash, and
relevant metadata field against the tracked `defaults.db` artifact at commit
`35c534b37d1c8a013dd64c1afe8b93a70013a493` (`2026-10-03`, `db update.`).
That artifact was independently regenerated from the same commit's
`Data/config.db` and `Data/pages.db` using the repository's existing
`GenerateSeedData` utility; the regenerated four row sets matched exactly.
The source and canonical database SHA-256 is
`6e716393661200b6276a2b5b72a51ddc73e35d72834f1ef16e1ad8ba93b29dab`.

| Domain | Rows examined | CANONICAL_MATCH | CUSTOMIZED | CANONICAL_VERSION_DIFFERENCE | UNKNOWN_PROVENANCE |
| --- | ---: | ---: | ---: | ---: | ---: |
| `DefaultConfiguration` | 73 | 73 | 0 | 0 | 0 |
| `DefaultThemes` | 27 | 27 | 0 | 0 | 0 |
| `DefaultFeatureTemplates` | 61 | 61 | 0 | 0 | 0 |
| `DefaultWikiPages` | 104 | 104 | 0 | 0 | 0 |
| **Total** | **265** | **265** | **0** | **0** | **0** |

No normalization was applied other than deterministic row ordering. The report
contains both source and canonical values for every record, an empty difference
list for every match, and a disposition. There are no customized or unknown
records to preserve separately. The recommended treatment is therefore to
exclude these shipped defaults from source-owned migration and reconcile the
target default store to this canonical artifact, while preserving any target
or user-owned data and requiring explicit operator approval before applying the
exclusions. Canonical provenance is established; the exclusion decision is not
automatically approved by this audit.

## Technical and operational status

| Issue | Class | Current status | Required action |
| --- | --- | --- | --- |
| Record-level integrity reporting and fail-closed migration | Technical correction | `VERIFIED` | Keep the existing `integrity` operation and fail-fast rehearsal tooling |
| Isolated repair transformations | Data-integrity finding | `PENDING OPERATOR APPROVAL` | Approve or reject `C1-SRC-001` through `C1-SRC-004` |
| Default-store provenance | Evidence audit | `VERIFIED` | Review [`default-provenance-comparison.json`](../../artifacts/validation/cw-m2-006c/default-provenance-comparison.json); it establishes 265 canonical matches and no customized/unknown rows |
| Default-store exclusions | Explicit migration decision | `PENDING OPERATOR APPROVAL` | Approve or reject `C1-EXC-001` through `C1-EXC-004`; provenance evidence alone does not grant approval |
| Source write freeze and final integrity snapshot | Operational prerequisite | `NOT EXECUTED` | Schedule only after policy decisions and separate cutover authorization |
| Production deployment/provider switch | Deployment authorization | `BLOCKED` | Requires explicit product-owner/operations authorization; none is present |
| Rollback write reconciliation | Operational limitation | `VERIFIED WITH LIMITATION` | Maintain a write journal/manual reconciliation procedure; snapshot restore alone is insufficient |

## Required operator decisions

An authorized operator must record, outside this document or in an approved
change record:

1. approval or rejection for each `C1-SRC-*` orphan disposition;
2. review of the record-level provenance report and confirmation that each
   `C1-EXC-*` source domain is to be treated as canonical shipped data;
3. approval or rejection for each default-store exclusion;
4. acceptance of the rollback limitation for writes after the cutover
   snapshot; and
5. a separate production cutover authorization after the final source freeze,
   migration, reconciliation, and health-check gates pass.

Until those decisions are recorded, pending approval is not success and the
source remains unsuitable for production migration.

## Recommendation

Do not cut over. The provenance audit establishes canonical identity for all
265 default-store rows, but the four source-integrity treatments, four
default-store exclusions, and production deployment authorization remain
unapproved.

`CW-M2-006C BLOCKED`
