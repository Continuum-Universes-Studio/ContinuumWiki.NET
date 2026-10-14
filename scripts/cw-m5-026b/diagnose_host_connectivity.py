#!/usr/bin/env python3
"""Run only the approved fixed-target Podman observations and TCP connect probes."""
from __future__ import annotations

import argparse
import errno
import hashlib
import importlib.util
import json
import os
import socket
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[2]
PREFLIGHT_PATH = ROOT / "scripts/preflight-m5-026b-host-connectivity.py"
spec = importlib.util.spec_from_file_location("cw_m5_026b_host_connectivity_preflight", PREFLIGHT_PATH)
assert spec and spec.loader
PREFLIGHT = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = PREFLIGHT
spec.loader.exec_module(PREFLIGHT)

PODMAN = "/usr/bin/podman"
RUN_ID_LABEL = "io.continuumwiki.m5-026b-run-id"
DATABASE_LABEL = "io.continuumwiki.m5-026b-database"
VOLUME_ROLE_LABEL = "io.continuumwiki.m5-026b-role"
MAX_AGE_SECONDS = 600
HEALTH = {"healthy", "unhealthy", "starting", "unavailable", "none"}


class ConnectivityRunError(ValueError):
    def __init__(self, classification: str, *, operation_id: str = "UNKNOWN",
                 exit_code: int | None = None, timed_out: bool = False,
                 output_failure_category: str | None = None,
                 podman_error_category: str = "UNKNOWN") -> None:
        super().__init__(classification)
        self.classification = classification
        self.operation_id = operation_id
        self.exit_code = exit_code
        self.timed_out = timed_out
        self.output_failure_category = output_failure_category
        self.podman_error_category = podman_error_category

    def sanitized_record(self) -> dict[str, Any]:
        return {
            "OperationId": self.operation_id,
            "Classification": self.classification,
            "ExitCode": self.exit_code,
            "TimedOut": self.timed_out,
            "OutputFailureCategory": self.output_failure_category,
            "JsonParseFailureCategory": "NOT_APPLICABLE_FIXED_TEMPLATE_OUTPUT",
            "PodmanErrorCategory": self.podman_error_category,
        }


def _stamp(value: datetime) -> str:
    return value.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")


def _commands(target: dict[str, Any]) -> list[list[str]]:
    name, volume, image = target["ContainerName"], target["VolumeName"], target["ImageReference"]
    fmt = (
        "{{.Id}}|{{.State.Status}}|{{.State.Running}}|"
        "{{if .State.Health}}{{.State.Health.Status}}{{else}}unavailable{{end}}|"
        "{{.ImageName}}|{{.Image}}|"
        f'{{{{index .Config.Labels "{RUN_ID_LABEL}"}}}}|'
        f'{{{{index .Config.Labels "{DATABASE_LABEL}"}}}}|'
        "{{.HostConfig.NetworkMode}}|"
        "{{with index .HostConfig.PortBindings \"3306/tcp\"}}{{range .}}{{.HostIp}}:{{.HostPort}},{{end}}{{end}}|"
        "{{range .Mounts}}{{if eq .Type \"volume\"}}{{.Name}}={{.Destination}};{{end}}{{end}}"
    )
    return [
        [PODMAN, "container", "inspect", "--format", fmt, name],
        [PODMAN, "port", name, "3306/tcp"],
        [PODMAN, "image", "inspect", "--format", '{{.Id}}|{{join .RepoDigests ","}}', image],
        [PODMAN, "volume", "inspect", "--format",
         f'{{{{.Name}}}}|{{{{index .Labels "{RUN_ID_LABEL}"}}}}|{{{{index .Labels "{VOLUME_ROLE_LABEL}"}}}}', volume],
    ]


def _errno_category(value: int | None) -> str:
    if value == errno.ENOENT:
        return "EXECUTABLE_NOT_FOUND"
    if value in {errno.EACCES, errno.EPERM}:
        return "EXECUTION_PERMISSION_DENIED"
    return "UNKNOWN"


