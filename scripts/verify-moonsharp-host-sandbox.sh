#!/usr/bin/env bash
set -Eeuo pipefail

# CW-M3-005B.5 host certification harness. Run only from a permitted Linux
# terminal or administrator-provisioned test service. It never contacts
# MediaWiki/MariaDB and never creates a systemd unit.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
worker_path="${CW_SCRIBUNTO_WORKER_PATH:-$repo_root/ScribuntoWorker/bin/Debug/net10.0/ContinuumWiki.ScribuntoWorker.dll}"
certification_path="${CW_SCRIBUNTO_CERTIFICATION_PATH:-$repo_root/ScribuntoSandboxCertification/bin/Debug/net10.0/ContinuumWiki.ScribuntoSandboxCertification.dll}"
probe_path="${CW_SCRIBUNTO_PROBE_PATH:-$repo_root/ScribuntoSandboxProbe/bin/Debug/net10.0/ContinuumWiki.ScribuntoSandboxProbe.dll}"
cgroup_path="${CW_SCRIBUNTO_WORKER_CGROUP_PATH:-}"
report_path="${CW_SCRIBUNTO_HOST_REPORT:-$repo_root/artifacts/validation/moonsharp-host-sandbox-report-v1.json}"
memory_limit=268435456
virtual_address_limit=1073741824
task_limit=32
checks_file="$(mktemp "${TMPDIR:-/tmp}/cw-m3-005b5-checks.XXXXXX")"
probe_output="$(mktemp "${TMPDIR:-/tmp}/cw-m3-005b5-probe.XXXXXX")"
trap 'rm -f "$checks_file" "$probe_output"' EXIT

add_check() {
    local name="$1" required="$2" status="$3" evidence="$4" message="$5"
    jq -cn --arg name "$name" --argjson required "$required" --arg status "$status" \
        --arg evidence "$evidence" --arg message "$message" \
        '{name:$name,required:$required,status:$status,evidence:$evidence,message:$message}' >>"$checks_file"
}

write_report() {
    local verdict="$1"
    mkdir -p "$(dirname "$report_path")"
    jq -s --arg version "cw-m3-005b.5-host-sandbox-v1" \
        --arg verdict "$verdict" --arg worker "$worker_path" \
        --arg cgroup "$cgroup_path" --arg dotnet_root "${dotnet_root:-}" \
        '{reportVersion:$version,verdict:$verdict,worker:{assembly:$worker,identity:"systemd User=continuumwiki-scribunto"},cgroupPath:$cgroup,dotnetRoot:$dotnet_root,summary:{total:length,passed:(map(select(.status=="PASS"))|length),failed:(map(select(.status=="FAIL"))|length),unavailable:(map(select(.status=="UNAVAILABLE"))|length)},checks:.}' \
        "$checks_file" >"$report_path"
}

mark_worker_checks_unavailable() {
    local reason="$1"
    for name in memory-cgroup task-cgroup namespace-support os-identity os-filesystem-read os-filesystem-write os-network os-environment os-process os-library os-memory os-tasks os-cpu worker-adversarial-suite; do
        add_check "$name" true UNAVAILABLE "$reason" "Mandatory check was not run because host prerequisites were unavailable."
    done
}

if [[ "$(uname -s)" != "Linux" ]]; then
    add_check linux true UNAVAILABLE "$(uname -s)" "Host certification requires Linux namespaces and cgroup v2."
    write_report HOST_CERTIFICATION_UNAVAILABLE
    exit 1
fi
add_check linux true PASS "$(uname -s)" "Linux host detected."

required_tools=(dotnet bwrap prlimit systemctl systemd-run timeout jq)
missing_tools=()
for tool in "${required_tools[@]}"; do
    command -v "$tool" >/dev/null 2>&1 || missing_tools+=("$tool")
