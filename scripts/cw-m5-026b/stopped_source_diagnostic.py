"""Pure planner/classifier for the stopped CW-M5-026B source target.

This module is a pure, source-bound planner/classifier. Live execution is
implemented separately in diagnose_stopped_source.py and is gated by its own
diagnostic-only authorization contract.
"""

from __future__ import annotations

import hashlib
import importlib.util
import re
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[2]
PREFLIGHT_PATH = ROOT / "scripts/preflight-m5-026b-inspection.py"
SPEC = importlib.util.spec_from_file_location("m5_026b_source_diagnostic_preflight", PREFLIGHT_PATH)
assert SPEC and SPEC.loader
PREFLIGHT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PREFLIGHT)

PODMAN = "/usr/bin/podman"
RUN_ID_LABEL = "io.continuumwiki.m5-026b-run-id"
DATABASE_LABEL = "io.continuumwiki.m5-026b-database"
VOLUME_ROLE_LABEL = "io.continuumwiki.m5-026b-role"
HEX64 = re.compile(r"^[0-9a-f]{64}$")
TARGET_ROLE = "Source"
TARGET_PREFIX = "SOURCE"
VOLUME_ROLE = "source"
REPORT_VERSION = "cw-m5-026b-stopped-source-diagnostic-v1"
EXPECTED = PREFLIGHT.expected_targets()[TARGET_ROLE]
EXPECTED_DIAGNOSTIC_CLASSIFICATION = f"{TARGET_PREFIX}_CONTAINER_STOPPED_IDENTITY_MATCH"
OBSERVED_CONTAINER_RUN_ID: str | None = None
OBSERVED_VOLUME_RUN_ID: str | None = None


def _classification(suffix: str) -> str:
    return f"{TARGET_PREFIX}_CONTAINER_{suffix}"


def podman_diagnostic_commands() -> list[list[str]]:
    """Return the bounded read-only command sequence for the exact source."""
    name = EXPECTED["ContainerName"]
    volume = EXPECTED["VolumeName"]
    image = EXPECTED["ImageReference"]
    inspect_format = (
        "{{.Id}}|{{.State.Status}}|{{.State.Running}}|{{.State.ExitCode}}|"
        "{{.ImageName}}|{{.Image}}|"
        f'{{{{index .Config.Labels "{RUN_ID_LABEL}"}}}}|'
        f'{{{{index .Config.Labels "{DATABASE_LABEL}"}}}}|'
        "{{range .Mounts}}{{.Name}}={{.Destination}};{{end}}|"
        "{{range $port, $bindings := .HostConfig.PortBindings}}"
        "{{$port}}={{range $bindings}}{{.HostIp}}:{{.HostPort}},{{end}};{{end}}"
    )
    return [
        [PODMAN, "info", "--format", "{{.Host.Security.Rootless}}"],
        [PODMAN, "container", "exists", name],
        [PODMAN, "container", "inspect", "--format", inspect_format, name],
        [PODMAN, "port", name, "3306/tcp"],
        [PODMAN, "image", "inspect", "--format", '{{.Id}}|{{join .RepoDigests ","}}', image],
        [PODMAN, "volume", "inspect", "--format",
         f'{{{{.Name}}}}|{{{{index .Labels "{RUN_ID_LABEL}"}}}}|'
         f'{{{{index .Labels "{VOLUME_ROLE_LABEL}"}}}}', volume],
    ]


def _observation(observations: dict[str, Any], name: str) -> tuple[int, str] | None:
    item = observations.get(name)
    if not isinstance(item, dict):
        return None
    code, stdout = item.get("ReturnCode"), item.get("Stdout")
    if type(code) is not int or not isinstance(stdout, str):
        return None
    return code, stdout


def _one_line_fields(stdout: str, count: int) -> list[str] | None:
    lines = stdout.strip().splitlines()
    if len(lines) != 1:
        return None
    fields = lines[0].split("|")
    return fields if len(fields) == count else None


def _port_matches(stdout: str) -> bool:
    rows = [line.strip() for line in stdout.splitlines() if line.strip()]
    if len(rows) != 1:
        return False
    observed = rows[0].rsplit("->", 1)[-1].strip()
    return observed == EXPECTED["Endpoint"]


