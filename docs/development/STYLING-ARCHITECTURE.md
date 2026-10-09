# Styling architecture

ContinuumWiki.NET keeps Bootstrap and TightWiki's existing theme resources intact. New application-owned styling is authored in LESS and compiled ahead of runtime into `TightWiki/wwwroot/css/continuum.css`.

## Structure

```text
TightWiki/Styles/
├── continuum.less          # single application entry point
├── core/                   # variables, runtime tokens, typography, layout
├── components/             # cw-prefixed application components
└── themes/                 # integration defaults for existing themes
```

The generated stylesheet is committed so production publishing and deployments do not require Node.js. It is served after TightWiki's existing `base.css`; Bootstrap and database-selected theme styles remain responsible for their existing components.

## LESS conventions

Use `cw-` prefixes for new application-owned classes. Put compile-time defaults and calculations in `core/variables.less`; expose runtime values through CSS custom properties in `core/tokens.less`. Keep imports centralized in `continuum.less`. Modify LESS sources, not generated CSS.

Current tokens cover page, surface, elevated-surface, text, border, accent, hover, focus, spacing, radius, and typography values. Defaults reference Bootstrap custom properties, so existing light, dark, gray, and custom themes can provide their own values without a second theme-selection system.

## Commands

From the repository root:

```bash
npm ci
npm run build:css
npm run check:css
npm run watch:css
```

`build:css` writes the committed asset. `check:css` compiles to a temporary file and fails on LESS errors. `watch:css` uses the small `less-watch-compiler` development dependency; no watcher is needed at application runtime.

## Razor integration

The shared layout loads `continuum.css` after `base.css` and applies the `cw-branding` class only to the existing application-branded navigation element. This is the demonstration component and intentionally avoids global redesign or Bootstrap duplication.

## Build and publish policy

Node.js is a development/build prerequisite only. The generated `wwwroot/css/continuum.css` is checked in and included by the normal ASP.NET Core static-content pipeline, so `dotnet build`, `dotnet run`, and `dotnet publish` do not invoke Node.js or require npm at runtime. When LESS changes, run `npm run build:css` and commit the resulting stylesheet with the source changes.

## Troubleshooting

- If `npm run check:css` fails, inspect the reported LESS source and rerun after fixing it.
- If styling appears stale, rebuild the stylesheet and clear the browser cache; the Razor layout applies ASP.NET file-version hashes.
- If a theme does not expose Bootstrap variables, the token fallbacks in `tokens.less` provide usable defaults without changing the theme selection or database configuration.