done
if ((${#missing_tools[@]} > 0)); then
    add_check required-tools true UNAVAILABLE "${missing_tools[*]}" "Required host certification tools were unavailable."
    write_report HOST_CERTIFICATION_UNAVAILABLE
    exit 1
fi
add_check required-tools true PASS "${required_tools[*]}" "Required host tools are present."

dotnet_path="$(readlink -f "$(command -v dotnet)")"
dotnet_root="${DOTNET_ROOT_X64:-${DOTNET_ROOT:-$(dirname "$dotnet_path")}}"
if [[ ! -x "$dotnet_root/dotnet" || ! -d "$dotnet_root" ]]; then
    add_check dotnet-runtime true UNAVAILABLE "$dotnet_root" "The .NET runtime root could not be resolved dynamically."
    write_report HOST_CERTIFICATION_UNAVAILABLE
    exit 1
fi
add_check dotnet-runtime true PASS "$dotnet_root" "Resolved the .NET runtime without a fixed installation-path assumption."

for artifact in "$worker_path" "$certification_path" "$probe_path"; do
    if [[ ! -f "$artifact" ]]; then
        add_check "artifact-$(basename "$artifact")" true UNAVAILABLE "$artifact" "Expected certification artifact was missing."
        write_report HOST_CERTIFICATION_UNAVAILABLE
        exit 1
    fi
done
add_check worker-artifacts true PASS "$worker_path" "Worker, certification runner, and OS probe assemblies are present."

if [[ "$(id -u)" == 0 ]]; then
    add_check unprivileged-launcher true UNAVAILABLE "uid=0" "Run certification as the dedicated unprivileged worker identity, never as root."
    write_report HOST_CERTIFICATION_UNAVAILABLE
    exit 1
fi
add_check unprivileged-launcher true PASS "uid=$(id -u),gid=$(id -g)" "The launcher was not started as root."

if [[ -z "$cgroup_path" || ! -d "$cgroup_path" ]]; then
    add_check worker-cgroup true UNAVAILABLE "$cgroup_path" "Provide an administrator-provisioned dedicated cgroup v2 directory through CW_SCRIBUNTO_WORKER_CGROUP_PATH."
    mark_worker_checks_unavailable "worker cgroup unavailable"
    write_report HOST_CERTIFICATION_UNAVAILABLE
    exit 1
fi
memory_max="$(<"$cgroup_path/memory.max")"
pids_max="$(<"$cgroup_path/pids.max")"
cpu_max="$(<"$cgroup_path/cpu.max")"
if [[ ! "$memory_max" =~ ^[0-9]+$ || "$memory_max" -gt "$memory_limit" ]]; then
    add_check memory-cgroup true FAIL "$memory_max" "memory.max was unlimited, invalid, or above the certification ceiling."
    write_report BLOCKED
    exit 1
fi
if [[ ! "$pids_max" =~ ^[0-9]+$ || "$pids_max" -gt "$task_limit" ]]; then
    add_check task-cgroup true FAIL "$pids_max" "pids.max was unlimited, invalid, or above the certification ceiling."
    write_report BLOCKED
    exit 1
fi
if [[ "$cpu_max" =~ ^[[:space:]]*[0-9]+[[:space:]]+[0-9]+[[:space:]]*$ ]]; then
    add_check resource-cgroup true PASS "memory.max=$memory_max;pids.max=$pids_max;cpu.max=$cpu_max" "Finite cgroup v2 resource ceilings are configured."
else
    add_check resource-cgroup true FAIL "$cpu_max" "cpu.max was unlimited or invalid."
    write_report BLOCKED
    exit 1
fi
if [[ ! -w "$cgroup_path/cgroup.procs" ]]; then
    add_check worker-cgroup true UNAVAILABLE "$cgroup_path" "The application identity cannot attach the worker to the delegated cgroup."
    mark_worker_checks_unavailable "worker cgroup not writable"
    write_report HOST_CERTIFICATION_UNAVAILABLE
    exit 1
fi
add_check worker-cgroup true PASS "$cgroup_path" "Dedicated cgroup v2 control files are present and writable."

if ! bwrap --unshare-all --die-with-parent --ro-bind "$dotnet_root" /dotnet --proc /proc --dev /dev --tmpfs /tmp --clearenv --setenv DOTNET_ROOT /dotnet -- /dotnet/dotnet --info >"$probe_output" 2>&1; then
    probe_error="$(tr '\n' ' ' <"$probe_output" | cut -c1-500)"
    if grep -Eqi 'operation not permitted|permission denied|eagain|netlink_route' "$probe_output"; then
        add_check namespace-support true UNAVAILABLE "$probe_error" "The host denied the namespace primitive; no certification claim is made."
        write_report HOST_CERTIFICATION_UNAVAILABLE
    else
        add_check namespace-support true FAIL "$probe_error" "Bubblewrap namespace setup failed."
        write_report BLOCKED
    fi
    exit 1
fi
add_check namespace-support true PASS "bwrap --unshare-all" "Bubblewrap namespace setup completed."

probe_dir="$(dirname "$probe_path")"
probe_args=(
    --die-with-parent --unshare-all --new-session --clearenv
    --ro-bind "$dotnet_root" /dotnet
    --ro-bind "$probe_dir" /probe
    --ro-bind "$repo_root/ScribuntoWorker/bin/Debug/net10.0" /worker
    --ro-bind-try /lib /lib
    --ro-bind-try /lib64 /lib64
    --ro-bind-try /usr/lib /usr/lib
    --ro-bind-try /usr/lib64 /usr/lib64
    --proc /proc --dev /dev --tmpfs /tmp --chdir /probe
    --setenv DOTNET_ROOT /dotnet --setenv DOTNET_ROOT_X64 /dotnet --setenv HOME /tmp --setenv TMPDIR /tmp --setenv PATH /dotnet
    -- /dotnet/dotnet /probe/$(basename "$probe_path")
)

run_os_probe() {
    local name="$1"
    local expected_kill="${2:-false}"
    set +e
    CW_SECRET_PROBE="synthetic-secret" timeout 5s prlimit --as="$virtual_address_limit" --cpu=2 --nofile=256 bwrap "${probe_args[@]}" "$name" >"$probe_output" 2>&1 &
    local pid=$!
    if ! printf '%s' "$pid" >"$cgroup_path/cgroup.procs" 2>/dev/null; then
        wait "$pid" 2>/dev/null || true
        set -e
        add_check "os-$name" true UNAVAILABLE "$cgroup_path" "The OS probe could not be attached to the delegated cgroup."
        return
    fi
    if ! grep -qx "$pid" "$cgroup_path/cgroup.procs"; then
        wait "$pid" 2>/dev/null || true
        set -e
        add_check "os-$name" true UNAVAILABLE "$cgroup_path" "The OS probe PID was not observable in the delegated cgroup."
        return
    fi
    wait "$pid"
    local status=$?
    set -e
    local blocked="false"
    if [[ $status -eq 0 ]] && jq -e '.blocked == true' "$probe_output" >/dev/null 2>&1; then blocked="true"; fi
    if [[ "$blocked" == true || ( "$expected_kill" == true && $status -ne 0 && $status -ne 124 ) ]]; then
        add_check "os-$name" true PASS "$(tr '\n' ' ' <"$probe_output" | cut -c1-500)" "The isolated OS probe was denied."
    else
        add_check "os-$name" true FAIL "$(tr '\n' ' ' <"$probe_output" | cut -c1-500)" "The forbidden OS operation was not proven blocked."
    fi
}

run_os_probe identity
run_os_probe filesystem-read
run_os_probe filesystem-write
run_os_probe network
run_os_probe environment
run_os_probe process
run_os_probe library
run_os_probe memory true
run_os_probe tasks true
run_os_probe cpu true

set +e
CW_SECRET_PROBE="synthetic-secret" dotnet "$certification_path" --worker "$worker_path" --cgroup "$cgroup_path" --report "${report_path%.json}-runner.json" >"$probe_output" 2>&1
runner_status=$?
set -e
if [[ $runner_status -eq 0 ]] && jq -e '.verdict == "VERIFIED"' "${report_path%.json}-runner.json" >/dev/null 2>&1; then
    add_check worker-adversarial-suite true PASS "${report_path%.json}-runner.json" "The real experimental launcher passed synthetic timeout, recovery, concurrency, API-denial, and output-bound probes."
else
    runner_error="$(tr '\n' ' ' <"$probe_output" | cut -c1-500)"
    if jq -e '.verdict == "HOST_CERTIFICATION_UNAVAILABLE"' "${report_path%.json}-runner.json" >/dev/null 2>&1; then
        add_check worker-adversarial-suite true UNAVAILABLE "$runner_error" "The worker launcher could not certify this host."
    else
        add_check worker-adversarial-suite true FAIL "$runner_error" "The real experimental launcher failed mandatory probes."
    fi
fi

if jq -e 'any(.[]; .required and .status == "FAIL")' "$checks_file" >/dev/null; then
    verdict=BLOCKED
elif jq -e 'any(.[]; .required and .status == "UNAVAILABLE")' "$checks_file" >/dev/null; then
    verdict=HOST_CERTIFICATION_UNAVAILABLE
else
    verdict=VERIFIED
fi
write_report "$verdict"
cat "$report_path"
[[ "$verdict" == VERIFIED ]]
