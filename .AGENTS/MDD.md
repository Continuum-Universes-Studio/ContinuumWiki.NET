# ContinuumWiki.NET — Master Design Document (MDD)

**Status:** Proposed design baseline — not finalized canon or implementation contract  
**Version:** 0.1  
**Product owner:** Christian Daniel Jensen  
**Product:** ContinuumWiki.NET  
**Purpose:** A Continuum Universes–specific replacement for the existing MediaWiki wiki  
**Companion:** [AGENTS.md](AGENTS.md)

## 1. Executive summary

ContinuumWiki.NET is a purpose-built wiki, structured lore registry, media publishing system and authoring platform for the **Continuum Universes** franchise. It is **not** intended to be a generic wiki engine or multi-tenant platform. It should replace the existing MediaWiki site only when the content, publishing workflows, history, redirects, permissions and media can be migrated safely and verified.

The product unites human-readable encyclopedia articles with structured, cross-referenced knowledge about universes, worlds, species, characters, civilizations and events. Structured lore data can be exported through versioned APIs to related tools and games, but lore is not directly coupled to gameplay state.

### Product principles

1. **Content preservation before reinvention.** Existing articles, revision history, uploads, categories, redirects, citations and authorship must survive.
2. **Franchise-first design.** Prioritize Continuum Universes concepts and editorial workflows over generic wiki flexibility.
3. **C#-first engineering.** Put server functionality in .NET 10; keep browser JS minimal and contained.
4. **Author-friendly editing.** Retain supported Wikitext and compatible template syntax; add typed forms without destroying prose.
5. **A single source of truth with explicit provenance.** Never conflate a rendered article with a canonical entity record or assume game rules are canon.
6. **Migration without downtime pressure.** Develop alongside existing MediaWiki; require measurable cutover gates and rollback.
7. **Modular monolith, clear boundaries.** Avoid premature distributed systems.

## 2. Goals and non-goals

### Goals

- Present an attractive, fast, accessible and SEO-friendly public encyclopedia.
- Support Wikitext source, internal wiki links, redirects, categories, references, media, templates and revisions for the subset actually used in the current site.
- Introduce typed lore entities, backlinks and traversable relationships, with article/editorial context preserved.
- Implement native infoboxes, banners, masonry/category galleries and image sliders.
- Expose secure, versioned content APIs for Continuum Universes applications and export tooling.
- Support lore review, canon status, author/loremaster/fan permissions and editorial accountability.
- Migrate from MediaWiki with verifiable content/history fidelity and no destructive source operations.

### Non-goals

- Reimplement every MediaWiki parser feature, extension, skin or API.
- Support arbitrary third-party users deploying their own wiki installations.
- Become a full CMS/commerce/forum/social network or a game runtime.
- Automatically generate gameplay ScriptableObjects or mutable campaign data from lore records.
- Replace every existing site/service of the Continuum Universes ecosystem.
- Require a JavaScript-free browser; native browser behavior and small interop are permitted.

## 3. Audiences and roles

| Role | Primary needs | Proposed permissions (validate against current wiki) |
|---|---|---|
| Public reader | Read, search, navigate, view accessible media | Read published content |
| Fan | Discussions / talk participation | Read + permitted discussion features |
| Author | Draft articles, revise content, upload allowed media | Create/edit pending-review content |
| Loremaster | Editorial oversight, canon review | Review, approve/publish, manage content relationships |
| Administrator | Users, permissions, system health | Administrative/configuration operations |

Implement policies per operation and content object. **Proposal:** author edits require loremaster approval before becoming publicly published where appropriate; exact migration of existing user groups must be assessed before implementation.

## 4. Information architecture

### 4.1 Content layers

| Layer | Responsibility | Primary record |
|---|---|---|
| Page | Title, namespace, slug, redirects, editorial state | `WikiPage` |
| Revision | Immutable authored source + author/time | `PageRevision` |
| Render | Cached HTML/AST from source, template and media versions | `RenderedPage` cache |
| Lore entity | Structured fact model, type, canonical identity | `LoreEntity` + typed records |
| Relation | Typed directed relationship and provenance | `LoreRelationship` |
| Media | Uploads, metadata, derivatives, access tier | `MediaAsset` |
| Classification | Categories, tags, chronology and editorial canon status | Classification records |
| Projection | Safe API/export representations | Versioned DTOs |

A page may describe one or more entities, and an entity may have multiple relevant pages. Do **not** assume a universal one-page-to-one-entity mapping.

### 4.2 Initial entity candidates

- `Universe`, `Realm`, `Dimension`, `Planet`, `StarSystem`
- `Species`, `Subspecies`, `Character`, `Civilization`, `Organization`
- `Event`, `Era`, `Technology`, `Artifact`, `Religion` (as future schema candidates)

