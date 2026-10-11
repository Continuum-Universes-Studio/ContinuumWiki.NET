# CW-M3-006C worker deployment reconciliation

This procedure reconciles the deployed worker with a reviewed Release publish
without changing the systemd unit, launcher, bubblewrap policy, production Lua
authorization, or any MariaDB database. It is operator-run only. Codex does
not execute the privileged commands below.

## Reviewed artifact

Repository HEAD: `987ce988bc2b6c6d49f827243a81149025e99a80`.

The non-privileged publish bundle is:

```text
artifacts/publish/scribunto-worker-987ce98-20261010231844/
```

It is framework-dependent for .NET 10 and contains the worker, its project
dependencies, MoonSharp 2.0.0, and the resolved runtime assemblies. Verify the
bundle before any deployment action:

```bash
cd /home/cdjensen94/Projects/ContinuumWiki.NET
PUBLISH_DIR="$PWD/artifacts/publish/scribunto-worker-987ce98-20261010231844"
test -f "$PUBLISH_DIR/SHA256SUMS"
(cd "$PUBLISH_DIR" && sha256sum -c SHA256SUMS)
```

The manifest is relative to the publish directory and excludes itself.

## Stage and activate atomically

Review the source launcher and unit before proceeding. These commands do not
install or alter them:

```bash
cd /home/cdjensen94/Projects/ContinuumWiki.NET
PUBLISH_DIR="$PWD/artifacts/publish/scribunto-worker-987ce98-20261010231844"
DEPLOY_ROOT=/srv/continuumwiki
STAGE="$DEPLOY_ROOT/staging/scribunto-worker-987ce98-20261010231844"
ROLLBACK="$DEPLOY_ROOT/rollback/scribunto-worker-$(date +%Y%m%d%H%M%S)"

sudo test -f "$PUBLISH_DIR/SHA256SUMS"
sudo install -d -o root -g root -m 0755 "$DEPLOY_ROOT/staging" "$DEPLOY_ROOT/rollback"
sudo install -d -o root -g root -m 0755 \
  "$STAGE/ScribuntoWorker/bin/Release/net10.0"
sudo cp -a "$PUBLISH_DIR/." "$STAGE/ScribuntoWorker/bin/Release/net10.0/"
sudo chown -R root:root "$STAGE"
sudo find "$STAGE" -type d -exec chmod 0755 {} +
sudo find "$STAGE" -type f -exec chmod 0644 {} +
sudo chmod 0755 "$STAGE/ScribuntoWorker/bin/Release/net10.0/ContinuumWiki.ScribuntoWorker"
cd "$STAGE/ScribuntoWorker/bin/Release/net10.0" && sudo sha256sum -c SHA256SUMS
```

The following activation window prevents mixed worker assemblies. It changes
only `/srv/continuumwiki/current/ScribuntoWorker`; the systemd unit, launcher,
socket path, resource limits, and bubblewrap policy are untouched.

```bash
sudo systemctl stop continuumwiki-scribunto-worker.socket
sudo mv /srv/continuumwiki/current/ScribuntoWorker "$ROLLBACK"
sudo mv "$STAGE/ScribuntoWorker" /srv/continuumwiki/current/ScribuntoWorker
sudo systemctl start continuumwiki-scribunto-worker.socket
```

The old directory is the rollback candidate. Do not delete it until the
deployment has passed all verification gates.

## Permissions and deployed-manifest verification

The worker identity must be able to read, but not write, the staged deployment:

```bash
sudo -u continuumwiki-scribunto find \
  /srv/continuumwiki/current/ScribuntoWorker/bin/Release/net10.0 \
  -type f ! -readable -print
sudo -u continuumwiki-scribunto find \
  /srv/continuumwiki/current/ScribuntoWorker/bin/Release/net10.0 \
  -type f -writable -print

sudo find /srv/continuumwiki/current/ScribuntoWorker/bin/Release/net10.0 \
  \( ! -user root -o -perm /022 \) -print

sudo sh -c 'cd /srv/continuumwiki/current/ScribuntoWorker/bin/Release/net10.0 && sha256sum -c SHA256SUMS'
sha256sum \
  /srv/continuumwiki/current/ScribuntoWorker/bin/Release/net10.0/ContinuumWiki.ScribuntoWorker.dll
```

The first three commands must print no paths. The deployed worker DLL must
match the reviewed manifest entry:

```text
0f3141cb9b211cd23beb28caad4346232668b670c1a95f49d1041b0d46a85cd7
```

Also verify that the fixed launcher still points at the expected directory and
that the effective unit hardening is unchanged:

```bash
sudo sha256sum /usr/local/libexec/continuumwiki-scribunto-worker-launcher
systemctl cat continuumwiki-scribunto-worker@.service
systemctl show continuumwiki-scribunto-worker@.service \
  -p User,Group,PrivateNetwork,PrivateDevices,ProtectSystem,ProtectHome,\
NoNewPrivileges,RestrictAddressFamilies,MemoryMax,TasksMax,CPUQuota,LimitCPU,\
LimitNOFILE,KillMode
```

Do not change worker socket ownership or permissions as part of this
reconciliation.

## Certification and socket tests

After deployment, run the existing host certification as the dedicated
identity, with the exact active cgroup path discovered from a disposable
socket request. Preserve its report and journal. The certification remains
synthetic and does not establish MediaWiki parity:

