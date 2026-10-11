# CW-M5-004 Native Wikitext parser compatibility

## Scope and verdict

`NATIVE_PARSER_COMPATIBILITY_PARTIALLY_VERIFIED`.

This milestone verifies a bounded, synthetic compatibility subset of the
imported Wikitext renderer. It does not establish full MediaWiki 1.45 parity,
historical reference parity, Lua/Scribunto parity, Cargo parity, or extension
compatibility. The rejecting Scribunto executor remains the default, production
Lua remains disabled, and the strict migration planner is unchanged.

The audit found one confirmed native-parser defect: a label-only `#switch`
argument did not fall through to the following keyed result. The smallest
correction records a matching bare label and selects the next keyed value
without expanding unselected values. The correction is covered by an
authentic parser-shape regression test. No other confirmed defect was found in
the exercised subset.

## Audit matrix

| Area | Classification | Evidence and bounded behavior |
| --- | --- | --- |
| `#if` | `VERIFIED_COMPATIBLE_SUBSET` | Expands the condition, treats empty/whitespace-only text as false, and expands only the selected branch. |
| `#ifeq` | `VERIFIED_COMPATIBLE_SUBSET` | Expands both operands, trims them for ordinal string comparison, and expands only the selected result. Numeric normalization and case folding are not part of the declared subset. |
| `#switch` direct/default cases | `VERIFIED_COMPATIBLE_SUBSET` | Trims the switch value, matches keyed cases, preserves `#default`, and expands only the selected result. |
| `#switch` label fall-through | `CONFIRMED_DEFECT` then fixed | Bare matching labels now select the following keyed result. The regression also proves an unselected missing template is not resolved. |
| `#expr` / `#ifexpr` | `VERIFIED_COMPATIBLE_SUBSET` | Restricted decimal arithmetic, precedence, parentheses, unary signs, modulo, and comparisons; invalid, divide-by-zero, overflow, length, and token-limit input is rejected with typed diagnostics. |
| Nested template arguments | `VERIFIED_COMPATIBLE_SUBSET` | Top-level pipe/equal splitting preserves nested templates and parser-function values, including an embedded equal sign. |
| Expansion ordering and lazy branches | `VERIFIED_COMPATIBLE_SUBSET` | Conditions and operands are expanded before selection; unselected branches/cases are not resolved. Typed terminal Scribunto errors stop later dependency work while preserving source text. |
| Malformed input | `VERIFIED_COMPATIBLE_SUBSET` | Unbalanced delimiters, parser depth, input, and output limits remain bounded and diagnosed. |
| Recursion and budgets | `VERIFIED_COMPATIBLE_SUBSET` | Direct, mutual, parser-function, and nested Scribunto cycles are stopped; operation and wall-clock failures retain distinct diagnostics. |
| Unsupported parser functions | `UNSUPPORTED_BEHAVIOR` | Extension functions such as `#cargo_store` remain read-only, diagnosed, and unevaluated. This is intentional and not a native-parser parity claim. |

## Implementation evidence

The parser-function dispatch and lazy selection paths are in
`TightWiki.Engine/MediaWiki/MediaWikiTemplateExpander.cs`. The restricted
expression parser is private and allow-listed; it does not evaluate arbitrary
code or invoke a provider. `FindTopLevelEquals` and `SplitTopLevel` retain
nested construct boundaries before argument parsing.

The targeted regression coverage is in
`TightWiki.Tests/Unit/MediaWikiWikitextRendererTests.cs`:

- `EvaluatesLazyCoreParserFunctionsAndRestrictedArithmetic`
- `SwitchSupportsFallThroughLabelsAndKeepsUnselectedResultsLazy`
- `EvaluatesIfAndIfEqWithWhitespaceNestedConditionsAndLazyBranches`
- `SwitchUsesFirstMatchingCaseEmptyResultAndDefaultWithoutExpandingOtherCases`
- `EvaluatesRestrictedExpressionPrecedenceDecimalsUnaryOperatorsAndRejectsInvalidInput`
- `PreservesNestedParserArgumentsWhenSeparatorsAndEqualsAreNested`
- `ParserFunctionOperandsUseTheEligibleHistoricalTemplateRevision`
- `EnforcesDiagnosticCountAndCancellationBounds`
- existing cycle, malformed-input, diagnostic, and budget tests

The fall-through correction is limited to `#switch` case selection. It does
not alter source Wikitext, revision selection, dependency resolution, worker
transport, execution limits, or the strict import planner.

## Budgets and failure policy

The tested defaults remain unchanged:

- input: 1,000,000 characters
- expanded output: 1,500,000 characters
- template depth: 16
- expansion operations: 10,000
- expansion wall clock: 500 ms
- diagnostics: 100
- restricted expression length: 512 characters, with the configured token limit

Budget failures are fail-closed and are not converted into successful output.
Finite synthetic workloads complete within the defaults. Cycles and deliberate
limit fixtures terminate with typed diagnostics. No diagnostic-only budget was
raised for this milestone.

## Verification and limitations

The focused renderer suite passed 51/51. The full Debug suite passed 308/308
with 23 intentional skips. The Release build passed with 0 warnings and 0
errors. The machine-readable evidence is
`artifacts/validation/mediawiki-native-parser-compatibility-v1.json`.

These are source-level and synthetic renderer results. They do not replace
independent MediaWiki references or prove exact HTML parity. Real-module
comparison, historical dependency completeness, Cargo provenance, worker host
certification, and production migration readiness remain separate gates.
