# MediaWiki Scribunto runtime evaluation (CW-M3-005B)

## Verdict

`CW-M3-005B BLOCKED`

This milestone produced a disposable execution proof of concept and passed
the managed interpreter and protocol checks, but the required operating-system
isolation could not be exercised on the available Linux host. The current
minimal reproduction reaches bubblewrap's network-namespace setup and fails
when bubblewrap attempts to create the loopback `NETLINK_ROUTE` socket:

```text
bwrap: loopback: Failed to create NETLINK_ROUTE socket: Operation not permitted
```

Lua execution therefore remains disabled and the default rejecting executor
remains the only application renderer provider.

This is a safety block, not a production approval. No production MediaWiki
endpoint, database, module source, or credentials were accessed.

## CW-M3-005B.5/005B.6 sandbox hardening and provisioning status

`CONTINUUMWIKI-LUA-SANDBOX-SECURITY.md` now documents the fail-closed cgroup
v2 and bubblewrap architecture, minimized runtime mounts, sanitized
environment, concurrent bounded pipe readers, process-tree reaping, fixed
systemd service/socket configuration, and host certification harness.

The old `prlimit --nproc=64` boundary has been removed. Task limits are now
required from an administrator-provisioned cgroup `pids.max`; CPU time,
address-space, and file-descriptor `prlimit` controls remain. The application
will not start Lua unless the cgroup control files are finite, writable, and
within policy.

This is an implementation, provisioning-procedure, and harness milestone, not
host certification. The final verdict remains `CW-M3-005B.6 BLOCKED` until an
operator provisions the fixed units on a supported Linux host and the harness
passes with actual cgroup enforcement and OS-level probes.

## CW-M3-005B.3 controlled module loading

`CW-M3-005B.3 VERIFIED — EXPERIMENTAL`

The 005B.3 extension preserves the blocked OS result above and adds no
production registration. The host now prepares a bounded, typed module bundle
using the existing historical source-selection contract. Each entry retains
source wiki, canonical title, page/revision identity, content model, source
SHA-256, raw source, and explicit child relationships. The worker validates
the entire bundle before Lua execution, including duplicate identities,
checksums, cross-wiki identity, supported content models, missing edges,
path-like titles, cycles, graph count, and aggregate source characters.

Within one fresh invocation, controlled `require` resolves only explicit
bundle edges and caches return values; repeated imports share the cached
value, while separate worker processes do not share state. `mw.loadData`
supports immutable bounded Lua tables, and `mw.loadJsonData` supports bounded
JSON scalar/array/object conversion with duplicate-key, depth, node, and
content-model checks. Executed module provenance is returned to the host as
typed rendering dependencies. Frame titles and parent-frame titles come only
from trusted request metadata, and frame argument behavior remains bounded and
nil/empty-safe.

The focused worker suite covers bundled/nested/shared imports, cycles,
missing imports, modified hashes, cross-wiki and path-like names, unsupported
models, large graphs/source totals, immutable and malformed JSON data,
historical identities, frame metadata, dangerous libraries, state leakage,
resource bounds, protocol tampering, and cancellation. This remains
interpreter/protocol evidence only; it does not certify bubblewrap or an OS
sandbox.

## CW-M3-005B.4 corpus result

The offline, versioned compatibility corpus and deterministic report runner are
documented in `MEDIAWIKI-SCRIBUNTO-COMPATIBILITY-CORPUS.md`. The current
synthetic report has 20 cases, 18 worker executions, 2 validation-only cases,
and 20 passes. It includes explicit partial, unsupported, invalid-input,
security-rejected, and Lua-version-difference classifications. This expands
experimental compatibility evidence only; it does not change this document's
`CW-M3-005B BLOCKED` OS-isolation verdict or enable production Lua execution.

## Existing boundary and integration surface

CW-M3-005A remains authoritative. The experiment uses, without replacing:

- `MediaWikiScribuntoInvocation` for typed function, positional, named,
  source-page, revision, culture, time-zone, and source-location context;
- `IMediaWikiScribuntoExecutionHost` for typed historical module-bundle preparation;
- `MediaWikiScribuntoExecutionBudget` for source, argument, result, work,
  dependency, time, and memory policy values;
- `MediaWikiScribuntoExecutionResult` for bounded text, diagnostics,
  dependencies, and the executed flag;
- the existing module-aware cache and provenance contracts.

