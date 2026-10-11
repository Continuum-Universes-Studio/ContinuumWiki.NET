# Experimental Lua API reference

This document describes the currently implemented CW-M3-006B worker surface.
Lua runs only in the isolated MoonSharp worker. Every namespace is installed
only when its capability is present in the typed request; missing capabilities
are not inferred from source code.

## Native `cw.*`

| API | Contract | Availability |
|---|---|---|
| `cw.text.trim(value)` | Returns a trimmed string. | Supported |
| `cw.text.split(value, separator)` | Returns a bounded array using literal ordinal splitting. | Supported |
| `cw.text.replace(value, search, replacement)` | Performs bounded literal ordinal replacement; empty search is rejected. | Supported |
| `cw.text.escape(value)` | HTML-encodes a string. | Supported |
| `cw.text.nowiki`, `encode`, `decode`, `listToText` | Reuses the bounded text operations documented in the capability matrix. | Supported partial subset |
| `cw.args.get(key)` / `has(key)` | Reads only current named arguments or positional key `"1"`. Missing values remain nil/false. | Supported |
| `cw.title.current` | Supplied frame title or empty string. | Supported |
| `cw.title.normalize(value)` | Trims and converts underscores to spaces. | Supported |
| `cw.title.namespace(value)` | Returns the textual prefix before a colon, or empty string. | Supported |
| `cw.module.name` / `revision` / `dependencies` | Reads validated request and dependency metadata only. | Supported |
| `cw.page.exists(title)` / `metadata(title)` | Returns false/nil until an authorized immutable page snapshot is supplied. | Explicitly unavailable |

## MediaWiki compatibility `mw.*`

The supported subset includes bounded `mw.text` trimming, literal splitting,
nowiki, entity encoding/decoding, list formatting, JSON encode/decode;
Unicode scalar operations in `mw.ustring`; request-derived `mw.title` and
`mw.site` snapshots; invariant `mw.language` case operations; and the
allow-listed `mw.html.element` constructor. `mw.loadData`, `mw.loadJsonData`,
and `require` remain restricted to the validated dependency bundle.

`mw.html.element` accepts only these tags: `b`, `code`, `div`, `em`, `i`,
`li`, `p`, `span`, `strong`, `sub`, `sup`, `u`, `ul`; and only `class`, `id`,
and `title` attributes. Text and attributes are HTML-encoded and bounded.

## Explicitly unsupported

The worker does not provide live page existence/content lookup, live namespace
or site configuration lookup, locale-sensitive formatting without an explicit
language context, arbitrary HTML, CSS/style/event attributes, filesystem,
network, environment, process, reflection, database, repository, or CLR
access. Unsupported pattern matching in the partial `mw.text` and
`mw.ustring` functions fails explicitly with a structured runtime diagnostic.

These limitations are intentional compatibility boundaries, not claims of full
Scribunto equivalence. The authoritative capability inventory is maintained in
`CONTINUUMWIKI-LUA-API-MATRIX.md`.
