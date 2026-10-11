# Scribunto host certification procedure (CW-M4-001)

This read-only procedure inspects the already-installed MoonSharp worker and
performs only the approved synthetic diagnostic. It does not deploy binaries,
modify systemd, restart services, change socket permissions, alter SELinux,
enable production Lua, or access MediaWiki/MariaDB data.

The overall verdict is `HOST_CERTIFIED` only when every required control has
independent evidence. Any inaccessible control is `BLOCKED`, not passed.

## CW-M4-001A current outcome

The current Codex session did not satisfy the authorization gate: it ran as
UID 1000 without effective membership in the worker group and had no separate
operator authorization to send a diagnostic workload. No socket request was
sent and no worker was activated by this milestone. The socket was observed
active/listening, but the current session could not stat the socket, and the
observed service instances had no live PID or usable cgroup.

The new runtime report is
`artifacts/validation/mediawiki-scribunto-runtime-certification-v1.json`.
The earlier exit-137/CoreCLR-GC failure is recorded separately from exit-134
and exit-1 instances whose causes remain unverified because current journal
access was unavailable. This is `HOST_CERTIFICATION_BLOCKED`, not a confirmed
security failure.

## CW-M4-001B memory-characterization status

The memory report is
`artifacts/validation/mediawiki-scribunto-memory-characterization-v1.json`.
The current session was not authorized to start a worker, create a transient
unit, or run a resource experiment, so no startup, idle, bounded-Lua,
cancellation, recovery, RSS, cgroup-peak, or `memory.events` measurements were
collected.

The deployed `SHA256SUMS` manifest passed. The deployed runtimeconfig targets
.NET 10 and contains no GC heap-hard-limit or server-GC override. The service
environment is empty, and the fixed launcher uses `--clearenv`, sets only the
fixed .NET/temp paths, and applies `prlimit --as=2147483648 --cpu=2
--nofile=256`. Systemd declares `MemoryMax=256M`; this is configuration
evidence, not a measured minimum viable limit.

The operator-supplied exit-137 journal is classified as a CoreCLR/GC startup
failure with HRESULT `0x8007000E`, but cgroup OOM is not claimed without
`memory.events` or kernel evidence. Exit-134 and exit-1 cases remain
indeterminate. Do not increase production `MemoryMax` based on this report.

An authorized disposable experiment must collect `memory.current`,
`memory.peak` where available, `memory.events`, process termination status, and
startup/post-initialization/idle/bounded-Lua/cancellation values. Any limit
comparison is diagnostic only and does not certify the installed host.

## Prerequisites and authorization

Run from an authorized operator terminal. The diagnostic worker and harness
must run under the existing `continuumwiki-scribunto` identity; never run the
worker or harness as root. The operator must have approved read-only access to
systemd and the delegated cgroup.

```bash
id
id continuumwiki-scribunto
uname -srmo
dotnet --info
systemctl --version
sestatus
```

Record only nonsecret metadata. Do not print environment variables, command
lines, or files containing credentials.

Verify deployed artifacts without changing them:

```bash
DEPLOYED=/srv/continuumwiki/current/ScribuntoWorker/bin/Release/net10.0
sha256sum "$DEPLOYED/ContinuumWiki.ScribuntoWorker.dll"
sha256sum /usr/local/libexec/continuumwiki-scribunto-worker-launcher
stat -c 'mode=%a uid=%u gid=%g size=%s path=%n' \
  "$DEPLOYED/ContinuumWiki.ScribuntoWorker.dll" \
  /usr/local/libexec/continuumwiki-scribunto-worker-launcher
```

The reviewed evidence records worker SHA-256
`f0301d935166bff81a8b9100ccbd122a1fc118e59d2d97486ba386e15c4ee37b` and
launcher SHA-256
`4a2238ccd114c46456e78b433e8c6b3f2b23a63742a01c2b8f9ba309f14af714`.
Stop if deployed hashes do not match reviewed provenance.

## Installed configuration inspection

These commands are read-only. Discover the installed units instead of
assuming repository templates are installed:

```bash
systemctl status continuumwiki-scribunto-worker.socket --no-pager
systemctl cat continuumwiki-scribunto-worker.socket
systemctl cat continuumwiki-scribunto-worker@.service
systemctl show continuumwiki-scribunto-worker.socket \
  -p ActiveState -p SubState -p Listen -p SocketUser -p SocketGroup \
  -p SocketMode -p DirectoryMode -p Accept
```

The service must retain `User=continuumwiki-scribunto`,
`Group=continuumwiki-scribunto`, `NoNewPrivileges=yes`, `PrivateDevices=yes`,
`PrivateNetwork=yes`, `ProtectSystem=strict`, `ProtectHome=yes`,
`RestrictAddressFamilies=AF_UNIX AF_NETLINK`, `MemoryMax=256M`, `TasksMax=32`,
`CPUQuota=100%`, `LimitCPU=2`, `LimitNOFILE=256`, `KillMode=control-group`, and
`UMask=077`. `ProtectKernelTunables=no` is intentional for the nested
bubblewrap procfs mount and must not be changed during certification.

Do not use `systemctl restart`, `systemctl enable`, `systemctl daemon-reload`,
`systemctl edit`, or any command that changes service state.

