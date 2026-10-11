# MediaWiki magic words and controlled functions

CW-M3-004D extends the existing `MediaWikiTemplateExpander`; it does not add a
second renderer, execute Lua/Scribunto, or evaluate arbitrary extension code.
Expansion produces bounded Wikitext, which continues through TightWiki's
escaping, link, media, reference, table, and gallery renderer.

## Typed render context

Each imported render has an immutable `MediaWikiExpansionContext` containing:

- the viewed page title, namespace id/name, source page and revision ids;
- source wiki identity, content model, historical revision timestamp, target
  page id, and target revision;
- site name, content language, culture, time zone, and one injected UTC clock
  value captured at render start.

The viewed page context does not change while a template is transcluded. A
template's source revision is recorded as a dependency and is used for
historical template resolution, but `PAGENAME`, `REVISIONID`, and related
words describe the page being viewed. Direct rendering of a template page uses
that template page as the viewed page.

## Supported magic words

The title family supports `PAGENAME`, `FULLPAGENAME`, `BASEPAGENAME`,
`SUBPAGENAME`, `ROOTPAGENAME`, `NAMESPACE`, `NAMESPACENUMBER`,
`TALKPAGENAME`, and `SUBJECTPAGENAME`, with the encoded `...E` forms for
title output. Namespace relationships cover the standard MediaWiki subject,
talk, Template, File/Image, Help, Category, and Module ids. Unknown namespace
aliases are not silently treated as filesystem paths.

`SITENAME` comes from configured wiki identity, and `CONTENTLANGUAGE` comes
from `TwConfiguration.ContentLanguage`. `REVISIONID`, `REVISIONTIMESTAMP`,
`REVISIONYEAR`, `REVISIONMONTH`, and `REVISIONDAY` use the imported historical
source revision metadata, never the current database clock.

UTC clock words include `CURRENTYEAR`, `CURRENTMONTH`, `CURRENTDAY`,
`CURRENTDAY2`, `CURRENTTIME`, and `CURRENTTIMESTAMP`. Local counterparts use
the viewer/configured `TimeZoneInfo`. `CURRENTMONTH` is zero-padded, while
`CURRENTDAY` is unpadded and `CURRENTDAY2` is padded. All clock values in one
render use the same injected instant.

## Controlled parser and string functions

The existing lazy `#if`, `#ifeq`, `#switch`, `#expr`, and `#ifexpr` behavior is
preserved. The bounded additions are `#titleparts`, `#rel2abs`, `#time`,
`lc`, `uc`, `lcfirst`, `ucfirst`, `padleft`, `padright`, and `formatnum`.
`#time` accepts a small explicit token allow-list (`%Y`, `%y`, `%m`, `%n`,
`%d`, `%j`, `%H`, `%G`, `%i`, `%s`, `%c`, `%U`, and `%%`); unsupported tokens,
invalid timestamps, excessive padding, and excessive output produce
diagnostics rather than unbounded work.

Scribunto `#invoke`, Variables, StringFunctions, DPL, CategoryTree, and other
unavailable extension/parser functions remain literal and produce explicit
unsupported-extension diagnostics. Unsupported magic words are likewise
diagnosed without invoking extension code.

## Cache and security boundaries

Imported page cache identity includes page/revision provenance, template cache
version, viewed-page context, culture, time zone, site name, content language,
and a one-second UTC clock bucket. A render that actually consumes current
time is marked `DependsOnCurrentTime` and is not stored as a reusable page
result. All function output remains subject to the existing expansion,
diagnostic, and renderer limits; no filesystem path or dynamic code execution
is introduced by `#rel2abs` or any magic word.
