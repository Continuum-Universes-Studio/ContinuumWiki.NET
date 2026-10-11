# MediaWiki tables and media (CW-M3-004C)

CW-M3-004C extends the existing 004B expansion and 004A safe renderer. It does
not create a second parser, template engine, attachment store, or production
integration. Imported Wikitext remains lossless in the import revision; table,
infobox, media, and gallery HTML is a transient render result.

## Tables and infoboxes

The bounded structural parser recognizes `{|`, `|}`, `|-`, `|+`, `!`, `|`,
`!!`, and `||`. It supports captions, multiline cell content, nested links,
expanded templates/parser functions, nested tables, and explicit `rowspan` and
`colspan` metadata. The public render result exposes typed table models for
diagnostics and verification; HTML uses semantic `table`, `caption`, `tr`,
`th`, and `td` elements.

An imported template such as `{{SpeciesInfobox|name=...}}` is expanded by the
existing 004B resolver first. The resulting table is then parsed by this
stage. Optional rows, links, references, categories, and media therefore use
the same existing repository and cache boundaries. A template revision changes
the provider-computed template cache version and invalidates the page output.

## Attribute allowlist and limits

Table and row/cell attributes are limited to safe `class`, validated `id`,
`role`, `scope`, `aria-label`, `rowspan`, and `colspan` values. Classes and ids
are token-validated; spans are integers from 1 through 64. Inline styles,
event handlers, unknown attributes, unsafe URL text, and CSS expressions are
ignored with diagnostics. Defaults are 8 nested tables, 1,000 rows, 5,000
cells, 100,000 characters per cell, and 16 attributes per table element.

## Media options

The supported display subset is `thumb`/`thumbnail`, `frame`, `frameless`,
`border`, `left`, `right`, `center`, `none`, bounded `Npx` or `Npx x Mpx`
dimensions, `alt`, `link`, and `upright` with a bounded width approximation.
Internal `link=` targets use imported page resolution; external media links
are rejected. Image dimensions are limited to 4,096 pixels. Imported bytes are
served through the existing permission-checked `/File/Binary` route. The
current system does not generate thumbnail files, so thumbnail-style syntax
uses the original imported bytes with responsive HTML width/height hints and
an explicit compatibility difference.

Unresolved or restricted files produce a visible safe placeholder and an
`UnresolvedMedia` diagnostic. Filenames are never treated as paths or fetched
from external locations.

## Galleries

Block `<gallery>` syntax supports multiple `File:`/`Image:` entries, Unicode
filenames, captions, and responsive `traditional`, `packed`, and `nolines`
presentation modes. `widths`, `heights`, and `perrow` are bounded. Unknown
modes and attributes, malformed entries, missing files, and unclosed galleries
produce explicit diagnostics. No JavaScript gallery framework is involved.

## Responsive styling and security

`TightWiki/Styles/components/mediawiki.less` is imported by the existing LESS
pipeline. All selectors are scoped under `cw-mediawiki-*`; native TightWiki
tables and theme resources are not changed. Tables use horizontal overflow on
narrow screens, infoboxes collapse to full width, and galleries/media scale to
their containing surface while retaining theme custom properties.

Imported text is HTML-encoded and no scripts, event handlers, arbitrary HTML,
Lua, C#, shell commands, external media fetches, or unsafe URLs are executed.
The parser and renderer reuse the existing input, output, parse, diagnostic,
template, and time budgets plus the table/media/gallery limits above.

Remaining gaps include Scribunto, arbitrary extension tags, full MediaWiki
CSS/layout parity, generated thumbnails, complex gallery modes, magic words,
and complete HTML table layout parity.
