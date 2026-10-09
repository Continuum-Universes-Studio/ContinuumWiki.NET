# AGENTS.md — ContinuumWiki.NET

> Authoritative instructions for coding agents operating in this repository.
> Product scope: a **Continuum Universes–specific** replacement for the existing MediaWiki wiki, not a general-purpose wiki framework.
> Pair with [MDD.md](MDD.md). If requirements conflict, **stop and report the conflict**; do not silently invent canon or alter existing content.

## 1. Mission and boundaries

Build ContinuumWiki.NET as a production-quality, franchise-native lore encyclopedia, authoring environment, and structured content platform. It must support existing Continuum Universes articles and media, and ultimately replace the current MediaWiki deployment **only after migration acceptance gates pass**.

The public encyclopedia is central, not an afterthought. Structured entities complement article prose; they do not erase or overwrite it. Continuum Universes lore is authoritative for lore; gameplay models consume curated lore through explicit conversion, not automatic runtime coupling.

**Do not** implement a general-purpose hosted wiki service, cross-tenant SaaS, arbitrary third-party MediaWiki extension compatibility, or a whole replacement in a single PR.

## 2. Technology constraints

- Target **.NET 10** for every application, library, tool, and test project. Use C# with nullable reference types enabled.
- ASP.NET Core for HTTP, Razor components/static server-side rendering for public pages, Blazor interactivity only where useful.
- **MySQL only** as the transactional datastore. Do not add SQLite or MariaDB providers. Use a .NET 10-compatible EF Core + MySQL provider after checking package compatibility; pin versions centrally. Keep provider-specific operations isolated.
- Use OpenSearch only as an optional, rebuildable search index; MySQL remains authoritative.
- Prefer server rendering, semantic HTML and CSS. Use JS only for behaviors that require browser APIs or materially improve UX; isolate JS interop.
- Use dependency injection, asynchronous cancellation-aware I/O, structured logging, options validation, explicit database migrations, and testable services.
- Prefer a **modular monolith** with feature modules; no premature microservices or dynamic plugin loader.
- Linux/openSUSE-friendly development and deployment. Prefer Podman examples for containers. Never depend on Windows-specific paths or tooling.

## 3. Domain and canon integrity

- Separate: (a) article Wikitext/source, (b) parsed/rendered output, (c) typed lore records, (d) relationships, (e) media, (f) editorial/canon status, and (g) game-derived export projections.
- Never silently promote draft, speculative, non-canon, game adaptation, or alternate-timeline material into established canon.
- Model canon status explicitly and show it in editorial workflows. Preserve provenance, citations, changes, and editorial ownership.
- Strongly typed relations should reference entity IDs internally, not unvalidated names. Resolve wiki titles, slugs and redirects at the boundary.
- Every persistent change should be auditable. Revisions must preserve authorship, timestamps, original source, and safe rollback.
- Schema types such as Species, Character, Planet, Civilization, Organization, Universe, and Event are initial candidates, not permission to impose new lore ontology without review.
- **Existing MediaWiki Wikitext remains a supported source format.** Support an explicit, tested subset; report unsupported parser functions/modules/templates rather than dropping or changing them silently.

## 4. Security and content safety

- Default deny on write/admin operations. Use ASP.NET Core authorization policies and object-level permission checks, including on REST endpoints and media.
- Prevent XSS: never render untrusted Wikitext or imported HTML as trusted markup; sanitize allowlisted HTML after parsing and before output.
- Prevent SSRF, path traversal, malicious uploads, image decompression abuse, SQL injection, and unbounded category/transclusion recursion.
- Enforce file type inspection, size limits, image processing limits, rate limits, pagination, anti-forgery controls for cookie-authenticated mutations, and request validation.
- Keep secrets in environment variables or dedicated secret stores; never commit secrets, credentials, or real production dumps.
- Media can have access/content-tier restrictions. Enforce them server-side, not solely by hiding UI elements.
- Preserve accessibility: keyboard interaction, semantic controls, captions/alt text, and reduced-motion preferences.

## 5. Architecture and project organization

Suggested initial structure (adapt if a better tested structure exists; do not rename existing files casually):

