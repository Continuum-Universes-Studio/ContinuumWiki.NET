#!/usr/bin/env bash
set -Eeuo pipefail

# Disposable host diagnostic for the nested systemd -> bubblewrap procfs
# interaction. Run as root on the installed host. It creates transient units,
# prints the effective parent procfs mount tree, and removes those units after
# each candidate. It never starts the Lua worker or contacts application data.
# The baseline failure is expected to contain "VFS: Mount too revealing".

if [[ "$(id -u)" != 0 ]]; then
    echo "Run this diagnostic as root so systemd applies the service identity and sandbox properties." >&2
    exit 2
fi

source_unit="${CW_SCRIBUNTO_PROC_SOURCE_UNIT:-}"
if [[ -z "$source_unit" ]]; then
    source_unit="$(systemctl list-units 'continuumwiki-scribunto-worker@*.service' \
        --all --no-legend --plain | awk 'NR == 1 {print $1}')"
fi
if [[ -z "$source_unit" ]]; then
    echo "No instantiated worker unit is available; activate the socket once, then rerun." >&2
    exit 2
fi
worker_user="$(systemctl show "$source_unit" -p User --value)"
worker_group="$(systemctl show "$source_unit" -p Group --value)"
protect_proc="$(systemctl show "$source_unit" -p ProtectProc --value)"
proc_subset="$(systemctl show "$source_unit" -p ProcSubset --value)"

[[ -n "$worker_user" && -n "$worker_group" ]] || {
    echo "Could not resolve User/Group from $source_unit" >&2
    exit 2
}

required_tools=(bwrap findmnt systemctl systemd-run)
for tool in "${required_tools[@]}"; do
    command -v "$tool" >/dev/null 2>&1 || {
        echo "Missing required tool: $tool" >&2
        exit 2
    }
done

run_candidate() {
    local candidate="$1"
    local diagnostic_unit="continuumwiki-scribunto-proc-${candidate}-$$.service"
    local expected_status="$2"
    local output
    local status

    echo "=== candidate: ProtectKernelTunables=$candidate ==="
    set +e
    output="$(systemd-run \
        --unit="$diagnostic_unit" \
        --collect \
        --wait \
        --pipe \
        --property="User=$worker_user" \
        --property="Group=$worker_group" \
        --property=NoNewPrivileges=yes \
        --property=PrivateTmp=yes \
        --property=PrivateDevices=yes \
        --property=ProtectSystem=strict \
        --property=ProtectHome=yes \
        --property="ProtectKernelTunables=$candidate" \
        --property=ProtectKernelModules=yes \
        --property="ProtectProc=$protect_proc" \
        --property="ProcSubset=$proc_subset" \
        --property=RestrictSUIDSGID=yes \
        --property="RestrictAddressFamilies=AF_UNIX AF_NETLINK" \
        --property=PrivateNetwork=yes \
        --property=MemoryMax=256M \
        --property=TasksMax=32 \
        --property=CPUQuota=100% \
        --property=LimitCPU=2 \
        --property=LimitNOFILE=256 \
        --property=KillMode=control-group \
        --property=TimeoutStopSec=5s \
        --property=UMask=077 \
        /bin/sh -c '
            set -eu
            echo "parent procfs:"
            /usr/bin/findmnt -R /proc -o TARGET,SOURCE,FSTYPE,OPTIONS
            /usr/bin/bwrap --unshare-all --die-with-parent \
                --ro-bind /usr /usr \
                --ro-bind /lib /lib \
                --ro-bind-try /lib64 /lib64 \
                --proc /proc --remount-ro /proc \
                --dev /dev --tmpfs /tmp -- /usr/bin/true
            /usr/bin/findmnt -T /proc -o TARGET,SOURCE,FSTYPE,OPTIONS
            echo PROC_MOUNT_OK
        ' 2>&1)"
    status=$?
    set -e

    printf '%s\n' "$output"
    if [[ "$status" -ne "$expected_status" ]]; then
        echo "Unexpected exit status for ProtectKernelTunables=$candidate: $status" >&2
        return 1
    fi
}

# The baseline must reproduce the failure. Only ProtectKernelTunables changes
# between candidates; all other service restrictions remain fixed.
run_candidate yes 1
run_candidate no 0
echo "PROCFS_DIAGNOSTIC_VERIFIED"
