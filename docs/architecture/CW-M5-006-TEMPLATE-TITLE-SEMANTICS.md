# CW-M5-006 Template title semantics and namespace compatibility

## Scope and verdict

`TEMPLATE_TITLE_SEMANTICS_PARTIALLY_VERIFIED`.

The bounded imported-Wikitext renderer now has deterministic title parsing for
the namespace forms represented by the import planner. This is implementation
and regression evidence, not a claim of complete MediaWiki title parity or
historical HTML parity.

## Current model and implementation

`TwNamespaceNavigation` is the application's web navigation model: it emits
`Namespace::Page`, lowercases by default, and removes characters that are
valid in MediaWiki titles. It is therefore not used to parse imported
MediaWiki titles. The import planner's built-in namespace mapping is the
authoritative bounded alias set. The engine uses a small adapter with the same
canonical names and aliases, including `Main`, `Template`, and `Image`.

The parser produces a namespace ID and title name before persistence lookup:

| Input form | Namespace | Lookup name |
| --- | ---: | --- |
| `{{Name}}` | 10 / Template | `Name` |
| `{{Template:Name}}` | 10 / Template | `Name` |
| `{{:Name}}` | 0 / Main | `Name` |
| `{{Main:Name}}` | 0 / Main | `Name` |
| `{{Image:Name}}` | 6 / File | `Name` |

Underscores become spaces and runs of Unicode whitespace become one space.
URI unescaping is retained. Unicode code points are otherwise preserved; no
new Unicode normalization or case-folding algorithm was introduced. Dynamic
heads are expanded through the same bounded Wikitext expander before parsing,
so their dependencies and budgets remain part of the render.

The repository boundary now has a namespace-aware
`ResolveTransclusionAsync` operation. Namespace 10 retains the established
template path. The MySQL implementation resolves the selected imported page
and revision body using the viewed source timestamp, excludes deleted and
suppressed revisions, orders deterministically, and reports missing,
restricted, or ambiguous results through the existing typed target status.
Backends that do not implement the optional namespace path remain fail-closed.

## Findings matrix

| Area | Classification | Evidence / boundary |
| --- | --- | --- |
| Leading-colon main-namespace transclusion | `VERIFIED_CORRECT` | `:Title` parses as namespace 0 and uses the typed transclusion resolver. |
| Explicit namespace resolution | `VERIFIED_CORRECT` | Built-in planner namespaces 0–15 and 828–829 are recognized; unsupported prefixes are diagnosed. |
| Supported aliases | `VERIFIED_CORRECT` | Planner-aligned aliases, including `Main` and `Image`, map to fixed source IDs. |
| Spaces and underscores | `VERIFIED_CORRECT` | Underscores and surrounding/internal whitespace normalize before lookup. |
| Repeated whitespace | `VERIFIED_CORRECT` | The parser collapses whitespace runs deterministically; imported canonical titles remain the persistence source of truth. |
| Unicode title handling | `PARTIALLY_VERIFIED` | Unicode is preserved and exercised; no speculative normalization or full MediaWiki collation claim is made. |
| Dynamic template names | `VERIFIED_CORRECT` | Dynamic heads re-enter the existing bounded expander and then use the same namespace parser. |
| Historical selection after normalization | `VERIFIED_CORRECT` | The normalized name is resolved before the existing `SourceTimestamp <= viewed timestamp` selection. |
| Invalid titles | `VERIFIED_CORRECT` | Empty, control-character, pipe, malformed namespace, and double-colon forms are rejected without lookup. |
| Ambiguous titles | `VERIFIED_CORRECT` | Existing typed `Ambiguous` status remains blocking for safe source selection. |
| Recursion and cycles | `VERIFIED_CORRECT` | Active source page/revision identity continues to drive cycle detection. |
| Cancellation | `VERIFIED_CORRECT` | Resolver cancellation remains `OperationCanceledException`; it is not converted into a source failure. |
| Diagnostic limits | `VERIFIED_CORRECT` | Existing diagnostic cap and first-cause behavior remain unchanged. |
| Expansion budgets | `VERIFIED_CORRECT` | Dynamic title expansion consumes the existing operation, depth, output, and time budgets. |
| Revision provenance | `VERIFIED_CORRECT` | Target dependencies retain source page/revision IDs and selected target revision. |
| Unsupported namespace behavior | `VERIFIED_CORRECT` | Unknown aliases remain `UnresolvedTemplateNamespace`; no alias is invented. |
| Persistence compatibility | `PARTIALLY_VERIFIED` | MySQL implementation is source-reviewed and compiled; disposable MariaDB execution remains required for authentic verification. |
| Security and production boundaries | `VERIFIED_CORRECT` | Strict import planning, rejecting Lua default, production-disabled Lua, and worker isolation were not changed. |

## Regression evidence

Focused renderer coverage exercises bare and explicit Template titles,
leading-colon and `Main:` transclusion, `Image:` alias recognition, repeated
whitespace, Unicode, dynamic names, historical main-namespace revision
selection, invalid forms, existing ambiguity/cycle behavior, cancellation,
and diagnostic limits. The test uses typed fake repository results and does
not fabricate production source or reference HTML.

The MySQL path is intentionally not certified by these unit tests. A
disposable import containing the relevant source pages is required to verify
the SQL lookup against the deployed schema and to compare actual rendered
output.

## Verification limits

This milestone does not establish complete MediaWiki namespace aliases,
language-specific title rules, Unicode normalization/collation parity,
fragment/subpage edge-case parity, or exact HTML parity. It does not enable
Lua, Cargo, production MediaWiki access, or production database writes.
