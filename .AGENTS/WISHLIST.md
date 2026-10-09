# ContinuumWiki.NET — Feature Wishlist

**Status:** Proposed ideas / discovery backlog — **not approved implementation requirements**  
**Version:** 0.2  
**Date:** 2026-10-09  
**Product:** ContinuumWiki.NET  
**Scope:** Continuum Universes–specific MediaWiki replacement  
**Companion documents:** [MDD.md](MDD.md) · [AGENTS.md](AGENTS.md)

## 1. Purpose and decision rules

This is the franchise-specific feature wishlist for ContinuumWiki.NET. It records aspirations and candidate improvements *without* silently modifying the agreed architecture or authorizing Codex to implement them. All 53 requests from the product owner are retained below, including overlapping ideas, so none is lost during consolidation.

- **Status legend:** `Idea` (captured, not designed), `Discovery` (needs inventory/spike), `Approved for design` (owner has explicitly authorized specification), `Committed` (owner has explicitly approved implementation and updated the MDD/backlog). **All items currently have status `Idea`.**
- **Priority legend:** `P0` foundational / needed to deliver the intended replacement; `P1` high-value franchise workflow; `P2` desirable expansion; `P3` experimental or high-cost optional system. Priorities are **tentative proposals, not commitments or scheduling**.
- **Decision authority:** The product owner approves changes to scope, priority, status, canon-facing behavior, security and implementation. Codex may investigate or propose designs but must not infer approval from priority.
- **Existing requirements prevail:** Consult [MDD.md](MDD.md) and [AGENTS.md](AGENTS.md) first. Some wishlist entries restate MDD features and may already be baseline candidates; recording them here does not demote or expand those requirements. On conflict, document the conflict and request an explicit design decision before implementation.
- **No silent migrations:** Existing Wikitext, pages, authorship, categories, templates, redirects, uploads and revision histories must be protected. The live MediaWiki installation stays authoritative until cutover gates pass.

## 2. Wiki & Editing

| ID | Feature | Tentative priority | Status | Design notes / acceptance direction |
|---|---|---|---|---|
| WIKI-01 | Visual WYSIWYG editor alongside Wikitext | P1 | Idea | Preserve raw Wikitext round-tripping; don't strip unknown constructs or replace advanced source editing. |
| WIKI-02 | Live split-screen editing and previews | P1 | Idea | Debounced, safely rendered preview; preserve unsaved text; predictable errors. |
| WIKI-03 | Reusable C# / Razor template components | P0 | Idea | Typed, testable server-rendered components; support approved legacy template invocation mappings. |
| WIKI-04 | Custom namespaces and page types | P0 | Idea | Namespace identity, per-type editorial rules and migration mappings; avoid collisions. |
| WIKI-05 | Revision history with visual diffs | P0 | Idea | Authorship and immutable history preservation; accessible comparison and rollback. |
| WIKI-06 | Drag-and-drop page and category organization | P2 | Idea | UI layer over explicit ordering/reclassification operations; permission and audit controls. |

## 3. Lore & Worldbuilding

| ID | Feature | Tentative priority | Status | Design notes / acceptance direction |
|---|---|---|---|---|
| LORE-01 | Structured species, character, planet, and civilization databases | P0 | Idea | Typed entities supplement article prose; schema and canon terminology require owner review. |
| LORE-02 | Interactive character ancestry and relationship trees | P1 | Idea | Typed, provenance-aware relations; avoid assuming biological ancestry is universal. |
| LORE-03 | Universe → galaxy → system → planet navigation | P1 | Idea | Hierarchical browsing where canon supports it; tolerate exceptions and nonstandard cosmology. |
| LORE-04 | Canon, draft, speculative, and alternate-timeline tracking | P0 | Idea | Keep editorial publication state separate from canon classification; trace approvals. |
| LORE-05 | Historical timelines with filtering | P1 | Idea | Era calendars, approximate/uncertain dates and cross-universe temporal semantics need design. |
| LORE-06 | Automatic backlinks and relationship discovery | P0 | Idea | Distinguish authored typed relationships from inferred links; show provenance. |

## 4. Media & Galleries

| ID | Feature | Tentative priority | Status | Design notes / acceptance direction |
|---|---|---|---|---|
| MEDIA-01 | Native masonry and category galleries | P0 | Idea | PHP/JS-heavy MediaWiki implementation migrates into .NET SSR + CSS-first rendering. |
| MEDIA-02 | Server-rendered banners with responsive image variants | P0 | Idea | Responsive sizes, alt text, safe caching, art-tier access and asset reuse. |
| MEDIA-03 | Blazor-powered slider galleries | P1 | Idea | Progressive enhancement, keyboard and touch controls, reduced motion; minimal JS interop if needed. |
| MEDIA-04 | Image metadata and reusable collections | P1 | Idea | Provenance, captions, derivatives, licensing, categorization and access policy. |
| MEDIA-05 | 3D model viewer for characters, ships, and artifacts | P3 | Idea | Explicit file formats and budgets; browser rendering requires client code/WebGL, not pure C#. |
| MEDIA-06 | Interactive image comparison and zoom | P2 | Idea | Accessible controls, responsive media derivatives and browser-side enhancement. |

