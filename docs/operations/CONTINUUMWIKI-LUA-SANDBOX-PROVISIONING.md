# ContinuumWiki Lua Engine host provisioning (CW-M3-005B.6B/005B.6C)

## Safety boundary

`CW-M3-005B.6C VERIFIED — HOST SANDBOX` on the unrestricted verification host;
repeat the commands below for each production host and retain its report before
approval. The commands are intentionally not executed by the application or
by wiki content. They require explicit administrator approval and should be
reviewed against the site's actual service account, deployment root, and
rollback policy first.

These commands do not access production MediaWiki or MariaDB data. They
install only the fixed worker service/socket and launcher already present in
this repository. They do not grant the web application general `sudo`,
systemd, or command-execution privileges.

## 1. Review the dynamically resolved runtime and artifacts

Run as the intended unprivileged certification identity or deployment account:

```bash
command -v dotnet
readlink -f "$(command -v dotnet)"
dotnet --info
test -f /srv/continuumwiki/current/ScribuntoWorker/bin/Release/net10.0/ContinuumWiki.ScribuntoWorker.dll
test -f /srv/continuumwiki/current/ScribuntoSandboxCertification/bin/Release/net10.0/ContinuumWiki.ScribuntoSandboxCertification.dll
test -f /srv/continuumwiki/current/ScribuntoSandboxProbe/bin/Release/net10.0/ContinuumWiki.ScribuntoSandboxProbe.dll
```

The fixed launcher derives the runtime root from `command -v dotnet`; it does
not assume `/opt/dotnet`. The release output must be copied to the fixed
deployment root above by the normal reviewed deployment process.

## 2. Provision the dedicated identity and reviewed file permissions

The service unit provides the worker identity through systemd's fixed
`User=continuumwiki-scribunto` and `Group=continuumwiki-scribunto` settings. The
launcher does not request a synthetic 65534 UID/GID and therefore does not
require allocating 65534 as a subordinate host identity. Review and verify the
actual dedicated account:

```bash
sudo /usr/sbin/groupadd --system --force continuumwiki-scribunto
sudo /usr/sbin/useradd --system --no-create-home --home-dir /var/lib/continuumwiki-scribunto \
  --shell /usr/sbin/nologin --gid continuumwiki-scribunto continuumwiki-scribunto
sudo install -d -o continuumwiki-scribunto -g continuumwiki-scribunto -m 0750 \
  /var/lib/continuumwiki-scribunto
sudo install -d -o root -g root -m 0755 /srv/continuumwiki/current
sudo chown -R root:root /srv/continuumwiki/current/ScribuntoWorker \
  /srv/continuumwiki/current/ScribuntoSandboxCertification \
  /srv/continuumwiki/current/ScribuntoSandboxProbe
sudo chmod -R go-w /srv/continuumwiki/current/ScribuntoWorker \
  /srv/continuumwiki/current/ScribuntoSandboxCertification \
  /srv/continuumwiki/current/ScribuntoSandboxProbe
sudo install -d -o continuumwiki-scribunto -g continuumwiki-scribunto -m 0750 \
  /srv/continuumwiki/certification
sudo install -D -o root -g root -m 0755 \
  scripts/verify-moonsharp-host-sandbox.sh \
  /srv/continuumwiki/current/scripts/verify-moonsharp-host-sandbox.sh
sudo install -D -o root -g root -m 0755 \
  scripts/diagnose-moonsharp-procfs.sh \
  /srv/continuumwiki/current/scripts/diagnose-moonsharp-procfs.sh
```

Verify the resulting identity without running Lua. The host UID/GID must be
nonzero and the service must have no effective capabilities:

```bash
getent passwd continuumwiki-scribunto
getent group continuumwiki-scribunto
id continuumwiki-scribunto
grep '^continuumwiki-scribunto:' /etc/subuid /etc/subgid
sudo -u continuumwiki-scribunto sh -c 'id; awk "/^CapEff:/ {print}" /proc/self/status'
```

No database configuration, appsettings file, credentials, home directory, or
repository source tree is mounted into the worker.

## 3. Install the fixed unit templates and launcher

Review the source files first, then install them as root-owned files:

```bash
sudo install -D -o root -g root -m 0755 \
  deploy/systemd/continuumwiki-scribunto-worker-launcher \
  /usr/local/libexec/continuumwiki-scribunto-worker-launcher
sudo install -D -o root -g root -m 0644 \
  deploy/systemd/continuumwiki-scribunto-worker.socket \
  /etc/systemd/system/continuumwiki-scribunto-worker.socket
sudo install -D -o root -g root -m 0644 \
  deploy/systemd/continuumwiki-scribunto-worker@.service \
  /etc/systemd/system/continuumwiki-scribunto-worker@.service
sudo systemd-analyze verify \
  /etc/systemd/system/continuumwiki-scribunto-worker.socket \
  /etc/systemd/system/continuumwiki-scribunto-worker@.service
sudo systemctl daemon-reload
sudo systemctl enable --now continuumwiki-scribunto-worker.socket
```

The socket is owned by `continuumwiki-scribunto`. The reviewed web service
account must be added only to that group if it needs to submit requests:

```bash
sudo usermod --append --groups continuumwiki-scribunto <reviewed-web-service-user>
sudo systemctl restart <reviewed-web-service-unit>
```

Do not add the web service account to `wheel`, `sudo`, `systemd-journal`, or a
general systemd-admin group. Do not expose `systemctl`, `systemd-run`, or the
system bus to wiki content.

