# ContinuumWiki Lua Engine sandbox security (CW-M3-005B.5/005B.6B/005B.6C)

## Status and security boundary

`CW-M3-005B.6C VERIFIED — HOST SANDBOX`

The implementation now has a production-shaped, fail-closed launch boundary
and a repeatable host certification harness. The unrestricted host report
`cw-m3-005b.6c-host-sandbox-v1` passed 20/20 checks through the installed
socket-activated worker, including the procfs mount, cgroup ceilings, OS
denials, recovery, and concurrent requests. Certification remains host-specific:
repeat the report for every deployment host.

The operator-run provisioning and verification procedure is
`docs/operations/CONTINUUMWIKI-LUA-SANDBOX-PROVISIONING.md`. No system-wide
installation or production Lua approval was performed by this change; the
verification used the already-installed socket and a disposable delegated
cgroup service.

The subsystem is the **ContinuumWiki Lua Engine**. `Scribunto` is retained in
protocol, compatibility, and source-contract names only because it is the
legacy MediaWiki compatibility designation.

The normal application renderer remains
`RejectingMediaWikiScribuntoExecutor`. The web startup path now has a
fail-closed selector: it can construct `MediaWikiScribuntoSocketExecutor` only
after explicit `ContinuumWiki:ExperimentalLua` enablement, the fixed
`unix-socket` transport, a readable verified host report, a live local socket,
and restrictive endpoint/parent-directory permissions all pass. Missing or
invalid evidence selects the rejecting executor. This does not authorize
production Lua, production database access, or production MediaWiki access.

## Renderer integration path (CW-M3-006A.2)

The existing call chain is:

1. `MediaWikiTemplateExpander` recognizes `#invoke` and parses it through
   `MediaWikiScribuntoInvocationParser`.
2. Its existing `ExpansionContext` resolves the historical root module and
   statically discovered dependency closure through
   `ITwMediaWikiImportRepository`, preserving source page/revision hashes.
3. The selected `IMediaWikiScribuntoExecutor` serializes the typed invocation
   with `MediaWikiScribuntoWorkerProtocol` and sends it over the approved
   Unix-domain socket. Lua never runs in the ASP.NET process.
4. The response is validated for protocol version, correlation ID, status,
   diagnostics, dependencies, and result budget. The returned text is treated
   as expansion text and re-enters the existing bounded Wikitext parser; it
   does not become trusted HTML or bypass sanitization.

The worker client enforces request/response limits, connection and total
execution timeouts, cancellation, newline framing, and socket half-close.

## Trust model

The web application is trusted to select a historical module bundle and pass a
bounded request. Lua is untrusted. The worker receives only source, explicit
function/arguments, typed provenance, explicit dependency edges, capabilities,
and budgets. It receives no database connection, credential, host secret,
arbitrary environment, CLR object, filesystem service, network client, title
service, or template callback.

The worker is disposable and one-request-per-process. A failed, timed-out, or
killed process is not reused. Dependencies are hash-checked and resolved by
the host before execution; Lua cannot perform dynamic source lookup.

## Launch architecture

`ExperimentalMediaWikiScribuntoExecutor` is still explicit opt-in. When
enabled, it requires all of the following before starting a worker:

1. Linux and bubblewrap at `/usr/bin/bwrap`.
2. A real, existing worker assembly.
3. An administrator-provisioned cgroup v2 directory below `/sys/fs/cgroup`.
4. Readable and writable `cgroup.procs`, finite `memory.max`, finite
   `pids.max`, and finite `cpu.max` values within the configured policy.

The application does not call `sudo`, create a systemd unit, set arbitrary
unit properties, or accept a unit name/command from wiki content. A delegated
cgroup is inspected before execution and the process PID is attached and
verified immediately after start. Failure to verify or attach is a structured
fail-closed result.

