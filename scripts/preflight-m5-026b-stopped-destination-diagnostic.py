#!/usr/bin/env python3
"""Nonconnecting preflight for a separate stopped-destination diagnostic."""
from __future__ import annotations

import importlib.util
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE_PREFLIGHT_PATH = ROOT / "scripts/preflight-m5-026b-stopped-source-diagnostic.py"
DESTINATION_PLAN_PATH = ROOT / "scripts/cw-m5-026b/stopped_destination_diagnostic.py"

def _load(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module

BASE = _load("cw_m5_026b_destination_diagnostic_preflight_base", SOURCE_PREFLIGHT_PATH)
DIAG = _load("cw_m5_026b_destination_diagnostic_plan", DESTINATION_PLAN_PATH)

CONTRACT = "cw-m5-026b-stopped-destination-diagnostic-authorization-v1"
PREFLIGHT_REPORT_VERSION = "cw-m5-026b-stopped-destination-diagnostic-preflight-v1"
PREFLIGHT_CLASSIFICATION = "CATEGORY_STOPPED_DESTINATION_DIAGNOSTIC_AUTHORIZATION_VALID_NONCONNECTING"
AUTHORIZED_TARGET = {key: DIAG.EXPECTED[key] for key in (
    "ContainerName", "ContainerId", "ImageReference", "VolumeName",
    "DatabaseName", "Endpoint", "ProvisioningRunId",
)}
OPS = {
    "InspectRootlessPodman": True, "InspectExactDestinationContainer": True,
    "InspectPinnedImage": True, "InspectBoundVolume": True,
    "InspectPublishedEndpoint": True, "RetainSanitizedEvidence": True,
    "DatabaseConnections": False, "CredentialReads": False, "ContainerStart": False,
    "ContainerRestart": False, "ContainerCreation": False, "ContainerRecreation": False,
    "ContainerStop": False, "ImageOrVolumeChanges": False, "DatabaseMutations": False,
    "Migrations": False, "GrantChanges": False, "FixtureWrites": False,
    "Backup": False, "Restore": False, "Cleanup": False, "Deletion": False,
    "ProductionAccess": False,
}

BASE.CONTRACT = CONTRACT
BASE.PREFLIGHT_REPORT_VERSION = PREFLIGHT_REPORT_VERSION
BASE.PREFLIGHT_CLASSIFICATION = PREFLIGHT_CLASSIFICATION
BASE.AUTHORIZED_TARGET = AUTHORIZED_TARGET
BASE.OPS = OPS
BASE.DIAG = DIAG

PreflightError = BASE.PreflightError
validate = BASE.validate
run = BASE.run

if __name__ == "__main__":
    raise SystemExit(BASE.main())
