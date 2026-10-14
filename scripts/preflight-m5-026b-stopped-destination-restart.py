#!/usr/bin/env python3
"""Nonconnecting preflight for one authorized start of the retained destination."""
from __future__ import annotations

import importlib.util
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE_PREFLIGHT_PATH = ROOT / "scripts/preflight-m5-026b-stopped-source-restart.py"
DESTINATION_DIAGNOSTIC_PREFLIGHT_PATH = ROOT / "scripts/preflight-m5-026b-stopped-destination-diagnostic.py"

def _load(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module

BASE = _load("cw_m5_026b_destination_restart_preflight_base", SOURCE_PREFLIGHT_PATH)
DIAGNOSTIC_PREFLIGHT = _load("cw_m5_026b_destination_restart_diagnostic_preflight", DESTINATION_DIAGNOSTIC_PREFLIGHT_PATH)

CONTRACT = "cw-m5-026b-stopped-destination-restart-authorization-v1"
PREFLIGHT_CONTRACT = "cw-m5-026b-stopped-destination-restart-preflight-v1"
PREFLIGHT_CLASSIFICATION = "CATEGORY_STOPPED_DESTINATION_RESTART_AUTHORIZATION_VALID_NONCONNECTING"
TARGET_ROLE = "Destination"
EXPECTED_DIAGNOSTIC_CLASSIFICATION = "DESTINATION_CONTAINER_STOPPED_IDENTITY_MATCH"
AUTHORIZED_TARGET = dict(DIAGNOSTIC_PREFLIGHT.AUTHORIZED_TARGET)
OPS = {
    "StartStoppedDestinationContainerOnce": True,
    "ContainerCreation": False, "ContainerRecreation": False, "ContainerRestart": False,
    "ImageOrVolumeChanges": False, "DatabaseConnections": False, "DatabaseMutations": False,
    "Migrations": False, "GrantChanges": False, "FixtureWrites": False,
    "Backup": False, "Restore": False, "Cleanup": False,
    "ContainerVolumeDeletion": False, "ProductionAccess": False,
}

BASE.CONTRACT = CONTRACT
BASE.PREFLIGHT_CONTRACT = PREFLIGHT_CONTRACT
BASE.PREFLIGHT_CLASSIFICATION = PREFLIGHT_CLASSIFICATION
BASE.TARGET_ROLE = TARGET_ROLE
BASE.EXPECTED_DIAGNOSTIC_CLASSIFICATION = EXPECTED_DIAGNOSTIC_CLASSIFICATION
BASE.AUTHORIZED_TARGET = AUTHORIZED_TARGET
BASE.OPS = OPS
BASE.DIAGNOSTIC_PREFLIGHT = DIAGNOSTIC_PREFLIGHT

PreflightError = BASE.PreflightError
validate = BASE.validate
run = BASE.run

if __name__ == "__main__":
    raise SystemExit(BASE.main())
