# MediaWiki rendering foundation (CW-M3-004A)

CW-M3-004A adds a bounded MediaWiki Wikitext compatibility path without
changing the native TightWiki engine or the stored imported body.

## Dispatch and persistence

`ITwMediaWikiImportRepository` selects a typed
`TwMediaWikiRenderSource` for the exact target page and target revision. A
completed `MediaWikiImportRevision` row selects `ImportedMediaWiki`; no
provenance row selects `NativeTightWiki`. The SQLite adapter always returns
the native descriptor. This selection does not use a title or namespace
convention.

The same lookup is performed for historical revisions. The page cache key
includes the source-format descriptor, import batch, source page, and source
revision, so imported and native output cannot share a cache entry.

`WikiEngine.Transform` remains the native path. Imported display is dispatched
by `PageController` to `MediaWikiWikitextRenderer`; comments, sidebars, and
other application-owned content continue to use the native renderer.

## Supported subset

The renderer parses a restricted intermediate document model and supports:

- levels 1-6 headings (`= Heading =`);
- bold, italic, and combined bold/italic emphasis;
- imported internal links with provenance navigation resolution;
- validated `http` and `https` external links;
- paragraphs and single-line breaks;
- ordered and unordered lists, including bounded nesting;
- horizontal rules;
- HTML-encoded literal text and `<nowiki>` bodies;
- imported redirect display as a link without following the redirect;
- `File:` and `Image:` links through imported attachment provenance;
- bounded Wikitext tables with captions, header/data cells, nested content,
  safe classes/ids, and bounded `rowspan`/`colspan`;
- template-generated infobox tables after the existing 004B expansion stage;
- common `thumb`, `thumbnail`, `frame`, `frameless`, `border`, alignment,
  dimension, `alt`, `link`, and feasible `upright` media options;
- bounded `<gallery>` blocks with Unicode filenames, captions, and responsive
  basic layouts;
- typed category recognition and a small category-link projection;
- `<ref>` references and `<references />` with deterministic local numbering.

Unresolved page and media targets remain visible as safe text/placeholders and
produce diagnostics. Thumbnail generation is not available for staged imports;
the supported fallback serves the imported original through the existing
permission-checked Binary route with responsive width/height hints.

## Unsupported syntax and diagnostics

Unsupported templates, parameters, parser functions, `#invoke`, unknown
extension tags, unbalanced delimiters, malformed reference tags, unsafe
external URLs, and unresolved imported dependencies are recognized without
unsafe evaluation. The CW-M3-004B allow-list evaluates ordinary imported
templates, parameters, inclusion tags, and `#if`/`#ifeq`/`#switch`/`#expr`/
`#ifexpr` through a transient Wikitext expansion stage; unsupported forms are
replaced by an encoded, marked unsupported span or safe text. Imported source
is never emitted as HTML.

Diagnostics contain a stable code, message, source offset and length, and
one-based line and column when a source location is available. Categories and
references are retained in the typed render result even when their target
projection is unresolved.

## Security and resource limits

Imported Wikitext is untrusted. The renderer has no script, C#, Lua, shell,
template, extension-hook, or network execution path. Text, labels, URLs, and
media attributes are HTML encoded. External links are allow-listed to `http`
and `https`; media filenames are resolved by the import repository and reject
path traversal. Media rendering uses existing permission-checked attachment
routes.

Default limits are 1,000,000 input characters, 1,500,000 output characters,
32 parse depth, 16 template expansion levels, 10,000 expansion operations,
500ms expansion time, 128 expression tokens, 10,000 blocks, and 100
diagnostics. Exceeding a limit is a diagnosed safe failure or bounded
truncation; it does not change persisted source text.

## Known differences and next priorities

This is not MediaWiki parity. Scribunto/Lua, arbitrary extension tags,
magic-word semantics, language-link projection, generated thumbnail files,
gallery modes beyond the documented subset, and MediaWiki HTML/CSS emulation
remain unsupported. No production MediaWiki endpoint or live content is used.