## Controlled socket probe

Only an approved diagnostic identity may perform this synthetic malformed
request; it must not contact a wiki or database:

```bash
sudo -u continuumwiki-scribunto env \
  CW_SCRIBUNTO_SOCKET_PATH=/run/continuumwiki/scribunto-worker.sock \
  python3 - <<'PY'
import socket
path = "/run/continuumwiki/scribunto-worker.sock"
with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
    client.settimeout(10)
    client.connect(path)
    client.sendall(b"{}\n")
    client.shutdown(socket.SHUT_WR)
    print(client.recv(4096).decode("utf-8", "replace"))
PY
```

The expected result is structured `ProtocolError` JSON. Connectivity alone is
not host certification.

After the probe, discover the actual instance rather than using a placeholder:

```bash
systemctl list-units 'continuumwiki-scribunto-worker@*.service' --all --no-legend --plain
unit="$(systemctl list-units 'continuumwiki-scribunto-worker@*.service' --all --no-legend --plain | awk 'NR == 1 {print $1}')"
test -n "$unit"
systemctl show "$unit" -p MainPID -p ControlGroup -p User -p Group -p MemoryMax -p TasksMax -p CPUQuota -p LimitCPU -p LimitNOFILE -p KillMode
control_group="$(systemctl show "$unit" -p ControlGroup --value)"
test -n "$control_group"
cgroup_path="/sys/fs/cgroup${control_group}"
cat "$cgroup_path/memory.max" "$cgroup_path/pids.max" "$cgroup_path/cpu.max"
cat "$cgroup_path/cgroup.procs"
```

Use the instance whose journal timestamp corresponds to the probe. The worker
and children must be in the intended delegated cgroup. Expected finite values
are `memory.max <= 268435456`, `pids.max <= 32`, a finite two-field `cpu.max`,
`LimitCPU=2`, and `LimitNOFILE=256`.

## Host certification harness

Run the existing harness as the dedicated identity with the observed cgroup
path. These paths use the reviewed Release worker and checked-out Debug
certification/probe assemblies; build them through the normal reviewed process
if absent, without deploying them:

```bash
repo_root="$PWD"
worker_path=/srv/continuumwiki/current/ScribuntoWorker/bin/Release/net10.0/ContinuumWiki.ScribuntoWorker.dll
certification_path="$repo_root/ScribuntoSandboxCertification/bin/Debug/net10.0/ContinuumWiki.ScribuntoSandboxCertification.dll"
probe_path="$repo_root/ScribuntoSandboxProbe/bin/Debug/net10.0/ContinuumWiki.ScribuntoSandboxProbe.dll"
report_path=/srv/continuumwiki/certification/moonsharp-host-sandbox-report-v1.json
sudo -u continuumwiki-scribunto env \
  CW_SCRIBUNTO_WORKER_PATH="$worker_path" \
  CW_SCRIBUNTO_CERTIFICATION_PATH="$certification_path" \
  CW_SCRIBUNTO_PROBE_PATH="$probe_path" \
  CW_SCRIBUNTO_SOCKET_PATH=/run/continuumwiki/scribunto-worker.sock \
  CW_SCRIBUNTO_WORKER_CGROUP_PATH="$cgroup_path" \
  CW_SCRIBUNTO_HOST_REPORT="$report_path" \
  "$repo_root/scripts/verify-moonsharp-host-sandbox.sh"
```

The harness must complete cgroup, namespace, filesystem, network, environment,
process, library, memory, task, CPU, timeout, recovery, concurrency, protocol,
and capability probes. Preserve its report and runner report. An unavailable
check or any `FAIL` is not certification.

```bash
jq '{verdict,summary,checks}' "$report_path"
journalctl -u "$unit" --since '-10 minutes' --no-pager
```

The separate `scripts/diagnose-moonsharp-procfs.sh` procedure is an
administrator-controlled diagnostic for the documented
`ProtectKernelTunables=yes` versus `no` interaction. Run it only when its
transient diagnostic units are separately authorized; never use it to change
the installed worker unit.

## Evidence matrix and decision

Record each control as exactly one of `PASS`, `FAIL`, `BLOCKED`, or
`NOT_APPLICABLE`: dedicated process UID/GID and effective capabilities;
installed systemd properties; socket ownership/mode and positive/negative
access; effective bubblewrap namespace and mount denial; network denial;
filesystem and secret denial; application and OS resource enforcement; typed
capability and malformed/unauthorized request rejection; SELinux enforcement
and relevant contexts; and production Lua-disabled/rejecting runtime state.

`HOST_CERTIFIED` requires all required controls to be `PASS`. An inaccessible
control is `BLOCKED`, never inferred from a template, source inspection, prior
report, or socket connectivity. A demonstrated security violation is
`HOST_CERTIFICATION_FAILED`. If no substantive checks were possible, use
`HOST_CERTIFICATION_NOT_ATTEMPTED`.

## Prohibited operations

Do not run the worker as root, alter socket ownership or mode, disable SELinux,
modify systemd or bubblewrap hardening, grant network or filesystem access,
read real secrets, run destructive exhaustion against the installed worker,
deploy binaries, restart production services, enable Lua/Cargo, or modify
source/reference/Cargo evidence to make certification pass.
