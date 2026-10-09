# Repository development guidance

For application-owned styling in ContinuumWiki.NET, use LESS under `TightWiki/Styles/` and the existing `cw-` naming convention. Modify LESS sources rather than the generated `TightWiki/wwwroot/css/continuum.css`, then run `npm run check:css` and `npm run build:css` for styling changes.

Preserve Bootstrap, the existing TightWiki theme selection/resources, and runtime CSS custom-property integration. Avoid unnecessary global selectors, Sass, Tailwind, new JavaScript frameworks, unrelated CSS conversion, and database or route changes.