The administrator-provisioned socket/service pattern is in
`deploy/systemd/continuumwiki-scribunto-worker.socket` and
`continuumwiki-scribunto-worker@.service`. The service has fixed executable
and arguments, a dedicated unprivileged identity, `MemoryMax=256M`,
`TasksMax=32`, `LimitCPU=2`, `LimitNOFILE=256`, `KillMode=control-group`,
`Delegate=yes`, `NoNewPrivileges`, private devices/tmp/network, and protected
system/home paths. `ProtectKernelTunables=no` is intentional for this nested
launcher: systemd's tunables protection masks child mounts below `/proc`, which
causes the kernel to reject bubblewrap's new private procfs as `VFS: Mount too
revealing`. Bubblewrap immediately remounts its PID-namespace procfs read-only,
so the worker retains private process visibility without writable kernel
tunables. Delegation is limited to this dedicated service subtree;
the parent systemd limits remain authoritative. Operators must install the fixed launcher and worker paths, provision
the `continuumwiki-scribunto` identity, and grant only the intended socket and
cgroup permissions. Wiki requests never reach systemd control APIs.

The application-side direct launcher is retained for the experimental
integration, but it requires the same delegated cgroup and does not use
`RLIMIT_NPROC`. Task/process enforcement belongs to cgroup v2 `pids.max`;
`prlimit` retains only CPU time, address-space, and file-descriptor controls.

## Bubblewrap filesystem and environment policy

The sandbox uses `--unshare-all`, `--die-with-parent`, `--new-session`,
`--clearenv`, private read-only `/proc`, private `/dev`, and a tmpfs `/tmp`. It mounts
read-only only:

- the dynamically resolved .NET runtime root as `/dotnet`;
- the worker output directory as `/worker`;
- the host ICU data directory as `/usr/share/icu` when present;
- the runtime shared-library directories that exist on the host (`/lib`,
  `/lib64`, `/usr/lib`, and `/usr/lib64`).

It no longer mounts `/usr` or `/etc`. It never mounts home directories,
repository source trees, SSH material, application configuration, database
configuration, or writable host paths. The runtime root is resolved from the
actual `dotnet` process path or explicitly configured `DotnetRootPath`; no
universal `/opt/dotnet` assumption is used.

The service account is the host-side worker identity; the launcher does not
invent a 65534 subordinate UID/GID mapping. Bubblewrap's user namespace still
isolates namespace-root from the host, while systemd's non-root `User` and
zero-capability verification establish the host identity. The only environment
values set inside the sandbox are `DOTNET_ROOT`, `DOTNET_ROOT_X64`, `HOME=/tmp`,
`TMPDIR=/tmp`, and `PATH=/dotnet`. Host secrets are not inherited.

## I/O, timeout, and termination controls

The launcher starts bounded concurrent readers for stdout and stderr before
writing the request. It caps stdout by bytes (with UTF-8 decoding only after the
bounded read), caps stderr separately, kills the process tree on overflow,
timeout, cancellation, or cgroup-attachment failure, and waits for process
exit and both readers before returning. It never calls unbounded
`ReadToEndAsync()` on a live worker pipe. The request is serialized and size
checked before stdin is written; structured response validation remains in the
existing versioned protocol.

The worker has independent Lua instruction/result/input budgets. The host adds
wall-clock, CPU, a 2 GiB virtual-address ceiling, file-descriptor, concurrency, cgroup memory,
and cgroup task ceilings. `KillMode=control-group` and the executor's process
tree kill/reap path prevent orphaned descendants.

## Certification harness

Run from a permitted Linux terminal or administrator-provisioned test service:

```bash
CW_SCRIBUNTO_WORKER_CGROUP_PATH=/sys/fs/cgroup/continuumwiki-scribunto \
  scripts/verify-moonsharp-host-sandbox.sh
```

The script writes
`artifacts/validation/moonsharp-host-sandbox-report-v1.json` and returns
nonzero unless the verdict is `VERIFIED`. It emits
`HOST_CERTIFICATION_UNAVAILABLE` when Linux privileges, namespace support,
required tools, the delegated cgroup, or the worker identity are unavailable;
it never silently skips a mandatory check.

The report covers Linux/tools and dynamic .NET discovery, artifact identity,
non-root launch, cgroup v2 memory/task/CPU evidence, bubblewrap namespace
setup, filesystem read/write denial, network denial, environment sanitization,
process/library denial, the real worker launcher, bounded output, timeout,
recovery, concurrency, malformed input, and state isolation. The separate
`ScribuntoSandboxProbe` is a synthetic OS-level probe; it is not exposed as a
production worker capability.

The probe distinguishes API-level denial from OS-level denial. MoonSharp's
missing `io`, `os`, `debug`, and CLR APIs prove only interpreter policy. The
probe separately attempts host file access, host-path writes, a network send,
process launch, secret-variable access, and arbitrary library loading inside
the actual bubblewrap namespace.

## Threat model and incident handling

Threats include malicious imported modules, infinite loops, deep recursion,
memory/table exhaustion, fork/task exhaustion, output flooding, dynamic module
lookup, filesystem disclosure, network access, environment leakage, CLR
reflection, worker crashes, and cancellation races.

The response is fail closed: return a structured diagnostic, mark the
invocation unexecuted, kill/reap the worker tree, preserve the source and
provenance diagnostics, and allow the next invocation to use a fresh process.
Operators should retain the machine-readable report and service journal,
disable the experimental opt-in if a control fails, and investigate cgroup,
systemd, bubblewrap, kernel, and LSM changes before retrying. No host security
control should be disabled to make a test pass.

## Known limitations and operational prerequisites

- Host certification is not portable between kernels, systemd/cgroup
  delegation policies, bubblewrap builds, user-namespace mappings, and LSM
  policies.
- A successful configuration inspection is not enforcement evidence; the
  harness must observe the process in the cgroup and pass stress/denial probes.
- The current restricted Codex environment is not a supported certification
  host. Its prior `NETLINK_ROUTE`/seccomp failure and lack of delegated memory
  controls require `HOST_CERTIFICATION_UNAVAILABLE`, not a workaround.
- Lua/Scribunto semantic parity remains the separate experimental corpus and
  differential-testing problem documented by the compatibility documents.
- Even after host certification, production deployment requires an explicit
  operator approval and a separate review of the fixed service/socket,
  identity, cgroup delegation, logs, and rollback procedure.