```bash
test -S /run/continuumwiki/scribunto-worker.sock
systemctl status continuumwiki-scribunto-worker.socket --no-pager

CW_SCRIBUNTO_SOCKET_PATH=/run/continuumwiki/scribunto-worker.sock \
  dotnet test TightWiki.Tests/TightWiki.Tests.csproj \
  -c Debug --no-restore -m:1 \
  --filter 'FullyQualifiedName~MediaWikiScribuntoSocketRendererIntegrationTests'

CW_SCRIBUNTO_SOCKET_PATH=/run/continuumwiki/scribunto-worker.sock \
  scripts/verify-m3-006c.sh
```

The real-module harness must still be assessed separately: a successful
worker response is not an exact/equivalent/partial MediaWiki result without
an independently captured MediaWiki reference for the same invocation and
revision snapshot.

## Rollback

If hash verification, hardening verification, host certification, or socket
tests fail, keep the evidence and restore the prior worker directory:

```bash
sudo systemctl stop continuumwiki-scribunto-worker.socket
sudo mv /srv/continuumwiki/current/ScribuntoWorker \
  /srv/continuumwiki/rollback/failed-worker-$(date +%Y%m%d%H%M%S)
sudo mv "$ROLLBACK" /srv/continuumwiki/current/ScribuntoWorker
sudo systemctl start continuumwiki-scribunto-worker.socket
```

Then verify the rollback directory's original manifest or previously recorded
hashes. Do not remove either deployment while investigation is ongoing.

## Ignore and tracking audit

The repository ignores `ScribuntoWorker/bin/` and `ScribuntoWorker/obj/`.
`artifacts/imports/` is not covered by `.gitignore`; the current imported XML
files are not tracked (`git ls-files artifacts/imports/**` is empty). This
procedure does not change ignore rules and does not delete or untrack any
files.

The checkout currently contains 503 tracked files beneath `bin/` or `obj/` in
other projects. They are reported as pre-existing tracked generated outputs;
they are not removed or untracked automatically.

## CW-M3-006D reviewed worker amendment

The following bundle is the separately reviewed CW-M3-006D artifact. It adds
the explicitly negotiated `frame.callParserFunction` protocol capability. The
host and worker must be upgraded together; an older installed worker will
reject requests containing that capability as `UnsupportedCapability`.

```bash
cd /home/cdjensen94/Projects/ContinuumWiki.NET
PUBLISH_DIR="$PWD/artifacts/publish/scribunto-worker-cw-m3-006d-20261011000000"
(cd "$PUBLISH_DIR" && sha256sum -c SHA256SUMS)
```

The reviewed worker DLL SHA-256 is:

```text
bb57ac5ce64de1405fd1503b055f602f9d8d379a70dd51d1e1934fd36ed21801
```

Use the same stage, ownership, permission, stop/move/start, rollback, and
hardening-verification procedure above, substituting:

```text
/srv/continuumwiki/staging/scribunto-worker-cw-m3-006d-20261011000000
```

for the staging directory and the CW-M3-006D `PUBLISH_DIR`. Before rerunning
the diagnostic harness, verify the deployed DLL directly:

```bash
sha256sum \
  /srv/continuumwiki/current/ScribuntoWorker/bin/Release/net10.0/ContinuumWiki.ScribuntoWorker.dll
```

It must equal the hash above. A socket connection alone does not prove that
the updated worker is installed. After the hash matches, run:

```bash
CW_SCRIBUNTO_SOCKET_PATH=/run/continuumwiki/scribunto-worker.sock \
  scripts/verify-m3-006c-diagnostic.sh
```

Do not treat a completed diagnostic harness as compatibility parity: the
independent MediaWiki references and the host certification gate remain
separate requirements.

## CW-M3-006F.2 reviewed worker amendment

CW-M3-006F.2 adds the explicit `mw.ext.cargo` capability. Its default provider
rejects because no authorized historical Cargo dataset exists. The worker and
host must be deployed as one complete bundle; do not mix this protocol with an
older installed worker.

```bash
cd /home/cdjensen94/Projects/ContinuumWiki.NET
PUBLISH_DIR="$PWD/artifacts/publish/scribunto-worker-cw-m3-006f2-20261011000000"
(cd "$PUBLISH_DIR" && sha256sum -c SHA256SUMS)
```

Expected worker DLL hash:

```text
f0301d935166bff81a8b9100ccbd122a1fc118e59d2d97486ba386e15c4ee37b
```

Use the existing staged stop/move/start procedure above, substituting:

```text
/srv/continuumwiki/staging/scribunto-worker-cw-m3-006f2-20261011000000
```

for the staging directory. Preserve root ownership, read-only access for the
dedicated worker identity, the existing launcher, systemd unit, socket mode,
bubblewrap policy, and rollback candidate. After activation, verify the exact
installed hash before any socket test:

```bash
sha256sum \
  /srv/continuumwiki/current/ScribuntoWorker/bin/Release/net10.0/ContinuumWiki.ScribuntoWorker.dll
```

Then run the diagnostic harness. It should still report Cargo as unavailable;
that is the intended fail-closed result until a versioned historical Cargo
dataset and an authorized provider are supplied:

```bash
CW_SCRIBUNTO_SOCKET_PATH=/run/continuumwiki/scribunto-worker.sock \
  scripts/verify-m3-006c-diagnostic.sh
```
