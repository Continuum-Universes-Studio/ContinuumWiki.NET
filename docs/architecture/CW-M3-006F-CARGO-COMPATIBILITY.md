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

## CW-M3-006F.4B snapshot entry point

`CargoSnapshotTool` consumes the private MariaDB option file and verified
schema metadata without printing credentials. Capture uses the existing host
database abstraction, a repeatable-read transaction, allowlisted base-table
columns, stable `_ID` ordering, bounded output, and atomic private
finalization. Verification runs after the database session closes and checks
manifest identity, schema hashes, exact row counts, content hashes, row
integrity, and completeness.

The resulting dataset is current-state evidence only. It is not historical
Cargo data, does not authorize runtime registration, and does not establish
MediaWiki parity. Authentic rows remain outside Git under the operator-owned
validation directory.

## CW-M3-006G historical provenance boundary

The eight private MediaWiki reference captures are preserved independently of
the Cargo evidence. Each capture records a page revision, rendered revision,
capture timestamp, and raw API response hash. The source/module inventory also
preserves historical Wikitext and Lua revision identities. Neither artifact
contains the Cargo rows that MediaWiki read while rendering the fixture.

The authenticated dataset `cargo-authentic-current-20261011-1` was captured on
2026-10-11 in a repeatable-read transaction and contains the current rows for
Characters, Divines, Species, Subvariants, and Variants. Its manifest explicitly
sets `HistoricalCorrelationVerified` to false. It is therefore
`CURRENT_STATE_ONLY`, never a fallback for a historical fixture.

The fixture-to-dataset evidence matrix is recorded in
`artifacts/validation/mediawiki-cargo-historical-provenance-v1.json`. No
revision-correlated or time-bounded historical Cargo dataset was identified in
the inspected authorized metadata. Two source-context limitations are recorded
there: no inventoried `Module:DivineMeta` revision is at or before fixture page
revision 9469, and no inventoried `Module:Message box` revision is at or before
fixture page revision 6208. A later module revision is not silently substituted.

Cargo compatibility results must remain in three separate categories:

1. **Historical parity** requires a Cargo dataset correlated to the fixture's
   source revision and matching source dependencies. No current fixture is
   eligible until that evidence exists.
2. **Current-state compatibility** may use the authenticated current snapshot
   only with an explicit current-state dataset identity. It cannot establish
   historical parity.
3. **Synthetic implementation testing** may use synthetic bounded rows to test
   the provider, worker boundary, and renderer. It establishes implementation
   behavior only, not equivalence to MediaWiki source data.

Historical reconstruction from Wikitext, Lua, schema, and current rows is not
treated as authentic Cargo history. Cargo is derived data and may depend on
rebuild timing, extension configuration, helper-table storage, and parser
behavior that page revision IDs do not capture. Missing or uncorrelated data
must remain an explicit `CargoHistoricalProvenanceUnavailable` condition; it
must not become fabricated empty rows or an unversioned current-data fallback.

## CW-M3-007D typed relationship boundary

`MediaWikiCargoRelationships.cs` defines immutable relationship descriptors,
schema identities, bounded queries, dataset provenance, and deterministic
result projection. The catalog records helper tables established by the
authenticated index inventory, including `_rowID`, `_value`, and `_position`
index columns. Because helper-table column rows and helper values were not
captured, these definitions remain `SchemaOnly`; the live provider rejects them
before opening a database connection. `Divines.Children` has no corresponding
verified base or helper table and remains explicitly unsupported.

Synthetic rows can exercise stable ordering, pagination, null handling, output
limits, and the existing typed `cargoResults` boundary. Synthetic results are
never labelled authentic. No worker protocol, authenticated snapshot, Lua
authorization, production provider default, or historical provenance rule was
changed.
