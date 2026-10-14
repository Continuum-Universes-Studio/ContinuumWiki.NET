#!/usr/bin/env python3
"""Run one separately authorized start of the exact retained destination."""
from __future__ import annotations

import importlib.util
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

def _load(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module

BASE = _load("cw_m5_026b_destination_start_runner_base", ROOT / "scripts/cw-m5-026b/start_stopped_source.py")
PREFLIGHT = _load("cw_m5_026b_destination_restart_preflight", ROOT / "scripts/preflight-m5-026b-stopped-destination-restart.py")

BASE.PREFLIGHT = PREFLIGHT
BASE.START_REPORT_VERSION = "cw-m5-026b-stopped-destination-start-v1"

StartError = BASE.StartError
execute = BASE.execute
start_command = BASE.start_command

if __name__ == "__main__":
    raise SystemExit(BASE.main())