def _configured_endpoint_status(value: str) -> str:
    """Validate the persisted HostConfig binding, not runtime publication.

    Output is a deterministic Go-template serialization of PortBindings. The
    return values distinguish an absent 3306/tcp binding from a malformed
    representation and from a binding to a different host/port.
    """
    mappings: dict[str, list[str]] = {}
    if value:
        for row in value.split(";"):
            if not row:
                continue
            if "=" not in row:
                return "malformed"
            port, bindings_text = row.split("=", 1)
            if not port or port in mappings:
                return "malformed"
            bindings = [binding for binding in bindings_text.split(",") if binding]
            mappings[port] = bindings
    bindings = mappings.get("3306/tcp")
    if not bindings:
        return "missing"
    if bindings == [EXPECTED["Endpoint"]]:
        return "matched"
    return "mismatched"


def classify_observations(observations: dict[str, Any]) -> dict[str, Any]:
    """Classify captured command results without retaining raw output/stderr."""
    global OBSERVED_CONTAINER_RUN_ID, OBSERVED_VOLUME_RUN_ID
    OBSERVED_CONTAINER_RUN_ID = None
    OBSERVED_VOLUME_RUN_ID = None
    findings: list[str] = []
    rootless = _observation(observations, "Rootless")
    if rootless is None or rootless[0] != 0:
        code = "ROOTLESS_RUNTIME_UNVERIFIED"
        return _report("DIAGNOSTIC_INCOMPLETE", [code])
    if rootless[1].strip().casefold() != "true":
        code = "PODMAN_NOT_ROOTLESS" if rootless[1].strip().casefold() == "false" else "ROOTLESS_RESULT_MALFORMED"
        return _report("DIAGNOSTIC_INCOMPLETE", [code])

    exists = _observation(observations, "Exists")
    if exists is None:
        return _report("DIAGNOSTIC_INCOMPLETE", ["CONTAINER_EXISTS_RESULT_MALFORMED"])
    if exists[0] == 1:
        return _report(_classification("MISSING"), ["CONTAINER_NAME_NOT_FOUND"])
    if exists[0] == 125:
        return _report(_classification("STORAGE_INACCESSIBLE"), ["CONTAINER_STORAGE_INACCESSIBLE"])
    if exists[0] != 0:
        return _report("DIAGNOSTIC_INCOMPLETE", ["CONTAINER_EXISTS_CHECK_FAILED"])

    inspect = _observation(observations, "Container")
    if inspect is None or inspect[0] != 0:
        return _report("DIAGNOSTIC_INCOMPLETE", ["CONTAINER_INSPECT_FAILED"])
    fields = _one_line_fields(inspect[1], 10)
    if fields is None:
        return _report("DIAGNOSTIC_INCOMPLETE", ["CONTAINER_INSPECT_MALFORMED"])
    (container_id, state, running_text, exit_text, image_name, image_id,
     run_id_label, database_label, mounts, configured_ports) = fields
    OBSERVED_CONTAINER_RUN_ID = run_id_label
    if not HEX64.fullmatch(container_id):
        return _report("DIAGNOSTIC_INCOMPLETE", ["CONTAINER_ID_MALFORMED"])
    state = state.casefold()
    allowed_states = {"running", "exited", "stopped", "created", "configured", "paused", "dead", "removing"}
    running_folded = running_text.casefold()
    observed_exit_code: int | None = None
    try:
        candidate = int(exit_text, 10)
        if 0 <= candidate <= 255:
            observed_exit_code = candidate
    except ValueError:
        pass
    if container_id != EXPECTED["ContainerId"]:
        return _report(_classification("RECREATED"), ["CONTAINER_ID_MISMATCH"],
                       observed_id=container_id,
                       observed_state=state if state in allowed_states else "unknown",
                       exit_code=observed_exit_code,
                       identity={"ContainerIdMatched": False, "RunIdLabelMatched": None, "DatabaseLabelMatched": None,
                                 "VolumeMountMatched": None, "EndpointMatched": None, "VolumeIdentityMatched": None})
    if observed_exit_code is None:
        return _report("DIAGNOSTIC_INCOMPLETE", ["CONTAINER_EXIT_CODE_MALFORMED"],
                       observed_id=container_id if len(container_id) == 64 else None)
    exit_code = observed_exit_code

    identity_failures: list[str] = []
    configured_image_matches = image_name == EXPECTED["ImageReference"]
    if not configured_image_matches:
        findings.append("CONFIGURED_IMAGE_REFERENCE_DIFFERS")
    if run_id_label != EXPECTED["ProvisioningRunId"]:
        identity_failures.append("PROVISIONING_RUN_ID_LABEL_MISMATCH")
    if database_label != EXPECTED["DatabaseName"]:
        identity_failures.append("DATABASE_LABEL_MISMATCH")
    mount_matches = f"{EXPECTED['VolumeName']}=/var/lib/mysql" in [part for part in mounts.rstrip(";").split(";") if part]
    if not mount_matches:
        identity_failures.append("VOLUME_MOUNT_MISMATCH")

    configured_endpoint_status = _configured_endpoint_status(configured_ports)
    if configured_endpoint_status == "malformed":
        return _report("DIAGNOSTIC_INCOMPLETE", ["CONFIGURED_ENDPOINT_RESULT_MALFORMED"], observed_id=container_id,
                       observed_state=state if state in allowed_states else "unknown", exit_code=exit_code)
    if configured_endpoint_status == "missing":
        identity_failures.append("CONFIGURED_ENDPOINT_BINDING_MISSING")
    elif configured_endpoint_status == "mismatched":
        identity_failures.append("CONFIGURED_ENDPOINT_MISMATCH")

    port = _observation(observations, "Port")
    if port is None or port[0] != 0:
        if state not in {"exited", "stopped"}:
            return _report("DIAGNOSTIC_INCOMPLETE", ["PORT_INSPECTION_FAILED"], observed_id=container_id,
                           observed_state=state if state in allowed_states else "unknown", exit_code=exit_code)
        port_rows = []
        findings.append("PODMAN_PORT_OBSERVATION_UNAVAILABLE")
        port_command_unavailable = True
    else:
        port_rows = [line.strip() for line in port[1].splitlines() if line.strip()]
        port_command_unavailable = False
    port_mapping_reported = bool(port_rows)
    if state in {"exited", "stopped"}:
        active_endpoint_observation = (
            "PORT_OBSERVATION_UNAVAILABLE_WHILE_STOPPED"
            if port_command_unavailable else "CONTAINER_STOPPED_ENDPOINT_INACTIVE"
        )
    elif state == "running" and port_mapping_reported:
        active_endpoint_observation = "RUNTIME_MAPPING_REPORTED"
    else:
        active_endpoint_observation = "RUNTIME_MAPPING_NOT_REPORTED"
    active_port_matches = state == "running" and port_mapping_reported and _port_matches(port[1])

    image = _observation(observations, "Image")
    image_fields = _one_line_fields(image[1], 2) if image is not None and image[0] == 0 else None
    if image_fields is None:
        return _report("DIAGNOSTIC_INCOMPLETE", ["IMAGE_INSPECT_FAILED_OR_MALFORMED"], observed_id=container_id,
                       observed_state=state if state in allowed_states else "unknown", exit_code=exit_code)
    else:
        image_matches = image_fields[0] == image_id and EXPECTED["ImageReference"] in image_fields[1].split(",")
        if not image_matches:
            identity_failures.append("IMMUTABLE_IMAGE_ID_OR_DIGEST_MISMATCH")

    volume = _observation(observations, "Volume")
    volume_fields = _one_line_fields(volume[1], 3) if volume is not None and volume[0] == 0 else None
    if volume_fields is None:
        return _report("DIAGNOSTIC_INCOMPLETE", ["VOLUME_INSPECT_FAILED_OR_MALFORMED"], observed_id=container_id,
                       observed_state=state if state in allowed_states else "unknown", exit_code=exit_code,
                       image_matches=image_matches)
    else:
        volume_matches = volume_fields[0] == EXPECTED["VolumeName"] and volume_fields[1] == EXPECTED["ProvisioningRunId"] and volume_fields[2] == VOLUME_ROLE
        OBSERVED_VOLUME_RUN_ID = volume_fields[1]
        if volume_fields[0] != EXPECTED["VolumeName"] or volume_fields[1] != EXPECTED["ProvisioningRunId"]:
            identity_failures.append("VOLUME_IDENTITY_MISMATCH")
        if volume_fields[2] != VOLUME_ROLE:
            identity_failures.append("VOLUME_ROLE_MISMATCH")

    if state == "running":
        if not port_mapping_reported:
            identity_failures.append("ACTIVE_ENDPOINT_NOT_OBSERVED_WHILE_RUNNING")
        elif not active_port_matches:
            identity_failures.append("ACTIVE_ENDPOINT_MISMATCH")
    elif state in {"exited", "stopped"}:
        # Neither an empty nor a populated `podman port` response establishes
        # active publication for an exited container. HostConfig was checked
        # independently above for the configured binding.
        findings.append("ACTIVE_ENDPOINT_INACTIVE_WHILE_STOPPED")

    identity = {
        "ContainerIdMatched": container_id == EXPECTED["ContainerId"],
        "RunIdLabelMatched": run_id_label == EXPECTED["ProvisioningRunId"],
        "DatabaseLabelMatched": database_label == EXPECTED["DatabaseName"],
        "VolumeMountMatched": mount_matches,
        "EndpointMatched": configured_endpoint_status == "matched",
        "ConfiguredEndpointMatched": configured_endpoint_status == "matched",
        "PodmanPortMappingReported": port_mapping_reported,
        "ActiveEndpointObservation": active_endpoint_observation,
        "VolumeIdentityMatched": volume_matches,
    }
    if identity_failures:
        return _report(_classification("IDENTITY_MISMATCH"), identity_failures + findings,
                       observed_id=container_id, observed_state=state if state in allowed_states else "unknown",
                       exit_code=exit_code, image_matches=image_matches, identity=identity)
    if running_folded not in {"true", "false"} or state not in allowed_states:
        return _report("DIAGNOSTIC_INCOMPLETE", ["CONTAINER_STATE_MALFORMED"], observed_id=container_id,
                       observed_state="unknown", exit_code=exit_code, image_matches=image_matches, identity=identity)
    if (state == "running") != (running_folded == "true"):
        return _report("DIAGNOSTIC_INCOMPLETE", ["CONTAINER_STATE_FIELDS_CONFLICT"], observed_id=container_id,
                       observed_state=state, exit_code=exit_code, image_matches=image_matches, identity=identity)
    if state in {"exited", "stopped"} and running_folded == "false":
        return _report(_classification("STOPPED_IDENTITY_MATCH"), findings, observed_id=container_id,
                       observed_state=state, exit_code=exit_code, image_matches=image_matches, identity=identity)
    if state == "running" and running_folded == "true":
        return _report(_classification("RUNNING_IDENTITY_MATCH"), findings, observed_id=container_id,
                       observed_state=state, exit_code=exit_code, image_matches=image_matches, identity=identity)
    return _report(_classification("STATE_UNEXPECTED"), ["CONTAINER_STATE_NOT_STOPPED_OR_RUNNING"] + findings,
                   observed_id=container_id, observed_state=state, exit_code=exit_code, image_matches=image_matches, identity=identity)