def _safe_podman_error_category(stderr: str) -> str:
    # Match only a small set of known diagnostic classes. Never retain or echo
    # stderr, which may contain host paths or other sensitive details.
    value = stderr[:4096].casefold()
    if "no such container" in value or "container not found" in value:
        return "CONTAINER_NOT_FOUND"
    if "no such image" in value or "image not known" in value:
        return "IMAGE_NOT_FOUND"
    if "no such volume" in value or "volume not found" in value:
        return "VOLUME_NOT_FOUND"
    if "permission denied" in value:
        return "PERMISSION_DENIED"
    if "template:" in value or "can't evaluate field" in value or "function \"" in value:
        return "TEMPLATE_ERROR"
    if "unknown flag" in value or "unknown shorthand" in value or "invalid argument" in value:
        return "COMMAND_ARGUMENT_ERROR"
    if "cannot connect to podman" in value or "unable to connect" in value or "connection refused" in value:
        return "PODMAN_SERVICE_UNAVAILABLE"
    return "UNKNOWN"


def _run_podman(command: list[str], deadline: float, operation_id: str = "PODMAN_OBSERVATION") -> subprocess.CompletedProcess[str]:
    remaining = deadline - time.monotonic()
    if remaining <= 0:
        raise ConnectivityRunError("OVERALL_TIMEOUT", operation_id=operation_id, timed_out=True)
    try:
        return subprocess.run(command, shell=False, check=False, capture_output=True, text=True,
                              timeout=min(30, max(1, int(remaining))),
                              env={"PATH": "/usr/bin:/bin", "HOME": str(Path.home()),
                                   "XDG_RUNTIME_DIR": f"/run/user/{os.getuid()}", "LC_ALL": "C"})
    except subprocess.TimeoutExpired:
        raise ConnectivityRunError("PODMAN_TIMEOUT", operation_id=operation_id, timed_out=True) from None
    except OSError as error:
        raise ConnectivityRunError("PODMAN_EXECUTION_UNAVAILABLE", operation_id=operation_id,
                                   podman_error_category=_errno_category(error.errno)) from None


def _stdout(result: subprocess.CompletedProcess[str], operation_id: str) -> str:
    exit_code = getattr(result, "returncode", None)
    if type(exit_code) is not int:
        raise ConnectivityRunError("PODMAN_OUTPUT_SCHEMA_MISMATCH", operation_id=operation_id,
                                   output_failure_category="PROCESS_RESULT_INVALID")
    if exit_code != 0:
        stderr = getattr(result, "stderr", "")
        category = _safe_podman_error_category(stderr) if isinstance(stderr, str) else "UNKNOWN"
        raise ConnectivityRunError("PODMAN_COMMAND_FAILED", operation_id=operation_id,
                                   exit_code=exit_code, podman_error_category=category)
    stdout = getattr(result, "stdout", None)
    if not isinstance(stdout, str):
        raise ConnectivityRunError("PODMAN_OUTPUT_SCHEMA_MISMATCH", operation_id=operation_id,
                                   exit_code=exit_code, output_failure_category="STDOUT_TYPE_INVALID")
    if not stdout.strip():
        raise ConnectivityRunError("PODMAN_OUTPUT_EMPTY", operation_id=operation_id, exit_code=exit_code,
                                   output_failure_category="EMPTY_STDOUT")
    if len(stdout) > 32768:
        raise ConnectivityRunError("PODMAN_OUTPUT_INVALID", operation_id=operation_id, exit_code=exit_code,
                                   output_failure_category="OUTPUT_TOO_LARGE")
    return stdout.strip()