No startup registration was added to `TightWiki`. The normal renderer still
constructs `RejectingMediaWikiScribuntoExecutor`. The experimental executor
requires a worker assembly path, an explicit `EnableExperimentalExecution`
flag, Linux, and bubblewrap; it throws rather than falling back to an
unisolated process.

## Candidate matrix

The versions below are the versions observed during this evaluation on
2026-10-10. They are not claims that any candidate is approved for production.

| Candidate | Lua / host fit | Isolation and limits | Supply chain / license | Decision |
|---|---|---|---|---|
| [MoonSharp 2.0.0](https://www.nuget.org/packages/MoonSharp/2.0.0) | Managed C# interpreter, documented as Lua 5.2-compatible; not Scribunto's Lua 5.1 target | Hard/soft library presets and automatic coroutine yielding are useful controls, but the project explicitly warns that removing APIs is not a complete sandbox and CLR interop must be controlled | BSD-3-Clause; stable package is old and 3.0 remains beta | Selected for a disposable worker-only POC; rejected as a standalone production boundary |
| [NLua 1.7.9](https://www.nuget.org/packages/NLua/1.7.9) + [KeraLua](https://github.com/NLua/KeraLua) | Maintained .NET bindings around native Lua 5.4; native version is a poor direct match for Scribunto's Lua 5.1 behavior | Native bindings provide no sandbox by themselves; in-process use would violate the hard constraint and a separate process would still need OS policy | NLua/KeraLua are MIT; native ABI and binary packaging increase deployment and review surface | Rejected for this POC |
| [PUC Lua 5.1](https://www.lua.org/manual/5.1/manual.html) standalone process | Closest language family to MediaWiki Scribunto; official Lua implementation | A standalone binary can be killed and confined, but the Lua VM itself does not provide filesystem/network/process isolation or memory safety; all limits must be external | Lua license; requires a pinned, reproducible native build and security review | Best future compatibility candidate, not introduced because no approved pinned binary/toolchain was present |
| [Wasmtime .NET 48.0.2](https://www.nuget.org/packages/Wasmtime/48.0.2) hosting a Lua-WASM guest | Strong host/guest memory boundary is possible, but no verified maintained Scribunto-compatible Lua-WASM guest was available for this repository | Wasmtime resource limiters cover guest resources, not all embedder allocations; WASI capabilities still need explicit denial; wall-clock termination still needs host control | Apache-2.0 with LLVM exception; large native package and a second unverified guest supply chain | Rejected as impractical for this milestone |

MediaWiki's [Scribunto Lua reference manual](https://www.mediawiki.org/wiki/Extension:Scribunto/Lua_reference_manual)
documents the compatibility target and confirms that a generic Lua library's
version label is not enough to establish Scribunto parity.

## Experimental architecture

The POC adds `ScribuntoWorker`, a separate `net10.0` executable with the
exact `MoonSharp` 2.0.0 package reference. The worker accepts one JSON line and
returns one JSON line. It receives explicit module source, exported function,
named and positional arguments, and bounded policy values. A worker exits
after one request; there is no shared interpreter state, persistent module
cache, or worker reuse.

`ExperimentalMediaWikiScribuntoExecutor` starts the worker through:

1. `prlimit` for address-space, CPU-time, process-count, and file-descriptor
   ceilings;
2. `bubblewrap` with `--unshare-all`, `--die-with-parent`, a private `/proc`
   and `/dev`, a temporary `/tmp`, a read-only worker/runtime view, and no
   network namespace connectivity;
3. a JSON request on stdin and a bounded response on stdout;
4. process-tree termination on timeout or cancellation.

The executor limits concurrent workers with a semaphore. A failed or killed
worker affects only that invocation; the next invocation gets a fresh process.
If bubblewrap is absent, the OS is not Linux, or the explicit opt-in is not
present, execution is rejected. There is no fallback to `dotnet` without the
isolation wrapper.

The current environment prevented step 2 from completing. The exact
disposable command reached bubblewrap but failed before the worker started:

```text
bwrap: loopback: Failed to create NETLINK_ROUTE socket: Operation not permitted
```

Consequently the architecture is implemented fail-closed, but its strongest
claim remains unverified here.

## CW-M3-005B.1 environment diagnosis

The commands were run read-only on 2026-10-10. The process is not running on
an unrestricted host shell:

| Observation | Result | Interpretation |
|---|---|---|
| `uname -a` | Linux kernel `7.2.9-1-default` | Host kernel is visible, but this does not imply host-level privilege |
| `systemd-detect-virt` | `container-other` | The execution surface is nested/restricted |
| cgroup | `/user.slice/.../app-com.microsoft.VSCode-80120.scope` | Process is inside the VS Code user-service scope |
| capabilities | Effective/permitted/bounding sets empty | No `CAP_NET_ADMIN`, `CAP_SYS_ADMIN`, or other capability is available |
| `NoNewPrivs` / seccomp | `1` / mode `2`, one filter | The parent execution policy is already applying a seccomp filter |
| `/proc/sys/user/max_user_namespaces` | `2147483647` | User-namespace quota is not the demonstrated cause |
| `/proc/sys/user/max_pid_namespaces` | `2147483647` | PID-namespace quota is not the demonstrated cause |
| cgroup `pids.max` / `pids.current` | `37321` / `572` | PID capacity was not near exhaustion |
| cgroup controllers in the app scope | `pids` only | No delegated memory or CPU cgroup controller is available there |
| SELinux | `/sys/fs/selinux/enforce` is `1` | Enforcement is enabled; no readable AVC denial was found, so SELinux is not assigned as the cause |
| kernel log / ptrace | `dmesg` and `strace` denied with `Operation not permitted` | Syscall-level tracing cannot be used from this environment |

Bubblewrap is `/usr/bin/bwrap`, version `0.12.0`, a regular executable owned
by `nobody` with no file capabilities or setuid bit. The individual namespace
reproduction was:

```text
bwrap --unshare-user -- ... /usr/bin/true                 exit 0
bwrap --unshare-pid -- ... /usr/bin/true                  exit 0
bwrap --unshare-ipc -- ... /usr/bin/true                  exit 0
bwrap --unshare-uts -- ... /usr/bin/true                  exit 0
bwrap --unshare-cgroup -- ... /usr/bin/true               exit 0
bwrap --unshare-all -- ... /usr/bin/true                  exit 1 (NETLINK_ROUTE EPERM)
bwrap --unshare-all --share-net -- ... /usr/bin/true      exit 0 (network remains shared)
```

The successful `--share-net` case is not an acceptable workaround: it proves
that the other bubblewrap setup operations can complete, but it deliberately
does not provide the required network isolation. `unshare --user --map-root-user
--net --mount-proc` also failed while mounting proc with `Operation not
permitted`. This is consistent with the inherited seccomp/no-new-privileges
policy. It is not evidence that increasing namespace sysctls would fix the
problem, and no host security setting was changed.

The exact launcher-shaped command also reproduces the failure:

```text
prlimit --as=2147483648 --cpu=2 --nproc=64 --nofile=256 \
  bwrap --unshare-all ... -- /usr/bin/true
# bwrap: loopback: Failed to create NETLINK_ROUTE socket: Operation not permitted
```

The parent policy denies the same primitive outside bubblewrap:

```text
python3 -c 'import socket; socket.socket(socket.AF_NETLINK, socket.SOCK_RAW, socket.NETLINK_ROUTE)'
# PermissionError: [Errno 1] Operation not permitted
```

The same policy also denies a normal `AF_INET` stream socket before any
connection is attempted. Thus the inherited filter blocks general network
socket creation, but that ambient restriction is not treated as a portable
worker guarantee.

This identifies the immediate root cause as denial of `AF_NETLINK`/
`NETLINK_ROUTE` socket creation in the current execution environment. The
available evidence strongly implicates the inherited seccomp/no-new-privileges
policy and absent network-admin capability; because ptrace and kernel-log
access are also denied, this run cannot distinguish a seccomp denial from an
additional LSM decision. It does establish that the failure is environmental,
not a bubblewrap namespace-quota exhaustion or an application Lua failure.

No supported alternate disposable environment was available from this
execution surface. Rootless `podman info` could not initialize because
`/run/user/1000/libpod` is read-only, and `systemd-run --user` could not
connect to the user scope bus. No privileged container, host security change,
SELinux change, or global namespace-limit change was attempted.

## Restricted Lua experiment

The worker constructs MoonSharp with `CoreModules.Preset_HardSandbox`. It does
not expose `os`, `io`, `debug`, `load`, `loadfile`, `dofile`, coroutine APIs,
or the dynamic/CLR interop surface. `package.loadlib` is absent. The custom
host API is limited to:

- `mw.text.trim(value)`;
- `mw.text.split(value, separator)`;
- `mw.ustring.len(value)` using Unicode scalar values;
- `mw.ustring.lower(value)` and `mw.ustring.upper(value)`;
- `frame.args`, represented as a bounded table containing positional and named
  values.

There is no `mw.site`, title lookup, page retrieval, database access, template
access, network client, filesystem API, process API, reflection, or arbitrary
host-function registration. The worker returns only bounded text and a
structured error. It does not claim Scribunto compatibility.

## Resource controls

| Control | POC behavior | Evidence / limitation |
|---|---|---|
| Source size | Worker rejects over-limit source before parsing | Focused adversarial test passed |
| Argument size | Worker rejects over-limit positional/named data | Focused adversarial test passed |
| Result size | Worker rejects over-limit result before response | Focused adversarial test passed |
| Instruction/work | MoonSharp coroutine auto-yield slices plus a maximum slice count | Infinite and recursive synthetic modules returned `InstructionLimitExceeded` |
| Wall clock | Host waits for the configured budget and kills the process tree | Code path is present; end-to-end wrapped-worker verification is blocked by namespace creation |
| Memory | `prlimit --as` is applied to the worker command; the .NET runtime needs a large virtual-address headroom before startup | The host ceiling is hard, but no trustworthy Lua-only memory number was established; cgroup/RSS verification is still required |
| Dependencies | Existing 005A scanner/planner bounds depth and records cycles; worker POC does not load modules dynamically | Existing 005A cycle tests passed |
| Concurrency | Host semaphore defaults to one worker | No request can share an interpreter; production capacity remains undecided |
| Worker lifecycle | One request per process, then exit | Recovery is covered by launching a new worker after failure |

## Adversarial security evidence

The focused suite is `ExperimentalMediaWikiScribuntoRuntimeTests` (8 passed,
0 failed, 0 skipped). It verifies:

- basic synthetic invocation, named and positional arguments, and the small
  `mw` API;
- infinite-loop and recursive-call work limits;
- huge result, source, and argument rejection;
- invalid Lua syntax and structured errors;
- absence of `os`, `io`, `loadfile`, `load`, `dofile`, `debug`, and
  `package.loadlib`;
- fresh-process state isolation and recovery after a failed invocation;
- fail-closed executor configuration and unchanged rejecting mode.

The following table distinguishes interpreter/API evidence from the missing
OS-level proof:

| Adversarial case | Result |
|---|---|
| Infinite loop | PASS: instruction/work budget returned a structured error |
| Memory exhaustion | BLOCKED: external address-space ceiling exists, but the wrapped worker could not be started and a Lua-only memory limit was not verified |
| Recursive calls | PASS: instruction/work budget returned a structured error |
| Huge strings/output | PASS: result bound returned a structured error |
| Filesystem read/write | API absent; OS namespace proof BLOCKED |
| Environment-variable access | API absent; OS namespace proof BLOCKED |
| Network access | No network API; network namespace proof BLOCKED |
| Process execution | No process API; namespace proof BLOCKED |
| Dynamic library loading | `package.loadlib` absent |
| Reflection/CLR access | No CLR registration or reflection surface |
| Restricted-API bypass | No bypass observed in synthetic probes; independent OS proof BLOCKED |
| Invalid syntax | PASS: structured worker error |
| Malformed arguments | PASS: source/argument validation errors |
| Worker failure recovery | PASS: new worker succeeds after failed worker |
| Concurrent isolation | Separate worker design; concurrency stress evidence remains pending |
| State leakage | PASS: separate workers returned no prior global state |
| Excessive output | PASS: bounded result error |
| Module cycles | PASS in 005A planner; runtime dependency loading is not enabled |
| Cancellation/timeout | Host kill path exists; wrapped-worker measurement BLOCKED |

Any claim of production sandbox safety would be invalid while the rows marked
`BLOCKED` remain unresolved.

## Performance observations

The worker is intentionally one-shot, so there is no warm shared-worker mode.
Five local synthetic runs of the Debug worker measured:

- cold wall time: `0.14`, `0.14`, `0.19`, `0.20`, `0.21` seconds;
- observed median: `0.19` seconds per process;
- maximum resident set: `46,304`–`46,768` KiB;
- warm-execution latency and sustained throughput: not measured because the
  worker is recycled after every invocation.

These are local process measurements, not deployment capacity claims. They
were run without the unavailable bubblewrap wrapper, so they exclude wrapper
startup and are not security-validation measurements.

## Verification record

CW-M3-005B.3 final verification:

```text
Focused worker tests: 31 passed, 0 failed, 0 skipped
Full regression: 211 passed, 21 intentional skips, 0 failed
Debug build: passed
Release build: passed
Release publish: passed
Clean solution restore: passed
Disposable MariaDB/HTTP harness: passed (CW-M3-004D VERIFIED)
```

The focused test run initially required loopback-enabled test execution
because the restricted test host denied the test runner's local socket. The
rerun passed. The separate bubblewrap OS isolation block remains unchanged.

Focused runtime/security tests:

```text
Passed: 8, Failed: 0, Skipped: 0
```

The first test attempt was blocked by the restricted test host's loopback
socket permission. The rerun with loopback-enabled test execution passed.

The required full regression/build/publish commands were run for 005B.3, but
they do not certify the separate OS isolation criterion. The current
repository regression evidence is 211 passed, 21 intentional skips, 0 failed;
Debug/Release builds, Release publish, clean restore, and the disposable
MariaDB/HTTP harness also passed.

## Remaining risks and recommendation

Before any future milestone can consider runtime execution, it must supply a
disposable Linux host where user namespaces, network namespaces, cgroups or an
equivalent memory controller, process-tree kill, and least-privilege worker
permissions can be measured. It must repeat the adversarial matrix with actual
filesystem, environment, network, process, memory, crash, concurrent, and
cancellation assertions. It must also decide whether Lua 5.1 compatibility is
required strongly enough to replace the MoonSharp POC with a pinned standalone
PUC Lua worker or another audited runtime.

CW-M3-005C must not start automatically. The recommendation is to keep the
rejecting executor, preserve all imported module source/provenance, and obtain
an explicit security/deployment decision before any further compatibility or
real-corpus runtime work.

## Historical CW-M3-005B.2 compatibility foundation

`CW-M3-005B.2 VERIFIED — EXPERIMENTAL` records the initial offline MoonSharp
worker hardening and compatibility foundation. It is superseded for module
loading by the 005B.3 section above and does not change the `CW-M3-005B
BLOCKED` verdict: bubblewrap's private-network setup remains unverified on
this host, and no production execution path is enabled.

The host and worker now share `MediaWikiScribuntoWorkerProtocol`. The one-shot
request includes protocol version, correlation ID, module title and source
identity, source wiki/revision provenance, function, typed string arguments,
budgets, and explicit capabilities. The one-shot response includes matching
version/correlation fields, status, bounded text, structured diagnostics, and a
resource-limit outcome. Unknown/duplicate fields, duplicate or ambiguous named
keys, unsupported capabilities, malformed JSON, invalid functions, oversized
payloads, and unexpected message sequences are rejected before interpretation.

The worker retains MoonSharp's hard sandbox and fresh-interpreter lifecycle.
The initial compatibility surface covered bounded `frame.args`, `mw.text`,
and Unicode-safe `mw.ustring` helpers. Pattern matching, template/parser
callbacks, nested worker-to-host calls, CLR/reflection, and all host services
remain unavailable. Static dependency scanning/planning from 005A remains
authoritative for offline inventory; 005B.3 adds only the separately
documented pre-resolved module bundle and controlled loaders.

The synthetic fixture is
`MediaWikiInventory/Fixtures/scribunto-moonsharp-compatibility-fixture.xml`.
The focused suite covers protocol rejection, API semantics, Unicode and
missing-versus-empty arguments, interpreter limits, dangerous-library absence,
unsupported operations, recovery, and preservation of the rejecting executor.
The historical focused result was `25 passed, 0 failed, 0 skipped`. The
current 005B.3 focused and full-regression results are recorded in the
section above and the final verification record. The test result is
interpreter/protocol evidence only;
OS-level memory/process/filesystem/network isolation remains `BLOCKED` or
`UNVERIFIED` until a disposable host permits the required measurements.
The final `scripts/verify-m3-004d.sh` disposable MariaDB/HTTP harness also
passed, including native rendering and the default unsupported `#invoke`
diagnostic path.
