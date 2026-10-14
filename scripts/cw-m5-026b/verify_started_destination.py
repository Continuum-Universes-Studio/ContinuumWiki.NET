#!/usr/bin/env python3
"""Verify the started destination container with fixed read-only Podman checks."""
from __future__ import annotations

import argparse
import importlib.util
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

def _load(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module

BASE = _load("cw_m5_026b_destination_verification_base", ROOT / "scripts/cw-m5-026b/verify_started_source.py")
PLAN = _load("cw_m5_026b_destination_verification_plan", ROOT / "scripts/cw-m5-026b/stopped_destination_diagnostic.py")
PREFLIGHT = _load("cw_m5_026b_destination_verification_preflight", ROOT / "scripts/preflight-m5-026b-stopped-destination-restart.py")

BASE.PLAN = PLAN
BASE.PREFLIGHT = PREFLIGHT
BASE.START_REPORT_VERSION = "cw-m5-026b-stopped-destination-start-v1"
BASE.VERIFICATION_REPORT_VERSION = "cw-m5-026b-started-destination-verification-v1"

VerificationError = BASE.VerificationError
execute = BASE.execute

def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--start-report", required=True)
    parser.add_argument("--report", required=True)
    parser.add_argument("--verify-started-destination", action="store_true")
    args = parser.parse_args(argv)
    if not args.verify_started_destination:
        parser.error("explicit --verify-started-destination is required")
    try:
        report = execute(Path(args.manifest), Path(args.start_report), Path(args.report))
        print(f"{report['Classification']}; podman_readonly_commands={report['PodmanReadOnlyCommands']}; database_connections=0; database_mutations=0; credentials_read=0")
        return 0 if report["Classification"] == "DESTINATION_CONTAINER_RUNNING_IDENTITY_MATCH" else 2
    except VerificationError as error:
        print(f"START_VERIFICATION_NOT_EXECUTED: {error}", file=__import__("sys").stderr)
        return 2

if __name__ == "__main__":
    raise SystemExit(main())