def _one_line(value: str, fields: int, operation_id: str,
              required_fields: tuple[int, ...] = ()) -> list[str]:
    rows = value.splitlines()
    if not rows:
        raise ConnectivityRunError("PODMAN_OUTPUT_EMPTY", operation_id=operation_id,
                                   output_failure_category="NO_OUTPUT_ROWS")
    if len(rows) != 1:
        raise ConnectivityRunError("PODMAN_OUTPUT_INVALID", operation_id=operation_id,
                                   output_failure_category="UNEXPECTED_ROW_COUNT")
    parts = rows[0].split("|")
    if len(parts) != fields:
        raise ConnectivityRunError("PODMAN_OUTPUT_INVALID", operation_id=operation_id,
                                   output_failure_category="DELIMITED_FIELD_COUNT_MISMATCH")
    if any(index >= len(parts) or not parts[index].strip() for index in required_fields):
        raise ConnectivityRunError("PODMAN_OUTPUT_SCHEMA_MISMATCH", operation_id=operation_id,
                                   output_failure_category="REQUIRED_FIELD_MISSING")
    return parts


def _port_matches(value: str, endpoint: str) -> bool:
    rows = [row.strip() for row in value.splitlines() if row.strip()]
    if len(rows) != 1:
        return False
    mapped = rows[0].rsplit("->", 1)[-1].strip() if "->" in rows[0] else rows[0]
    return mapped == endpoint


def _safe_label(value: str, choices: set[str]) -> str:
    return value if value in choices else "unrecognized"


def _inspect_target(target: dict[str, Any], role: str, run) -> dict[str, Any]:
    container_cmd, port_cmd, image_cmd, volume_cmd = _commands(target)
    prefix = role.upper()
    container_operation = f"{prefix}_CONTAINER_INSPECT"
    fields = _one_line(_stdout(run(container_cmd, container_operation), container_operation), 11,
                       container_operation, (0, 1, 2, 4, 5, 6, 7, 8))
    cid, state, running, health, image_name, image_id, run_id, database, network, bindings, mounts = fields
    configured_endpoint = target["Endpoint"]
    try:
        host, port_text = configured_endpoint.rsplit(":", 1)
        expected_binding = host + ":" + port_text + ","
    except ValueError:
        raise ConnectivityRunError("FIXED_ENDPOINT_INVALID") from None
    identity_checks = {
        "ContainerId": cid == target["ContainerId"],
        "ImageReference": image_name == target["ImageReference"],
        "RunIdLabel": run_id == target["ProvisioningRunId"],
        "DatabaseLabel": database == target["DatabaseName"],
        "ConfiguredLoopbackBinding": bindings == expected_binding,
        "PortPublication": _port_matches(_stdout(run(port_cmd, f"{prefix}_PUBLISHED_PORTS"),
                                                  f"{prefix}_PUBLISHED_PORTS"), configured_endpoint),
        "VolumeMount": f'{target["VolumeName"]}=/var/lib/mysql;' in mounts,
    }
    image_operation = f"{prefix}_IMAGE_INSPECT"
    image_fields = _one_line(_stdout(run(image_cmd, image_operation), image_operation), 2,
                             image_operation, (0, 1))
    volume_operation = f"{prefix}_VOLUME_INSPECT"
    volume_fields = _one_line(_stdout(run(volume_cmd, volume_operation), volume_operation), 3,
                              volume_operation, (0, 1, 2))
    identity_checks["ImmutableImage"] = image_fields[0] == image_id and target["ImageReference"] in image_fields[1].split(",")
    expected_role = "source" if role == "Source" else "destination"
    identity_checks["VolumeIdentity"] = volume_fields == [target["VolumeName"], target["ProvisioningRunId"], expected_role]
    network = network if network and len(network) <= 80 and all(c.isalnum() or c in "_.:-" for c in network) else "unrecognized"
    mismatched_fields = [key for key, matched in identity_checks.items() if not matched]
    return {"Role": role, "ContainerIdMatched": identity_checks["ContainerId"],
            "IdentityChecks": identity_checks, "IdentityMatched": all(identity_checks.values()),
            "FailureClassification": None if not mismatched_fields else "PODMAN_IDENTITY_MISMATCH",
            "MismatchedIdentityFields": mismatched_fields,
            "Running": running.casefold() == "true" if running.casefold() in {"true", "false"} else None,
            "State": _safe_label(state, {"running", "exited", "created", "paused", "stopping", "unknown"}),
            "Health": _safe_label(health, HEALTH), "ImageReferenceMatched": identity_checks["ImageReference"],
            "NetworkMode": network, "ConfiguredEndpoint": configured_endpoint,
            "ConfiguredPortBindingMatched": identity_checks["ConfiguredLoopbackBinding"],
            "PublishedPortMappingMatched": identity_checks["PortPublication"]}


