# MediaWiki compatibility assessment

The current renderer is TightWiki markup, not MediaWiki Wikitext. `WikiEngine.Transform` creates a `WikiEngineState`, discovers regex/function/handler plugins, and emits HTML through `TwPluginResult`; `PageController` invokes it for page display and sidebar rendering. There is no MediaWiki XML dump importer, template/module runtime, parser-function compatibility layer, or migration report generator in this fork.

Recommended approach is an additive, Wikitext-first migration boundary:

- Export MediaWiki pages, revisions, users/attribution, namespaces, redirects, categories, links, templates and file metadata from a read-only source.
- Persist original Wikitext and revision provenance unchanged; never silently discard unsupported constructs.
- Inventory the real corpus first, then implement a bounded tokenizer/AST and adapters for headings, emphasis, lists, tables, links, file links, categories, redirects, named/positional templates, references, and the actual Continuum infobox/gallery/banner patterns.
- Represent unsupported parser functions, Lua modules, extensions, templates, and dangerous HTML as diagnostic nodes with an import report. Enforce recursion, expansion, size and sanitization limits.
- Use fixture/golden tests for representative Species, Character, Planet, and gallery-heavy pages, plus sampled rendered diffs and link/redirect/category counts.

Primary risks are template/module semantics, parser functions, extension tags, nested transclusion, image/thumb behavior, namespaces and redirects, HTML/CSS assumptions, Unicode title normalization, revision authorship/time zones, and XSS/SSRF through imported content. MediaWiki remains live until measured fidelity, permissions, history, media, rollback, and owner approval gates pass. This is a CW-M1+ migration workstream, not a baseline change.