def _report(classification: str, findings: list[str], *, observed_id: str | None = None,
            observed_state: str = "unknown", exit_code: int | None = None,
            image_matches: bool | None = None, identity: dict[str, Any] | None = None,
            podman_commands: int = 0) -> dict[str, Any]:
    identity = identity or {key: None for key in (
        "ContainerIdMatched", "RunIdLabelMatched", "DatabaseLabelMatched",
        "VolumeMountMatched", "EndpointMatched", "VolumeIdentityMatched")}
    return {
        "ReportVersion": REPORT_VERSION,
        "Classification": classification,
        "TargetRole": TARGET_ROLE,
        "ContainerRunIdLabelPresent": None if OBSERVED_CONTAINER_RUN_ID is None else bool(OBSERVED_CONTAINER_RUN_ID),
        "ContainerRunIdLabelSha256": hashlib.sha256(OBSERVED_CONTAINER_RUN_ID.encode()).hexdigest() if OBSERVED_CONTAINER_RUN_ID else None,
        "VolumeRunIdLabelPresent": None if OBSERVED_VOLUME_RUN_ID is None else bool(OBSERVED_VOLUME_RUN_ID),
        "VolumeRunIdLabelSha256": hashlib.sha256(OBSERVED_VOLUME_RUN_ID.encode()).hexdigest() if OBSERVED_VOLUME_RUN_ID else None,
        "ContainerAndVolumeRunIdLabelsMatch": (
            None if OBSERVED_CONTAINER_RUN_ID is None or OBSERVED_VOLUME_RUN_ID is None
            else OBSERVED_CONTAINER_RUN_ID == OBSERVED_VOLUME_RUN_ID
        ),
        "ExpectedContainerId": EXPECTED["ContainerId"],
        "ObservedContainerIdSha256": hashlib.sha256(observed_id.encode()).hexdigest() if observed_id else None,
        "ObservedState": observed_state,
        "ObservedExitCode": exit_code,
        "PinnedImageIdentityMatched": image_matches,
        **identity,
        "Findings": sorted(set(findings)),
        "RawPodmanOutputPersisted": False,
        "StderrPersisted": False,
        "DatabaseConnections": 0,
        "DatabaseMutations": 0,
        "CredentialsRead": 0,
        "PodmanCommandsAreReadOnly": True,
        "PodmanReadOnlyCommands": podman_commands,
    }
