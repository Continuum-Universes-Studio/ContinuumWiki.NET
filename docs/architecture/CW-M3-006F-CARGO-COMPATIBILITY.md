# CW-M3-006F Cargo compatibility audit

CW-M3-006F.1 is an audit only. The imported module export contains observed
Cargo reads, but it does not contain Cargo-owned table rows or a historical
Cargo snapshot. Consequently no Cargo result is reconstructed and no Cargo
compatibility claim is made.

## Observed dependency surface

The historical revisions used by the six diagnostic fixtures reference
`mw.ext.cargo.query`. The affected modules are `Module:SpeciesVariants`,
`Module:DivineTree`, `Module:DivineFamily`, `Module:DivineMeta`, and
`Module:CharacterFamily`. The report records each observed revision, source
line, table, field, dynamic input, and result shape in
`artifacts/validation/mediawiki-cargo-dependency-audit-v1.json`.

The module export contains 11 `#cargo_query` occurrences and no
`#cargo_declare`, `#cargo_store`, or `#cargo_compound_query` occurrences.
Those counts are limited to the imported module XML; they do not prove that
the wider wiki has no Cargo parser-function usage.

## Provenance finding

The XML export preserves Wikitext and Lua revisions, not Cargo's separately
maintained tables. Current Cargo rows, if obtained from an authorized source,
must not be presented as historical rows for a prior page revision. A valid
parity comparison requires a Cargo snapshot with an explicit source wiki,
schema/data version, capture time, and relationship to the requested page and
module revisions.

## Boundary for a future implementation

Cargo access must remain a trusted-host operation. Lua must not receive SQL,
database handles, filesystem access, network access, or arbitrary callbacks.
The proposed interface is a typed, read-only query plan containing only
allowlisted table and field identifiers, bounded predicates and options, and
the fixture's revision/provenance identity. Results should be deterministic
row tables with bounded row, field, byte, query-count, and time budgets.

The current one-request/one-response worker protocol is not sufficient for an
implicit arbitrary host callback. The safer choices are either pre-resolving
approved query results into the request's typed dependency bundle or adding an
explicit, bounded request/resume protocol with correlation, cancellation, and
provenance. The latter requires a separate protocol design and tests; it is
not part of this audit.

Unsupported constructs include arbitrary SQL, unallowlisted identifiers,
unbounded joins, writes, and fallback to unversioned current data. Missing or
historically unproven Cargo data must produce structured `CargoDataUnavailable`
or `CargoHistoricalProvenanceUnavailable` results, not fabricated empty rows.

## Recommended CW-M3-006F.2 scope

1. Obtain an authorized read-only Cargo schema and versioned data snapshot.
2. Define typed allowlisted plans for the observed `Variants`, `Subvariants`,
   `Divines`, and `Characters` surfaces.
3. Add deterministic host-side resolution and provenance-bearing worker input.
4. Test rejection of unknown tables/fields, unsupported query features, row
   and output limits, cancellation, missing snapshots, and historical mismatch.
5. Capture new independent MediaWiki references against the same dataset before
   evaluating parity.

No production database, worker deployment, Lua authorization, or strict import
planner behavior was changed by this audit.
