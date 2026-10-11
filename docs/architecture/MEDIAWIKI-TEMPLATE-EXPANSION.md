# MediaWiki template expansion (CW-M3-004B)

CW-M3-004B extends the bounded `MediaWikiWikitextRenderer` with transient
template expansion. Imported article bodies remain unchanged in `PageRevision`
and their source hashes/provenance remain owned by the import tables. Native
TightWiki pages still use `WikiEngine.Transform`.

## Resolution boundary

`ITwMediaWikiImportRepository` resolves a template from the completed import
batch that owns the article revision. Only source namespace `10` (`Template`)
is eligible. The resolver joins `MediaWikiImportPage`,
`MediaWikiImportRevision`, `Page`, and `PageRevision`, so it returns the
persisted body and its source/target revision identity without creating an
application account or a second template store. Incomplete, deleted,
redirect, ambiguous, or cross-batch content is not exposed through
transclusion.

For a current article, the newest eligible template revision is selected. For
a historical article, the newest template revision whose source timestamp is
not later than the article source timestamp is selected. This is an explicit
best-effort historical policy; it is not a claim of complete MediaWiki
timestamp-accurate dependency resolution when source timestamps are missing.

Supported calls are:

```text
{{TemplateName}}
{{Template:TemplateName}}
{{TemplateName|value}}
{{TemplateName|key=value}}
```

Titles retain Unicode and normalize underscores to spaces. `subst:`,
`safesubst:`, module calls, and other namespace forms are diagnosed and left
for the safe renderer to mark unsupported.

## Parameters and inclusion

Positional parameters are numbered in encounter order (`{{{1}}}`, `{{{2}}}`),
named parameters use their trimmed names, and the last duplicate named value
wins. An explicitly empty value is different from an absent value:
`{{{name|default}}}` uses the default only when `name` is absent. Defaults may
contain nested templates, links, pipes, equals signs, and Unicode text. All
balanced splitting is performed by the expansion scanner; it does not use a
naive `Split('|')` over nested Wikitext.

For transclusion:

- `<noinclude>...</noinclude>` is omitted;
- `<includeonly>...</includeonly>` is retained without its tags;
- when one or more `<onlyinclude>...</onlyinclude>` regions exist, only those
  regions are retained.

When a template page itself is rendered, `noinclude` content is retained,
`includeonly` content is omitted, and `onlyinclude` tags are stripped. Missing
or malformed boundaries produce `MalformedInclusionTag` diagnostics.

## Parser-function subset

The initial allow-list includes `#if`, `#ifeq`, `#switch`, `#expr`, and
`#ifexpr`. Branches are lazy: only the selected branch is expanded. `#switch`
uses exact ordinal comparison after trimming and supports `#default`. The
restricted expression grammar accepts decimal numeric literals, parentheses,
unary signs, `+`, `-`, `*`, `/`, `%`, and comparisons (`==`, `!=`, `<`, `<=`,
`>`, `>=`). It uses invariant-culture `decimal` arithmetic, rejects unknown
tokens, bounds token count and expression length, and diagnoses division by
zero and overflow. These numeric rules intentionally differ from full
MediaWiki parser behavior.

Unknown parser functions, `#invoke`, invalid expressions, and unsupported
template syntax produce explicit compatibility diagnostics. No dynamic
language evaluation, Lua/Scribunto, JavaScript, shell, network, or C#
execution exists in this path.

## Limits and security

Expansion uses separate bounded depth, operation, output, expression-token,
diagnostic, input, and time budgets. Direct and indirect template cycles are
tracked by source page/revision identity and stop with `RecursiveTemplate`.
Missing, restricted, and ambiguous templates are diagnosed rather than
silently treated as successful expansions. Expansion returns Wikitext only;
the existing parser then performs HTML encoding, safe link/media resolution,
reference/category collection, and extension-tag rejection. Template bodies
and parameter values therefore cannot inject trusted HTML.

## Dependencies and cache identity

Render results expose the resolved template source page/revision dependencies.
The imported page cache key also contains a provider-computed version of the
eligible template revisions selected for the article's historical timestamp.
The version hashes source page IDs, source revision IDs, target revisions, and
persisted Wikitext hashes. A template update, import of a previously missing
template, or deletion/restriction therefore selects a new cache key without a
reverse-dependency table. Native cache keys remain unchanged and separate.

## Known gaps

The 004D expansion stage does not implement Scribunto, arbitrary extension
hooks, HTML passthrough, language variants,
or complete parser parity. Expanded template output may now contain the 004C
table, infobox, advanced-media, and gallery subset; it is still returned as
Wikitext and parsed by the same bounded renderer. Unsupported constructs
remain in source persistence and are represented by safe diagnostics/markers
at render time.

Future Scribunto integration, if ever approved, must be a separately sandboxed
provider boundary and must not execute imported Lua inside the web process.

The disposable fixture and HTTP verification are in
`MediaWikiInventory/Fixtures/template-expansion-fixture.xml` and
`scripts/verify-m3-004b.sh`.

CW-M3-004D adds the bounded magic-word and function subset documented in
`MEDIAWIKI-MAGIC-WORDS-AND-FUNCTIONS.md`. Its synthetic fixture and disposable
MariaDB/HTTP verification are `magic-functions-fixture.xml` and
`scripts/verify-m3-004d.sh`; unsupported extension functions remain diagnosed
and are never executed.

CW-M3-005A treats `#invoke` as a typed rejected boundary. Template expansion
preserves the original invocation while returning structured Scribunto
diagnostics/dependencies; module source resolution and module cache versions
are provider operations for a future sandbox, not execution capabilities in
the current web process.
