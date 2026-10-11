#!/usr/bin/env bash
set -Eeuo pipefail

# Explicit operator entry point. The ordinary verify-m3-006c.sh remains strict.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
socket_path="${CW_SCRIBUNTO_SOCKET_PATH:-}"
if [[ -z "$socket_path" ]]; then
    echo 'CW-M3-006C diagnostic transport: missing CW_SCRIBUNTO_SOCKET_PATH.' >&2
    exit 21
fi
socket_directory="$(dirname -- "$socket_path")"
if [[ ! -x "$socket_directory" ]]; then
    echo "CW-M3-006C diagnostic transport: filesystem/permission-denied: $socket_directory" >&2
    exit 23
fi
if [[ ! -S "$socket_path" ]]; then
    echo "CW-M3-006C diagnostic transport: filesystem/socket-not-found: $socket_path" >&2
    exit 22
fi
if [[ ! -r "$socket_path" || ! -w "$socket_path" ]]; then
    echo "CW-M3-006C diagnostic transport: filesystem/permission-denied: $socket_path" >&2
    exit 23
fi

probe_result="$(SOCKET_PATH="$socket_path" python3 - <<'PY'
import json, os, socket, sys
path = os.environ["SOCKET_PATH"]
try:
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
        client.settimeout(3)
        client.connect(path)
        client.sendall(b"{}\n")
        client.shutdown(socket.SHUT_WR)
        payload = client.recv(8192).decode("utf-8", "replace").strip()
except PermissionError:
    print("connect-permission-denied")
    sys.exit(24)
except ConnectionRefusedError:
    print("connection-refused")
    sys.exit(25)
except OSError as error:
    print(f"connection-failure:{error.__class__.__name__}")
    sys.exit(26)
if not payload:
    print("worker-activation-failure:empty-response")
    sys.exit(27)
try:
    response = json.loads(payload)
except json.JSONDecodeError:
    print("protocol-rejection:invalid-json")
    sys.exit(28)
if response.get("protocolVersion") != 1:
    print("protocol-rejection:unsupported-version")
    sys.exit(29)
if response.get("status") != "ProtocolError":
    print(f"protocol-rejection:unexpected-probe-status:{response.get('status')}")
    sys.exit(30)
print("connected-worker-protocol-ok")
PY
)" || {
    status=$?
    echo "CW-M3-006C diagnostic transport: $probe_result" >&2
    exit "$status"
}
echo "CW-M3-006C diagnostic transport: $probe_result"

export CW_M3_006C_DIAGNOSTIC=1
exec "$repo_root/scripts/verify-m3-006c.sh"