All types require product-owner confirmation against existing wiki nomenclature and templates before schema lock. Prefer entity-specific definitions over arbitrary JSON blobs for durable core relationships; optional extensible attributes can be versioned.

### 4.3 Two independent status dimensions

**Editorial status:** Draft → In Review → Published → Archived (with rejection/rework transitions).  
**Canon classification:** Confirmed canon / Draft canon / Speculative / Alternate timeline / Game interpretation / Undetermined.

Do not confuse published with canon. A published article can intentionally document non-canon material.

## 5. Core functional modules

### 5.1 Encyclopedia / pages

- Namespaces as needed for existing content; clean readable titles and permanent redirects.
- Page creation, edit preview, compare revisions, revert, watch/track changes, history and attribution.
- Redirects, categories, backlinks, wanted pages, broken links, disambiguation.
- References/citations and external-link safety controls.
- Public cached HTML with invalidation when pages, templates, linked media or relation projections change.

### 5.2 Wikitext and template compatibility

**Strategy:** incremental compatibility for the actual MediaWiki corpus, not a claimed perfect clone. Persist original Wikitext unchanged on import. Build a tokenizer/AST/parser with guarded expansion and diagnostic nodes for unsupported syntax. Create a corpus-based compatibility suite with fixture inputs and expected rendering.

First syntax priorities:

1. Headings, emphasis, paragraphs, lists, tables, escapes/nowiki and comments.
2. Internal and external links; file links, thumbnails/captions, categories and redirects.
3. Templates with named/positional parameters, defaults and nesting.
4. Common parser functions and extension features **only after corpus inventory**.
5. References and representative infobox, banner, gallery and slider template invocations.

Do not silently remove unknown constructs. Show them in an admin diagnostic report; preserve original source for future recovery. Enforce recursion/expansion limits and sanitize rendered HTML.

### 5.3 Structured lore registry

- Typed schemas, validations, relationship types, inverse traversal, provenance and citation links.
- Relationships such as Species–native-to–Planet and Character–member-of–Organization are **examples**, not canonical ontology decisions.
- Entity identity is stable even when page titles change.
- Editorially reviewable modifications; typed forms and side-by-side raw source where mappings apply.
- A relationship engine can generate related-content modules and automatic lists without hardcoded Wikitext duplication.
- Explicit synchronization ownership: determine whether a field is managed by structured forms or imported from a template; never allow ambiguous bidirectional writes.

### 5.4 Infoboxes and banners

- Use the existing ContinuumInfobox templates/extension as **migration input**, not as a runtime dependency.
- Schema-defined field display rules, validation, localization-ready labels, links, image presentation and accessibility.
- Retain documented template invocation compatibility when feasible via render adapters.
- Banners are server-rendered configurable components with controlled styles and sensible fallbacks.

### 5.5 Media and gallery engine

- Media asset registry with original files, metadata, ownership, alt text, thumbnail variants, checksums and access restrictions.
- **Masonry gallery:** CSS-first responsive layout; preserve aspect ratio and keyboard accessibility.
- **Category gallery:** paginated server-side queries over category membership, configurable sort/filter.
- **Slider gallery:** server-render slides, CSS scroll snap, semantic controls; C#/Blazor for app state where useful, isolated JS for genuinely browser-dependent gestures or measurements.
- Lazy-loading, responsive image sizes, content tier filtering and robust cache invalidation.
- Do not equate “pure PHP” or “pure C#” with zero browser technology; CSS/HTML and occasional JS interop remain normal.

### 5.6 Search and discoverability

- Search articles, entities, categories and media with permission-aware results.
- MySQL fallback search for initial MVP; optional rebuildable OpenSearch index when justified.
- Facets by type, universe, category and status where public and indexed.
- Canonical URLs, redirects, sitemap, robots directives, Open Graph and metadata output.

### 5.7 Identity, permissions and moderation

- ASP.NET Core Identity plus policies for editing, approval, upload, restricted media and administration.
- Map historical contributors where possible; preserve original usernames/identifiers on imported revisions, even if accounts are not automatically activated.
- Rate limits, anti-abuse controls, audit logs and review queues.
- Public discussion/talk support can be staged based on actual dependency inventory; no destructive assumption that every legacy feature can be immediately replaced.

### 5.8 APIs and integrations

- Versioned read APIs, e.g., `/api/v1/pages/{id}`, `/api/v1/lore/entities/{id}`, `/api/v1/media/{id}`.
- Stable IDs, ETags/caching, authorization, pagination and rate limits.
- Export profiles for other Continuum Universes tools and games, with provenance, data version and schema version.
- Distinct **lore source → explicitly authored game data → mutable runtime state** boundaries.