def _probe(endpoint: str, timeout: int = 3) -> str:
    host, port = endpoint.rsplit(":", 1)
    try:
        connection = socket.create_connection((host, int(port)), timeout=timeout)
    except (socket.timeout, TimeoutError):
        return "CONNECTION_TIMEOUT"
    except ConnectionRefusedError:
        return "CONNECTION_REFUSED"
    except OSError:
        return "NETWORK_ERROR"
    try:
        return "TCP_ACCEPTED"
    finally:
        connection.close()


def _claim_once(manifest: dict[str, Any], digest: str) -> None:
    directory = Path(manifest["Evidence"]["ManifestPath"]).parent
    payload = {"ApprovalId": manifest["ApprovalId"], "ManifestSha256": digest, "StartedAtUtc": _stamp(datetime.now(timezone.utc))}
    fd = os.open(directory / ".started", os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0), 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as stream:
        json.dump(payload, stream, sort_keys=True)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())


def _fresh_preflight(report: dict[str, Any], manifest: dict[str, Any], digest: str, now: datetime) -> None:
    try:
        generated = PREFLIGHT._utc(report["GeneratedAtUtc"], "PREFLIGHT_TIME_INVALID")
    except (KeyError, PREFLIGHT.ConnectivityPreflightError):
        raise ConnectivityRunError("PREFLIGHT_INVALID") from None
    age = (now - generated).total_seconds()
    if (report.get("Classification") != "HOST_CONNECTIVITY_PREFLIGHT_VALID_NONCONNECTING"
            or report.get("ApprovalId") != manifest["ApprovalId"] or report.get("ManifestSha256") != digest
            or report.get("Targets") != manifest["Targets"] or report.get("PodmanCommands") != 0
            or report.get("TcpConnections") != 0 or report.get("DatabaseConnections") != 0
            or report.get("CredentialsRead") is not False or age < 0 or age > MAX_AGE_SECONDS):
        raise ConnectivityRunError("PREFLIGHT_NOT_FRESH_OR_NOT_MATCHED")