```text
src/
  ContinuumWiki.Web/             # ASP.NET Core entry, pages, endpoints, DI
  ContinuumWiki.Domain/          # Pure domain types, invariants
  ContinuumWiki.Application/     # Use cases, permissions, interfaces
  ContinuumWiki.Infrastructure/  # EF Core, MySQL, files, caching, external services
  ContinuumWiki.Rendering/       # Wikitext parsing, template dispatch, sanitization
  ContinuumWiki.Features.Lore/   # Lore schemas, relationships, canon workflows
  ContinuumWiki.Features.Media/  # Media assets, galleries, banners, slider
  ContinuumWiki.Migration/       # Read-only MediaWiki import and reports
  ContinuumWiki.Contracts/       # Versioned API/export DTOs

tests/
  ContinuumWiki.UnitTests/
  ContinuumWiki.IntegrationTests/
  ContinuumWiki.RenderingTests/
  ContinuumWiki.MigrationTests/
docs/
```

Modules may be folders or separate projects; select the simplest design maintaining clear dependencies. Domain code must not depend on ASP.NET, EF Core, Blazor, or MySQL. No mutable global singleton state. Persist both raw source and normalized entities where useful; use clearly defined synchronization rules.

## 6. Editing and publishing

- Prioritize **Wikitext-first compatibility** for migration and existing author habits. A richer structured form editor is an additional authoring experience, not a destructive replacement.
- Preserve page revisions; support draft, review, published and archival states separately from canon classification.
- Render infoboxes from typed schemas while preserving compatible legacy template invocations or mapping them with explicit migration rules.
- Galleries, masonry/category gallery, banners and slider galleries are first-class features. Server-render HTML and CSS first; JS only where needed.
- Support clean titles, redirects, categories, file pages, internal links, namespaces relevant to existing content, metadata/SEO, citations, and backlinks.
- Never change public canonical URLs or remove redirect support without a migration map and redirect tests.

## 7. MediaWiki migration policy

- MediaWiki **stays live** until a documented go/no-go decision after validation. Treat the source MediaWiki database and uploads as **read-only** during import.
- Create repeatable, resumable migration commands with idempotent mapping of pages, revisions, authors, timestamps, links, categories, redirects, templates, and media.
- Use import checkpoints, dry-run mode, collision reports, unsupported-syntax reports, hash validation for media and sampled rendering diffs.
- Never edit or delete source MediaWiki data. Never invent missing revision metadata.
- Test at least one representative Species, Character and Planet page, plus a gallery-heavy page, before accepting the renderer.
- No cutover until all MDD acceptance gates are met, a tested rollback plan exists, and the product owner explicitly approves.

## 8. Delivery workflow for Codex

1. Inspect `MDD.md`, this file, existing source, tests, migrations and relevant designs before touching code.
2. State the exact vertical slice, assumptions, dependencies and non-goals. Flag contradictory requirements.
3. Implement the smallest complete production-shaped slice: domain, use case, persistence, presentation/API, authorization, error handling, tests and documentation.
4. Use descriptive C# types and names. Avoid stringly typed domain state; slugs/IDs in transport and persistence boundaries are acceptable.
5. Add unit tests for invariants, integration tests for persistence/permissions, rendering snapshots or golden fixtures for Wikitext, and migration tests for idempotency.
6. Run `dotnet format --verify-no-changes`, `dotnet build`, and `dotnet test` when feasible. Report commands and actual outcomes honestly.
7. Include migration impacts, deployment/config changes, security considerations, and a manual smoke-test checklist in the PR summary.
8. Do not refactor unrelated areas, introduce placeholder production behavior, or silently change canon, templates, article text, URLs, or data schemas.

## 9. Definition of done

A task is done only when its feature functions end-to-end, has tests for success/failure paths, applies authorization consistently, has error/logging behavior, documents configuration, and does not regress representative legacy content. Mark anything not implemented as **Not implemented**; no fake completion.

## 10. Decisions requiring product-owner approval

- Removing or restricting any existing Wikitext syntax used by the wiki.
- Changing entity ontology or canon/editorial status meanings.
- Changing existing public URLs, redirects, namespaces, user attribution, or permissions.
- Introducing external services, JS-heavy frameworks, or new data stores.
- Replacing Wikitext-first editing with a purely block-based editor.
- Production migration/cutover, retention/deletion policies, or public launch.

## 11. First authorized milestone

Implement the foundation and **read-only page rendering** with a minimal supported Wikitext subset and diagnostic reporting. Do not generate a full wiki engine at once. Use the milestone ordering in `MDD.md`.