## 5. AI & Developer Tools

| ID | Feature | Tentative priority | Status | Design notes / acceptance direction |
|---|---|---|---|---|
| DEV-01 | Ollama-powered lore assistant | P2 | Idea | Permission-scoped retrieval, source citations, opt-in generation, no automatic canon promotion. |
| DEV-02 | Canon consistency and contradiction detection | P2 | Idea | Produce reviewable findings, evidence and confidence; never silently rewrite canon. |
| DEV-03 | REST APIs for Unity and external tools | P0 | Idea | Versioned DTOs, scoped auth, pagination and source/canon provenance; no mutable game state exposed as lore. |
| DEV-04 | OpenAPI integration for CMS.NET | P1 | Idea | Define concrete read/write boundaries, authentication, namespace permissions and feedback loops. |
| DEV-05 | Bulk imports and structured data exports | P0 | Idea | Idempotent MediaWiki import, dry-run reports, versioned exports and reversible operations. |
| DEV-06 | Extension SDK and developer documentation | P2 | Idea | Begin with internal module contracts; defer dynamic third-party loading until justified. |

## 6. Publishing & Community

| ID | Feature | Tentative priority | Status | Design notes / acceptance direction |
|---|---|---|---|---|
| PUB-01 | Advanced SEO and social metadata | P1 | Idea | Canonical URLs, page descriptions, social previews, structured metadata and redirects. |
| PUB-02 | Featured articles and homepage showcases | P1 | Idea | Editorial curation and reliable scheduling/publishing workflow. |
| PUB-03 | Lore discovery and recommendation engine | P2 | Idea | Transparent related-content rules, tag/graph navigation, optional personalization. |
| PUB-04 | Role-based authoring and approval workflows | P0 | Idea | Preserve author, loremaster, fan and administrator distinctions; enforce authorization server-side. |
| PUB-05 | Reader bookmarks and collections | P2 | Idea | Privacy-conscious account data, opt-in storage and export. |
| PUB-06 | Content maturity settings and media visibility controls | P0 | Idea | Enforce access to restricted assets and derivatives on the server; artwork substitution rules below. |

## 7. Additional wishes — captured verbatim in substance

These are **22 additional owner requests**, each preserved individually even when they overlap with other sections.

