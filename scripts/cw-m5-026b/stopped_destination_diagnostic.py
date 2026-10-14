"""Destination specialization of the established stopped-container diagnostic."""
from __future__ import annotations

import importlib.util
import json
import os
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE_PLAN_PATH = ROOT / "scripts/cw-m5-026b/stopped_source_diagnostic.py"
SPEC = importlib.util.spec_from_file_location("cw_m5_026b_destination_diagnostic_base", SOURCE_PLAN_PATH)
assert SPEC and SPEC.loader
BASE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(BASE)

TARGET_ROLE = "Destination"
TARGET_PREFIX = "DESTINATION"
VOLUME_ROLE = "destination"
REPORT_VERSION = "cw-m5-026b-stopped-destination-diagnostic-v1"
EXPECTED_DIAGNOSTIC_CLASSIFICATION = "DESTINATION_CONTAINER_STOPPED_IDENTITY_MATCH"


def _current_binding_expected() -> dict[str, str]:
    """Overlay only the destination-specific RunId from fresh binding evidence."""
    expected = dict(BASE.PREFLIGHT.expected_targets()[TARGET_ROLE])
    binding_path = os.environ.get("CW_M5_026B_DESTINATION_BINDING_PATH")
    if not binding_path:
        return expected
    try:
        binding = json.loads(Path(binding_path).read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise RuntimeError("DESTINATION_BINDING_EVIDENCE_UNAVAILABLE") from error
    run_id = binding.get("ContainerRunId")
    if (binding.get("ContainerName") != expected["ContainerName"]
            or binding.get("ContainerId") != expected["ContainerId"]
            or binding.get("VolumeName") != expected["VolumeName"]
            or binding.get("VolumeRunId") != run_id
            or binding.get("ContainerAndVolumeRunIdLabelsMatch") is not True
            or not isinstance(run_id, str)
            or not re.fullmatch(r"\d{8}T\d{6}Z-[A-Za-z0-9._-]+", run_id)):
        raise RuntimeError("DESTINATION_BINDING_EVIDENCE_INVALID")
    expected["ProvisioningRunId"] = run_id
    return expected


EXPECTED = _current_binding_expected()

BASE.TARGET_ROLE = TARGET_ROLE
BASE.TARGET_PREFIX = TARGET_PREFIX
BASE.VOLUME_ROLE = VOLUME_ROLE
BASE.REPORT_VERSION = REPORT_VERSION
BASE.EXPECTED = EXPECTED
BASE.EXPECTED_DIAGNOSTIC_CLASSIFICATION = EXPECTED_DIAGNOSTIC_CLASSIFICATION

PREFLIGHT = BASE.PREFLIGHT
podman_diagnostic_commands = BASE.podman_diagnostic_commands
classify_observations = BASE.classify_observations
_classification = BASE._classification
