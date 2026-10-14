#!/usr/bin/env python3
"""Run one separately authorized, read-only diagnostic on the retained destination."""
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

BASE = _load("cw_m5_026b_destination_diagnostic_runner_base", ROOT / "scripts/cw-m5-026b/diagnose_stopped_source.py")
PREFLIGHT = _load("cw_m5_026b_destination_diagnostic_preflight", ROOT / "scripts/preflight-m5-026b-stopped-destination-diagnostic.py")
PLAN = _load("cw_m5_026b_destination_diagnostic_plan_for_runner", ROOT / "scripts/cw-m5-026b/stopped_destination_diagnostic.py")

BASE.PREFLIGHT = PREFLIGHT
BASE.PLAN = PLAN

DiagnosticError = BASE.DiagnosticError
execute = BASE.execute
validate_execution = BASE.validate_execution

if __name__ == "__main__":
    raise SystemExit(BASE.main())