| ID | Feature / wish | Tentative priority | Status | Design notes / acceptance direction |
|---|---|---|---|---|
| EXTRA-01 | Project / Studio Blog | P2 | Idea | Editorial posts, categories, feeds, authorship and separate article/blog routing. **Scope expansion:** MDD currently excludes a full CMS. |
| EXTRA-02 | Namespaces | P0 | Idea | Overlaps WIKI-04 intentionally; explicit migration map, title rules and access policies. |
| EXTRA-03 | Emote System | P3 | Idea | Decide where emotes work (comments, forums, profiles) and how moderation handles them. |
| EXTRA-04 | Tooltips and Hover Over Measurement Conversion | P1 | Idea | Accessible focus/tap equivalents; units, precision, locale and conversion source rules. |
| EXTRA-05 | Social Profiles | P2 | Idea | Opt-in public identities, moderation, visibility, account deletion and privacy requirements. **Scope expansion.** |
| EXTRA-06 | Forum | P3 | Idea | Native forum versus integration with an existing forum must be decided; avoid recreating anti-spam and moderation stack casually. **Scope expansion.** |
| EXTRA-07 | More Social Aspects | P3 | Idea | Umbrella discovery item; specify interactions before designing storage or permissions. **Scope expansion.** |
| EXTRA-08 | Groups | P2 | Idea | Clarify authorization groups vs. social/community groups; do not conflate roles with memberships. |
| EXTRA-09 | Article Rating | P2 | Idea | Define whether ratings assess usefulness, quality, enjoyment or canon reliability; prevent brigading. |
| EXTRA-10 | Spam System | P1 | Idea | Rate limits, moderation queues, content filters, auditability and abuse reporting; essential if public input is enabled. |
| EXTRA-11 | Authentik SSO integration | P1 | Idea | OIDC first; map identity and roles deliberately, preserve local recovery paths, test logout and account linking. |
| EXTRA-12 | Themes | P1 | Idea | Accessible, configurable theme tokens and franchise visual identity; distinguish visitor preference from sitewide branding. |
| EXTRA-13 | Variable artwork based on content filter | P1 | Idea | Same article/entry chooses appropriate image variant for content tier, with server-side policy, cache variation and fallback; never leak restricted originals through API or thumbnails. |
| EXTRA-14 | Comments Section | P2 | Idea | Per-page discussion, moderation, threading and permissions; consider interoperability with existing community services. **Scope expansion.** |
| EXTRA-15 | Suggestions Page | P2 | Idea | Structured suggestions, triage states, review/approval and abuse controls; suggestions do not create canon automatically. |
| EXTRA-16 | Newsletter | P3 | Idea | Explicit subscriptions, consent, unsubscribe, deliverability and external mail provider integration. **Scope expansion.** |
| EXTRA-17 | Actually look pretty | P0 | Idea | Deliberate UI design system, strong typography, responsive layouts, accessible contrast and polished galleries; add visual acceptance checks. |
| EXTRA-18 | Semantic Relationships | P0 | Idea | Typed, directed, reversible where appropriate, evidence-backed graph edges; overlaps LORE-06 intentionally. |
| EXTRA-19 | Scribuntu-esque modular code without recompiling the whole project | P2 | Idea | **Discovery spike:** investigate restricted template scripting/configuration or versioned interpreted DSL; never permit arbitrary untrusted C# compilation/execution. See Section 9. |
| EXTRA-20 | WorldAnvil-ish authoring | P1 | Idea | Entity-centric forms, linked prompts, timelines, world navigation and guided drafting, while retaining wiki prose and source. |
| EXTRA-21 | A Story Section | P1 | Idea | Separate long-form canon/fiction works from encyclopedia articles; chapters, series, attribution, maturity and canon labels. |
| EXTRA-22 | Fan-Fiction Section | P2 | Idea | Explicit fan-work/non-canon status, permission and moderation workflows, author attribution and publication terms. **Scope expansion.** |

## 8. Linguistics, IPA, Orthography & Conlangs

| ID | Feature | Tentative priority | Status | Design notes / acceptance direction |
|---|---|---|---|---|
| LING-01 | Native IPA, Orthography and Conlang Integration | P1 | Idea | Franchise-native language registry, IPA phonemic/phonetic transcriptions, multiple writing systems, orthography rules and cross-entity language links. See design exploration below. |

### Proposed scope (discovery, not committed)

- **Language registry:** Languages, dialects, historical stages, language families, speaker populations, associated species/cultures/places, status, creator attribution, and canon provenance.
- **IPA:** Store Unicode IPA transcriptions alongside orthographic forms, explicitly distinguishing phonemic `/.../` from phonetic `[...]` readings; provide keyboard/input aids and accessible rendering. Do not invent pronunciations where none are authored.
- **Orthography and scripts:** Support more than one writing system per language, script direction and glyph/font fallbacks, transliteration schemes, romanization, alternate spellings, and revisioned conventions.
- **Lexicon:** Entries with definitions, parts of speech, pronunciation, morphological notes, etymology, semantic links, examples, usage labels and linked articles. Handle homographs and variant forms without destructive normalization.
- **Grammar references:** Phoneme inventory, phonotactics, inflection/derivation, syntax and reusable paradigms; allow incomplete languages without fabrication.
- **Cross-links and search:** Search by lemma, translation/gloss, IPA or script (subject to Unicode/search limitations); relate phrases and names to characters, species, cultures and articles.
- **Article integration:** Render pronunciation blocks, lexical entries, conjugation/declension tables and hover details via typed Razor components with source citations.
- **Optional audio:** Author-supplied pronunciation recordings and explicit licensed/generated audio with provenance; no assumption that generic TTS correctly speaks fictional languages.
- **Security and portability:** Treat custom fonts and uploads as potentially untrusted; validate media, escape user-authored markup, and version exports for reuse in external Continuum Universes tools.

**Important:** The exact conlangs, orthographies, glyph fonts, transcription conventions and required import formats remain to be inventoried. A full linguistic workbench is not implicitly approved by this wishlist entry.

## 9. Cross-cutting design explorations

### 9.1 Replace the wiki, not all surrounding services by default

The current [MDD.md](MDD.md) explicitly excludes building a full CMS/forum/social network as initial scope. `EXTRA-01`, `EXTRA-05`–`EXTRA-08`, `EXTRA-14`–`EXTRA-16` and `EXTRA-22` may justify expanding that scope, but that is an **owner decision**. Evaluate native modules versus integrations with existing forum, blog, comments, mailing and SSO infrastructure; avoid duplicate identity systems. No specific integration is decided here.

