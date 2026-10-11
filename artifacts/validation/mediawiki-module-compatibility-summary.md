# CW-M3-006C compatibility summary

Status: **PARTIAL — real export inventory and disposable import verified; parity unevaluated**

| Measure | Result |
|---|---:|
| Inventory report | `cw-m3-006c-v1` |
| Real export | `continuumwiki-full-with-files.xml` (`5a6d6ad1...a38afd`) |
| Modules | 30 |
| Revisions | 89 |
| Invocation sites | 2,187 real page/template usages; 31 documentation examples |
| Representative cases | 8 (`mediawiki-compatibility-corpus-v1.json`) |
| Reference comparisons | 0 |
| Socket executions | 2 passed, 0 failed, 0 skipped |
| Storage-backed render | 1 passed, 0 failed, 0 skipped |
| MariaDB import gate | Verified: 4 selected modules / 19 revisions |
| Production Lua | Disabled |

The inventory and compatibility JSON reports are deterministic for the real
offline export. Static evidence found missing dependencies, dynamic loading in
`Module:Message box` and `Module:Params`, and unsupported references including
`mw.ext`, `mw.getCurrentFrame`, and `os.*`. These are reported as gaps or
unsupported surfaces, not compatibility passes.

The disposable import harness imported actual selected module pages and
revisions through the normal planner/importer and reported `VERIFIED`.
An authorized socket-backed probe was run after the worker became reachable.
The disposable import and storage-backed historical-resolution/render test
passed. The existing socket-backed integration suite separately passed 2 tests
with 0 failures and 0 skips. These are execution results for
synthetic/transport coverage, not real-module parity evidence.

Deployment parity is now verified: the deployed worker and launcher match the
reviewed manifests. The bounded synthetic storage/render smoke gate is also
verified.

The eight selected real-module cases therefore remain `NOT_EVALUATED`. No
independent MediaWiki reference output was available, so no exact,
equivalent, or partial parity claim is made.

No independently captured MediaWiki output was available. Therefore the
representative cases remain `NOT_EVALUATED`; no Scribunto parity claim is
made. See `mediawiki-compatibility-comparison-v1.json` for the structured
counts and required evidence.