## 6. Technical architecture

### 6.1 Technology baseline

| Area | Proposed technology |
|---|---|
| Language/runtime | C# / .NET 10 |
| Web | ASP.NET Core; Razor components + static SSR |
| Interactivity | Blazor where justified; isolated JS interop |
| Persistence | MySQL with compatible EF Core provider |
| Search | MySQL MVP; OpenSearch optional |
| Media | File/object abstraction with local storage initially |
| Caching | Memory/distributed cache as required; explicit dependency invalidation |
| Authentication | ASP.NET Core Identity + authorization policies |
| Testing | xUnit or equivalent; integration DB tests; parser golden fixtures |
| Deployment | Linux/openSUSE; systemd or Podman; Apache reverse proxy if desired |

Package versions and exact EF Core provider choice must be validated in a real build; this document does not assert a specific provider version has been tested.

### 6.2 Dependency direction

```text
Web / API / Razor
      ↓
Application use cases ← Feature modules
      ↓
Domain and contracts
      ↑ (implements interfaces)
Infrastructure (MySQL, media, search)

Rendering consumes page/source/media/template contracts;
Migration writes through controlled import services.
```

Avoid sharing DbContext entities directly with public API contracts or Blazor components. Treat imported Wikitext as untrusted input.

### 6.3 Suggested aggregates / key tables

- `Pages`, `PageRevisions`, `PageRedirects`, `Namespaces`
- `Templates` or template-backed pages, `RenderDependencies`, `RenderDiagnostics`
- `LoreEntities`, `LoreEntityTypes`, `LoreRelationships`, `LoreFactProvenance`
- `Categories`, `PageCategories`, `PageLinks`
- `MediaAssets`, `MediaVariants`, `PageMediaUses`
- `EditorialReviews`, `CanonClassifications`, `AuditEvents`
- `ImportRuns`, `ImportMaps`, `ImportIssues`, `ImportCheckpoints`

These names are architectural sketches, not locked schemas. Concurrency tokens, uniqueness constraints, foreign keys, auditing and indexing must be designed before initial migrations.

## 7. Editing experience

**Baseline decision:** Wikitext-first migration/editor support plus progressive typed forms. A block/visual editor is optional and **not an excuse to lose source fidelity**.

Target editor layout: source or visual preview; template parameter guidance; structured lore pane; validation diagnostics; changes/diff; cite/source prompts; review submission. Public users need not download complex interactive editor assets.

## 8. Migration from MediaWiki

### 8.1 Discovery (required before implementation estimates)

Inventory:

- Actual MediaWiki version, database schema, namespaces, user groups, content models and page count.
- Templates and Lua modules ranked by transclusion frequency.
- Parser functions and installed extensions that affect rendered content.
- Media files, image variants, galleries and content tier restrictions.
- Redirects, categories, citations, SEO slugs, external backlinks and canonical URLs.
- Discussion pages, revision histories, deleted/suppressed revisions and retention obligations.

### 8.2 Import pipeline

1. **Read-only extract** from MySQL dumps/API and upload storage copies.
2. **Preserve** raw Wikitext, revision metadata, identifiers, redirect/category/page mappings and media checksums.
3. **Transform** through idempotent staging records, with explicit unresolved mappings.
4. **Validate** item counts, duplicates, history chains, UTF-8/Unicode fidelity, files and link resolution.
5. **Render** a representative corpus and compare output meaning, links, images and structure.
6. **Re-run** import incrementally to support an updated source snapshot before cutover.
7. **Cut over only after approval** and tested redirects/rollback.

### 8.3 Non-negotiable cutover gates

- [ ] All in-scope published pages imported with original source retained.
- [ ] Revisions and attribution preserved to the level agreed in writing; any gaps enumerated.
- [ ] Redirect and public URL compatibility verified with a crawl/map.
- [ ] Uploaded media copied and checksum validated, with access rules correctly enforced.
- [ ] Representative template, category, reference and gallery pages pass acceptance review.
- [ ] No unexplained unsupported syntax on critical pages; known exceptions documented.
- [ ] Permissions, editorial approvals and restricted media tested.
- [ ] Backup restore and rollback drill performed successfully.
- [ ] Product owner explicitly approves launch.

## 9. Implementation roadmap (Codex-sized vertical slices)