### 9.2 Runtime-configurable / Scribunto-like extensibility

`EXTRA-19` expresses a real need: editable presentation or business-rule modules without rebuilding the entire app. Distinguish:

1. **Data-driven templates** — schemas, component composition and rules stored as validated configuration (potentially sufficient for most cases).
2. **Restricted scripting** — an audited, bounded DSL or sandboxed interpreter for template calculations and formatting.
3. **Compiled C# modules** — deployed versioned assemblies with explicit release/restart rules; appropriate for trusted features but not arbitrary wiki-editable code.

A .NET module architecture does **not** automatically make live C# edits executable without recompilation or redeployment. Investigate security, performance, caching, author permissions, module versioning and compatibility before choosing an approach. Never run user-authored C# or other arbitrary code inside the production web process as an assumed default.

### 9.3 Content-tier-specific art

`PUB-06` and `EXTRA-13` must be designed together. A single entity or article can reference multiple approved artwork variants. The selected variant depends on viewer policy, account preference where allowed, and an explicit fallback order. SSR, APIs, image URLs, cache keys, Open Graph cards, gallery thumbnails and download permissions must all honor content eligibility. Hiding a restricted image in CSS is never sufficient access control.

### 9.4 Social content and lore authority

Stories, fan fiction, comments, suggestions, ratings and AI output **do not become canon through publication or popularity**. Keep editorial review, canon classification, attribution, moderation, and revision/audit trails distinct. Fan works should be visibly labeled as such and should not pollute canonical search/export results unless explicitly requested.

### 9.5 “Pretty” is a product requirement, not a CSS afterthought

Translate `EXTRA-17` into a visual design system: consistent layout grids, typographic scale, rich banners, gallery treatments, mobile/desktop behavior, motion preferences, polished empty/error states, dark/bright theme variants if desired, and screenshot-based review. Continuum Universes branding should inform the system, not generic dashboard components.

## 10. Discovery questions / pending owner decisions

These are unresolved deliberately, not Codex permission to make product decisions:

- Does `Groups` mean membership communities, wiki access roles, or both?
- Should Blog, Forum, Comments and Newsletter be native modules, integrations, or links to existing sites?
- What does `Article Rating` measure, and who is permitted to rate?
- Is the primary editor Wikitext, WYSIWYG, structured forms, or context-dependent combinations?
- Which exact MediaWiki templates and pages are the acceptance corpus for banners, masonry galleries, categories and slider galleries?
- How should Authentik identities map to legacy author/loremaster/fan permissions, including account recovery?
- Which maturity tiers apply to articles versus individual assets, and what fallback image is used when no eligible art exists?
- Which authoring features from World Anvil are desired in concrete terms? Avoid assumptions about the third-party product.
- What capabilities must runtime-editable modules support, and who may publish such scripts/configuration?
- What separates the Story Section from Fan-Fiction (canon, licensing, editorial policy, age restrictions, discoverability)?
- Which existing Continuum languages and orthographies should form the IPA/conlang acceptance corpus, and what transcription and script standards do their authors use?

## 11. Promotion workflow for Codex

1. **Capture:** Add a wishlist row with an ID, feature statement and tentative priority; default status `Idea`.
2. **Investigate:** Create a focused design note or spike with examples, existing MediaWiki behavior, constraints, risks and estimates if useful; status may become `Discovery`.
3. **Approve design:** Product owner decides expected UX, permissions, dependencies, and acceptance criteria; mark `Approved for design` only on explicit approval.
4. **Commit:** Update the MDD scope/requirements and repository issue/backlog; write acceptance tests and migration impact; then mark `Committed` on explicit approval.
5. **Implement:** Codex follows `AGENTS.md`, works in narrowly scoped vertical slices, runs tests, and reports any conflicts without silently changing architecture or lore.

**Codex instruction:** Do not implement a feature merely because it appears in this wishlist, including P0 items. Use this document to guide planning and uncover product-owner decisions. For approved work, the MDD and a specific task/issue are the execution authority.

## 12. Counts and preservation audit

| Source | Captured entries |
|---|---:|
| Wiki & Editing | 6 |
| Lore & Worldbuilding | 6 |
| Media & Galleries | 6 |
| AI & Developer Tools | 6 |
| Publishing & Community | 6 |
| Additional wishes | 22 |
| Linguistics, IPA, Orthography & Conlangs | 1 |
| **Total** | **53** |

**Intentional overlaps preserved:** Namespaces (`WIKI-04` / `EXTRA-02`); relationships (`LORE-06` / `EXTRA-18`); maturity controls (`PUB-06` / `EXTRA-13`). These may ultimately share implementations but should not be deleted from the wishlist without owner approval.
