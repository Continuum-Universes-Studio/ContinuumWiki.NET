# MoonSharp Scribunto compatibility foundation (CW-M3-005B.3)

## Scope and verdict

`CW-M3-005B.3 VERIFIED — EXPERIMENTAL`

This document describes the offline compatibility foundation implemented in
the disposable `ScribuntoWorker`. It is not a production-sandbox approval and
does not change the separate `CW-M3-005B BLOCKED` operating-system verdict in
`MEDIAWIKI-SCRIBUNTO-RUNTIME-EVALUATION.md`. The normal application renderer
still uses `RejectingMediaWikiScribuntoExecutor`; no startup registration or
production module execution was added.

The compatibility target is the MediaWiki Scribunto Lua API. The official
[Scribunto Lua reference manual](https://www.mediawiki.org/wiki/Extension:Scribunto/Lua_reference_manual)
documents `mw.text`, `mw.ustring`, frame arguments, and the distinction between
Unicode-safe string helpers and Lua pattern matching. MoonSharp is therefore
treated as an implementation substrate, not as proof of Scribunto parity.

## Typed worker protocol and dependency bundle

`MediaWikiScribuntoWorkerProtocol` is shared by the host and worker. One worker
process accepts one request line and emits one response line. stdout contains
only the JSON response; diagnostics and unexpected failures are represented in
the response rather than printed as logs.

The request envelope contains:

| Field | Contract |
|---|---|
| `protocolVersion` | Required version `1`; other versions are rejected |
| `correlationId` | Required bounded ASCII-safe request identity; response must echo it |
| `moduleTitle` | Canonical module title used for interpreter diagnostics |
| `moduleSourceIdentity` | Provenance-safe module/revision identity |
| `sourceWikiIdentity`, `sourceRevisionId` | Typed source provenance |
| `source`, `function` | Module source and exported function |
| `positionalArguments`, `namedArguments` | String-only arguments; missing and empty values remain distinct |
| `budget` | Source, count, aggregate/individual argument, result, dependency, input/output, and instruction limits |
| `capabilities` | Explicit allow-list for frame metadata, controlled module loading, and the restricted `mw` APIs |
| `dependencies` | Bounded pre-resolved module entries with source wiki, canonical title, page/revision identity, content model, SHA-256, source, and explicit child edges |
| `rootDependencies` | Explicit child edges for the root module; no implicit search path is used |
| `frameTitle` / parent fields | Trusted host invocation metadata used by frame title methods |

The parser rejects unknown or duplicate envelope fields, malformed JSON,
non-string arguments, duplicate or case-ambiguous named keys, invalid function
names, unsupported capabilities, oversized payloads, non-positive budgets, and
unexpected second messages. Responses contain the version, correlation ID,
status, bounded text, structured diagnostics, and an optional resource-limit
outcome. Supported statuses are `Succeeded`, `Failed`, `ResourceLimit`,
`ProtocolError`, `SandboxUnavailable`, and `Terminated`.

Diagnostic codes intentionally avoid host paths, secrets, and raw exception
details. Current codes include `LuaSyntaxError`, `LuaRuntimeError`,
`ExportNotFound`, `UnsupportedApi`, `InstructionLimitExceeded`,
`ResultLimitExceeded`, `ArgumentLimitExceeded`, `IndividualArgumentLimitExceeded`,
`UnsupportedCapability`, `DuplicateOrInvalidNamedArgument`, and protocol,
worker-start, timeout, and resource-limit failures.

## Supported compatibility surface

The worker creates a fresh `MoonSharp` interpreter with
`CoreModules.Preset_HardSandbox` for every request. The following surface is
available when the corresponding capability is present:

| API | Implemented behavior | Deliberate difference or bound |
|---|---|---|
| `frame.args` | Positional keys start at `1`; named keys retain their text; missing keys are `nil`; empty values remain empty strings; Unicode is preserved | The table is a request-local, scalar-only table; no CLR collections or host callbacks are exposed |
| `frame:getTitle()` | Returns trusted invocation page metadata when `frame.title` is allowed | It is not derived from Lua or a live title service |
| `frame:getParent()` | Returns a bounded parent frame with trusted title when `frame.parent` metadata is unambiguous | Parent arguments and unsupported relationships are not synthesized |
| `mw.text.trim` | Unicode-aware .NET trim of a string | Site-specific whitespace policy is not loaded |
| `mw.text.split` | Literal separator split, including empty fields | Ustring pattern mode is explicitly rejected as `UnsupportedApi` |
| `mw.text.nowiki` | Escapes HTML/Wikitext metacharacters deterministically | Context-sensitive MediaWiki parser state is not emulated |
| `mw.text.encode` | HTML entity encoding; named entities for `<`, `>`, `&`, `"`, and NBSP, numeric entities for other selected characters | Optional charset is a bounded literal character set, not a general Ustring pattern |
| `mw.text.decode` | Decodes numeric entities and the five default named entities; optional `true` uses the .NET HTML5 table | MediaWiki/PHP entity-table edge cases require differential testing before claiming parity |
| `mw.text.listToText` | Empty, one, two, and many scalar list values with explicit separator/conjunction | Deterministic defaults are `", "` and `" and "`; maximum 1024 items |
| `mw.ustring.len` | Unicode scalar-value count | Grapheme clusters are not counted as one unit |
| `mw.ustring.lower`, `upper` | Invariant Unicode case conversion | Locale/site-specific casing is not exposed |
| `mw.ustring.sub` | One-based and negative codepoint indexing with bounded slicing | Lua pattern semantics are not involved |
| `mw.ustring.find` | Literal Unicode-codepoint search with one-based result offsets and optional initial index | Pattern mode is explicitly rejected as `UnsupportedApi` |

The `mw.text.encode` and `decode` choices follow the official reference:
`encode` uses named entities for five characters and numeric entities for other
selected characters, while default `decode` recognizes the small named set.

## Controlled module loading

`require` is available only with the `module.require` capability. Module names
are canonicalized through the Module namespace rules and path-like names are
rejected. The bundle is validated before interpretation: every source hash is
recomputed, content models are restricted to Lua/Scribunto or JSON where
applicable, source totals and entry counts are bounded, all relationship edges
must resolve, and cycles/duplicate or cross-wiki identities are rejected.

`require` executes a bundled Lua module once per invocation and caches its
return value. It does not search the filesystem, network, package paths, or
the host repository. A bundled module that is not listed as an explicit edge
is rejected. Nested depth and interpreter work are shared across the whole
invocation; output remains bounded by the existing result budget. Every
executed module is returned with its source wiki, page/revision identity,
checksum, and status.

`mw.loadData` executes only a bundled Lua/Scribunto entry, requires a table,
and exposes an immutable bounded proxy. `mw.loadJsonData` reads only a
bundled JSON-content entry and converts bounded JSON scalars, arrays, and
objects to immutable Lua values. Duplicate object keys, malformed JSON,
unsupported content models, excessive depth, and excessive nodes are rejected.
The supported conversion is intentionally smaller than MediaWiki's complete
JSON/Scribunto behavior and is not a parity claim.

## Explicitly unsupported capabilities

The worker does not register `frame:preprocess`,
`frame:expandTemplate`, or `frame:callParserFunction`. It does not implement
nested worker-to-host requests. Calling these methods therefore fails inside
the interpreter instead of silently performing an untyped lookup.

Static inventory remains authoritative for offline discovery and historical
planning; the worker receives only the host's already-resolved bundle and
never chooses historical revisions. The complete Scribunto API, Lua 5.1
parity, and OS sandbox certification remain out of scope.

The following are absent from the hard-sandbox interpreter and remain absent
from the host contract: `os`, `io`, `debug`, `load`, `loadfile`, `dofile`,
`package.loadlib`, CLR interop, reflection, filesystem, environment, process,
network, database, title, page, template, and site services.

## Resource and security model

MoonSharp coroutine auto-yielding and the maximum slice count provide an
interpreter-level work bound. The host additionally supplies `prlimit` CPU,
address-space, process-count, and file-descriptor ceilings and kills the
worker tree on timeout or cancellation. The executor still requires explicit
opt-in and bubblewrap; it never falls back to an unconfined process.

The separate OS result remains `CW-M3-005B BLOCKED`: the available host denies
bubblewrap's private-network `NETLINK_ROUTE` setup. Consequently memory,
process, filesystem, and network isolation are documented controls and not
portable production evidence. The worker-level tests prove interpreter and
protocol behavior only.

## CW-M3-005B.4 offline compatibility corpus

The versioned synthetic corpus, deterministic runner, JSON report, and API gap
inventory are documented in
`MEDIAWIKI-SCRIBUNTO-COMPATIBILITY-CORPUS.md`. The current corpus is
`cw-m3-005b.4-v1`; it records `SUPPORTED`, `PARTIALLY_SUPPORTED`,
`UNSUPPORTED`, `INVALID_INPUT`, and `SECURITY_REJECTED` outcomes separately.
Its passing results are experimental worker/protocol evidence only and do not
alter the rejecting production executor or the blocked OS-sandbox verdict.

## CW-M3-005B.5 security hardening

The security architecture and host-side certification procedure are documented
in `CONTINUUMWIKI-LUA-SANDBOX-SECURITY.md`. The experimental launcher now
requires a verified delegated cgroup v2, removes `RLIMIT_NPROC`, avoids broad
`/usr` and `/etc` mounts, resolves the installed .NET runtime dynamically,
clears and rebuilds the worker environment, and drains bounded stdout/stderr
concurrently. These changes preserve the existing typed worker protocol and
rejecting default; they do not constitute host certification by themselves.

## Synthetic fixtures and adversarial coverage

`MediaWikiInventory/Fixtures/scribunto-moonsharp-compatibility-fixture.xml`
contains a compatibility module, Unicode and empty-argument invocation,
static `require`, and invalid dynamic-load examples. The focused test fixture
also covers:

- valid named/positional invocation and fresh-process state isolation;
- missing versus empty arguments and Unicode scalar indexing;
- `nowiki`, encode/decode, list formatting, literal split/find, and substring;
- invalid syntax, missing exports, runtime errors, unsupported pattern/frame APIs;
- infinite and recursive work, source/argument/result/count bounds;
- malformed JSON, unsupported protocol, duplicate fields/keys, capabilities,
  and message-sequence rejection;
- recovery after a failed worker and preservation of the rejecting executor.

The focused 005B.3 suite passes `31/31`; the full repository regression passes
`211`, with `21` intentional MariaDB skips and no failures.
Any future expansion must add typed capability and provenance contracts first,
then differential tests against a real Scribunto/MediaWiki reference. Passing
the MoonSharp tests alone must never be promoted to production approval.

## CW-M4-001 host certification status

The dedicated-identity host certification investigation is recorded in
`artifacts/validation/mediawiki-scribunto-host-certification-v1.json`; the
repeatable operator procedure is
`docs/operations/SCRIBUNTO-HOST-CERTIFICATION.md`.

Read-only inspection found the installed socket active and listening, the
reviewed root-owned worker and launcher hashes, the declared dedicated systemd
identity, finite service resource limits, bubblewrap namespace/mount
configuration, and SELinux enabled in enforcing mode. The observed worker
instances were failed with no live MainPID or exposed ControlGroup, and the
current inspection session was not established as the dedicated worker
identity. Effective UID/capability state, socket authorization, cgroup
enforcement, namespace denials, filesystem/network denial probes, worker
recovery, and relevant SELinux contexts therefore remain `BLOCKED` rather than
being inferred from configuration.

CW-M4-001 is investigation-complete with verdict
`HOST_CERTIFICATION_BLOCKED`; it is not `HOST_CERTIFIED`. Production Lua
remains disabled, rejecting Scribunto and Cargo defaults remain in place, and
no service, permission, policy, binary, or production data was changed.

## CW-M4-001A runtime activation status

The CW-M4-001A report is
`artifacts/validation/mediawiki-scribunto-runtime-certification-v1.json`.
Its authorization gate was evaluated but not satisfied: the inspection session
was not the dedicated worker identity and did not have effective socket-group
access, so no diagnostic socket request was sent. The installed socket remains
active/listening, while the observed worker instances are failed with no live
PID or usable cgroup.

One historical exit-137 instance has operator-supplied CoreCLR/GC allocation
failure evidence under the configured 256 MiB service ceiling. Exit-134 and
exit-1 instances remain cause-unclassified because current journal access was
unavailable. No startup status is treated as a security failure without direct
evidence. Runtime identity, cgroup, bubblewrap, filesystem, network, SELinux
context, resource, capability, and production-activation controls remain
`BLOCKED`; no hardening or production state was changed.

## CW-M4-001B memory characterization status

The memory characterization report is
`artifacts/validation/mediawiki-scribunto-memory-characterization-v1.json`.
Static inspection verified the deployed bundle manifest, .NET 10 runtimeconfig,
empty service environment, launcher `--clearenv` behavior, fixed runtime paths,
and the separate systemd `MemoryMax=256M` and launcher address-space limit.
No GC heap-hard-limit override or missing declared framework was found in the
static artifacts.

Runtime memory characterization remains blocked because the authorization gate
did not permit worker activation or disposable resource experiments. The
operator-supplied exit-137 CoreCLR/GC message is evidence of startup failure
under the configured ceiling, but not proof of a cgroup OOM kill. Exit-134 and
exit-1 causes, cgroup counters, resident/committed memory, cancellation
cleanup, and a minimum viable resource policy remain unverified. No production
limit, worker, launcher, SELinux policy, or security default changed.