| Milestone | Deliverable | Exit test |
|---|---|---|
| M0 — Inventory | Corpus/templates/extensions/media/URL report | Quantified coverage and blockers |
| M1 — Skeleton | Compiling .NET 10 app, MySQL setup, health checks, CI/tests, module seams | Reproducible build + tests |
| M2 — Read-only wiki | Page/revision models, routes, parser subset, safe renderer | Sample pages display with diagnostics |
| M3 — Identity/editorial | Login, role policies, edit/review/revision flow | Author cannot self-approve; rollback works |
| M4 — Templates/infoboxes | Template expansion, typed infobox renderer, legacy adapter | Selected real infobox pages preserve content |
| M5 — Media suite | Upload registry, banners, masonry, category gallery, sliders | Accessible responsive pages and access tests |
| M6 — Lore registry | Typed entities, relations, canon status, editorial forms | Queryable relationships with provenance |
| M7 — Discoverability/APIs | Search, sitemap, structured APIs, export contracts | Permission-aware search/API responses |
| M8 — Migration hardening | Full dry runs, exception reports, incremental import, performance tests | Cutover gates demonstrated |
| M9 — Cutover | Approved production deployment + rollback capability | Verified traffic, links, backups and recovery |

Milestones can overlap selectively but **M0/M1 evidence gates should precede architectural commitments** to broad parser compatibility. This is active development planning, not a promise that implementation begins at a later unspecified time.

## 10. Quality requirements

- **Correctness:** no silent data loss; deterministic parser behavior; documented unsupported syntax.
- **Reliability:** database migrations reversible or backed by a tested restore path; safe retries and idempotent imports.
- **Performance:** server-rendered public pages, bounded template expansion, paginated galleries, cache invalidation.
- **Security:** XSS/CSRF/SSRF protection, content-aware access checks, upload validation, safe authorization.
- **Accessibility:** keyboard navigation, reduced-motion behavior, semantic article structure and descriptive media.
- **Operations:** readiness/health endpoints, structured errors/logs, backups, import telemetry and index-rebuild tools.
- **Testing:** domain unit tests, HTTP integration tests, permission matrix, rendered fixture regressions, migration replay tests.

Performance targets (latency, corpus size, indexing SLAs) are **TBD after source inventory and baseline profiling**. Do not invent benchmarks.

## 11. Product decisions to resolve

1. Exact existing template names, parameter contracts and top-transcluded pages (including ContinuumInfobox).
2. Existing gallery/slider extension behavior to preserve versus simplify.
3. Which parser functions, Lua modules and extensions are essential for compatibility.
4. Scope of talk/discussion features and forum integration.
5. Whether the primary editing UX remains Wikitext or becomes hybrid with structured forms (baseline assumes hybrid support).
6. Media content/access tier rules and whether existing login state must migrate.
7. Initial lore schema and editorial authority for canon classification.
8. Canonical URL conventions and current SEO metadata dependencies.
9. Hosting isolation, filesystem/object storage, and search-index timing.
10. The exact staged cutover and rollback procedure.

## 12. First Codex task (copy into a coding run)

**Context:** This repository implements ContinuumWiki.NET using this MDD and `AGENTS.md`.

**Goal:** Create M1 skeleton and the smallest M2 read-only page vertical slice: a .NET 10 ASP.NET Core application, MySQL persistence, a page + immutable revision domain model, page lookup endpoint, safely escaped/basic-Wikitext server-rendered view, parser diagnostics for unsupported constructs, and tests.

**Hard constraints:** No general wiki framework, no unreviewed schema/canon invention, no mutable gameplay state, no SQLite/MariaDB, no unnecessary JS, no destructive source migration, no fake parser compatibility.

**Files to inspect:** `AGENTS.md`, `MDD.md`, existing solution/project files and relevant tests (if any).

**Files to create/modify:** Smallest coherent solution/projects; configuration examples; initial migrations; tests; README with local setup and commands.

**Implementation:** (1) scaffold architecture and DI; (2) add entities and EF mapping; (3) route `/wiki/{**title}` with safe lookup and redirect handling stub clearly marked as not implemented if necessary; (4) implement basic heading/paragraph/emphasis and escaped text rendering using test fixtures; (5) show unsupported constructs as editor/admin diagnostics; (6) add permission-safe read APIs and automated tests.

**Acceptance criteria:** `dotnet build` and `dotnet test` pass, app starts against configured MySQL, a seeded page renders with title and basic formatting, revision source remains unmodified, and unsupported template syntax produces diagnostics rather than silently disappearing.

**Do not do:** Implement the entire MediaWiki parser, custom runtime plugin loading, generalized SaaS tenancy, game runtime coupling, or production cutover.

---

**Document policy:** Mark final decisions as **Accepted** only after product-owner approval. New ideas stay **Proposed**. Maintain an explicit change log as implementation and source-wiki inspection clarify requirements.