def run_diagnostic(manifest: dict[str, Any], preflight_report: dict[str, Any], *, now: datetime | None = None) -> dict[str, Any]:
    instant = now or datetime.now(timezone.utc)
    evidence = manifest["Evidence"]
    validated = PREFLIGHT.validate_manifest(manifest, Path(evidence["ManifestPath"]), now=instant,
                                           allow_existing_preflight=True)
    digest = validated["ManifestSha256"]
    _fresh_preflight(preflight_report, manifest, digest, instant)
    _claim_once(manifest, digest)
    report: dict[str, Any] = {"ReportVersion": PREFLIGHT.CONTRACT + "-result", "GeneratedAtUtc": _stamp(instant),
        "ApprovalId": manifest["ApprovalId"], "ManifestSha256": digest, "Classification": "HOST_CONNECTIVITY_DIAGNOSTIC_INCOMPLETE",
        "Targets": [], "ObservationFailures": [], "PodmanCommands": 0, "TcpAttempts": 0, "PayloadBytesSent": 0, "Retries": 0,
        "DatabaseConnections": 0, "CredentialsRead": False, "SqlExecuted": False,
        "ProductionMigration": "NO_GO", "RepositoryActivation": "NO_GO"}
    started = time.monotonic()
    deadline = started + 600
    command_count = 0

    def observe(command, operation_id):
        nonlocal command_count
        command_count += 1
        return _run_podman(command, deadline, operation_id)

    try:
        rootless_operation = "PODMAN_ROOTLESS_CONTEXT"
        rootless = _stdout(observe([PODMAN, "info", "--format", "{{.Host.Security.Rootless}}"], rootless_operation),
                           rootless_operation).casefold()
        if rootless != "true":
            raise ConnectivityRunError("PODMAN_NOT_ROOTLESS", operation_id=rootless_operation,
                                       output_failure_category="ROOTLESS_FALSE")
        expected = PREFLIGHT._expected_targets()
        observations = []
        for role in ("Source", "Destination"):
            try:
                observations.append(_inspect_target(expected[role], role, observe))
            except ConnectivityRunError as error:
                observations.append({"Role": role, "IdentityMatched": None,
                                     "FailureClassification": error.classification,
                                     "Running": None, "Health": "unknown", "NetworkMode": "unknown",
                                     "ConfiguredEndpoint": expected[role]["Endpoint"],
                                     "ConfiguredPortBindingMatched": None,
                                     "PublishedPortMappingMatched": None})
                report["ObservationFailures"].append(error.sanitized_record())
                report["Targets"] = observations
                report["Classification"] = error.classification
                break
        report["Targets"] = observations
        report["PodmanCommands"] = command_count
        if report["ObservationFailures"]:
            return report
        if not all(item["IdentityMatched"] for item in observations):
            report["Classification"] = "PODMAN_IDENTITY_MISMATCH"
            for item in observations:
                if item.get("FailureClassification") == "PODMAN_IDENTITY_MISMATCH":
                    report["ObservationFailures"].append({
                        "OperationId": item["Role"].upper() + "_TARGET_IDENTITY",
                        "Classification": "PODMAN_IDENTITY_MISMATCH",
                        "ExitCode": None,
                        "TimedOut": False,
                        "OutputFailureCategory": None,
                        "JsonParseFailureCategory": "NOT_APPLICABLE_FIXED_TEMPLATE_OUTPUT",
                        "PodmanErrorCategory": "UNKNOWN",
                        "MismatchedIdentityFields": item["MismatchedIdentityFields"],
                    })
            return report
        if time.monotonic() >= deadline:
            raise ConnectivityRunError("OVERALL_TIMEOUT")
        probe_results = []
        for role in ("Source", "Destination"):
            endpoint = expected[role]["Endpoint"]
            outcome = _probe(endpoint, 3)
            report["TcpAttempts"] += 1
            probe_results.append({"Role": role, "Endpoint": endpoint, "Outcome": outcome, "PayloadBytesSent": 0, "Attempts": 1})
        report["TcpProbes"] = probe_results
        report["Classification"] = "HOST_CONNECTIVITY_DIAGNOSTIC_COMPLETED"
    except ConnectivityRunError as error:
        report["Classification"] = error.classification
        report["ObservationFailures"].append(error.sanitized_record())
    report["PodmanCommands"] = command_count
    return report


def run_paths(manifest_path: Path) -> dict[str, Any]:
    manifest = PREFLIGHT.BASE._read_private_json(manifest_path, "MANIFEST_JSON_INVALID")
    preflight_path = Path(manifest["Evidence"]["PreflightReportPath"])
    preflight_report = PREFLIGHT.BASE._read_private_json(preflight_path, "PREFLIGHT_REPORT_INVALID")
    report = run_diagnostic(manifest, preflight_report)
    report_path = Path(manifest["Evidence"]["ResultReportPath"])
    PREFLIGHT.BASE._write_once(report_path, report)
    return report


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True)
    args = parser.parse_args(argv)
    try:
        report = run_paths(Path(args.manifest))
    except (OSError, KeyError, TypeError, PREFLIGHT.ConnectivityPreflightError, ConnectivityRunError) as error:
        code = str(error) if isinstance(error, (PREFLIGHT.ConnectivityPreflightError, ConnectivityRunError)) else "DIAGNOSTIC_STOPPED"
        print("Host-connectivity diagnostic stopped: " + code, file=sys.stderr)
        return 2
    print("Host-connectivity diagnostic: " + report["Classification"])
    return 0 if report["Classification"] == "HOST_CONNECTIVITY_DIAGNOSTIC_COMPLETED" else 3


if __name__ == "__main__":
    raise SystemExit(main())