## 4. Verify the fixed unit and effective cgroup

Run read-only checks as the operator. The service must be active under the
dedicated identity and the cgroup must be a system-managed cgroup, not a user
scope:

```bash
systemctl status continuumwiki-scribunto-worker.socket --no-pager
systemctl cat continuumwiki-scribunto-worker.socket
systemctl cat continuumwiki-scribunto-worker@.service
systemctl list-dependencies continuumwiki-scribunto-worker.socket
systemctl show continuumwiki-scribunto-worker.socket \
  -p User,Group,SocketUser,SocketGroup,Listen,Accept
systemctl show continuumwiki-scribunto-worker@.service \
  -p ProtectKernelTunables,ProtectProc,ProcSubset,PrivateNetwork,MemoryMax,TasksMax
```

Activate one disposable instance through the socket using only malformed
synthetic protocol data; this must not contact a wiki or database:

```bash
python3 - <<'PY'
import socket
path = "/run/continuumwiki/scribunto-worker.sock"
with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
    client.settimeout(5)
    client.connect(path)
    client.sendall(b"{}\n")
    client.shutdown(socket.SHUT_WR)
    print(client.recv(4096).decode("utf-8", "replace"))
PY
systemctl list-units 'continuumwiki-scribunto-worker@*.service' --all --no-legend
```

The service must not retain `ProtectKernelTunables=yes`: systemd implements
that setting with locked child mounts below `/proc` (`/proc/sys`, `/proc/acpi`,
`/proc/irq`, and related paths). The kernel rejects a nested bubblewrap procfs
mount when those entries are already masked, reporting `VFS: Mount too
revealing`. After the disposable socket activation above has created an
instance, compare the candidates without changing global host security:

```bash
sudo /srv/continuumwiki/current/scripts/diagnose-moonsharp-procfs.sh
```

The expected evidence is a failing baseline for
`ProtectKernelTunables=yes`, a successful `PROC_MOUNT_OK` result for
`ProtectKernelTunables=no`, and the final `PROCFS_DIAGNOSTIC_VERIFIED` marker.
The corrected worker still creates its private PID namespace in bubblewrap and
remounts the final `/proc` read-only; disabling the systemd procfs child masks
does not expose host processes to the worker.

The response should be a structured protocol rejection, and the activated
instance must show the dedicated `User`/`Group` and fixed resource settings.

After one disposable socket connection has activated an instance, identify it
without guessing the escaped unit name:

```bash
systemctl list-units 'continuumwiki-scribunto-worker@*.service' --all --no-legend
unit="$(systemctl list-units 'continuumwiki-scribunto-worker@*.service' --all --no-legend | awk 'NR==1 {print $1}')"
systemctl show "$unit" -p MainPID,ControlGroup,User,Group,MemoryMax,TasksMax,CPUQuota,LimitCPU,LimitNOFILE,KillMode
control_group="$(systemctl show "$unit" -p ControlGroup --value)"
cgroup_path="/sys/fs/cgroup${control_group}"
cat "$cgroup_path/memory.max" "$cgroup_path/pids.max" "$cgroup_path/cpu.max"
cat "$cgroup_path/cgroup.procs"
```

Expected effective values are `memory.max <= 268435456`, `pids.max <= 32`, a
finite two-field `cpu.max`, `LimitCPU=2`, `LimitNOFILE=256`, and a non-root
`User`. The worker PID and every child PID must remain in the same intended
cgroup while active. The fixed launcher retains `RLIMIT_AS=2147483648` as a
virtual-address ceiling; cgroup `memory.max=268435456` remains the effective
physical-memory containment. Preserve this command output as certification
evidence.

## 5. Run host certification

Run the harness as the dedicated unprivileged identity with the exact cgroup
path observed above. Do not run it as root:

```bash
sudo -u continuumwiki-scribunto \
  env CW_SCRIBUNTO_WORKER_CGROUP_PATH="$cgroup_path" \
  CW_SCRIBUNTO_HOST_REPORT=/srv/continuumwiki/certification/moonsharp-host-sandbox-report-v1.json \
  /srv/continuumwiki/current/scripts/verify-moonsharp-host-sandbox.sh
```

If the deployment does not copy the repository scripts, run the reviewed
checkout path instead. The harness must execute all 15 mandatory isolation
checks; `HOST_CERTIFICATION_UNAVAILABLE`, a missing check, or any `FAIL` is
not certification. Preserve the report, runner report, unit status, cgroup
values, and service journal:

```bash
jq '{verdict,summary,checks}' /srv/continuumwiki/certification/moonsharp-host-sandbox-report-v1.json
sudo journalctl -u "$unit" --since '-10 minutes' --no-pager
```

The harness uses synthetic source only. It tests the actual bubblewrap chain,
filesystem reads/writes, network sends, environment leakage, process/library
access, memory pressure, task pressure, CPU/time limits, bounded output,
timeout, recovery, malformed input, concurrency, and state isolation.

## 6. Failure handling and rollback

If any check fails or is unavailable:

```bash
sudo systemctl disable --now continuumwiki-scribunto-worker.socket
sudo rm -f /etc/systemd/system/continuumwiki-scribunto-worker.socket \
  /etc/systemd/system/continuumwiki-scribunto-worker@.service
sudo systemctl daemon-reload
```

Retain the report and journal before cleanup. Do not disable SELinux, seccomp,
user namespaces, cgroup controllers, or network isolation to make a check pass.
Do not enable the Lua executor in normal application startup. A successful
host report still requires a separate explicit production approval.
