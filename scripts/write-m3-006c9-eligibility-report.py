#!/usr/bin/env python3
"""Create a strict-vs-diagnostic fixture eligibility report."""
import json
import pathlib
import sys

blocker_path, reference_path, output_path = map(pathlib.Path, sys.argv[1:4])
blockers = json.loads(blocker_path.read_text())
references = json.loads(reference_path.read_text())
reference_by_id = {item["FixtureId"]: item for item in references["Evaluations"]}
rows = []
for fixture in blockers["Fixtures"]:
    fixture_id = fixture["FixtureId"]
    reference = reference_by_id.get(fixture_id, {})
    blocking = sorted({item["Code"] for item in fixture["PlannerFindings"] if item.get("Severity") == "Blocking"})
    missing = fixture.get("GroundedMissingModuleDependencyTitles", [])
    source_safe = fixture.get("SourcePagePresent") and fixture.get("SourceRevisionPresent") and not missing
    rows.append({
        "FixtureId": fixture_id,
        "PageTitle": fixture["PageTitle"],
        "ReferenceBehavior": reference.get("ReferenceBehavior"),
        "SourcePersistenceSafety": "SAFE_CANDIDATE" if source_safe else "BLOCKED",
        "DiagnosticImportEligibility": "ELIGIBLE_IN_DISPOSABLE_POLICY" if source_safe else "INELIGIBLE",
        "StrictMigrationImportEligibility": "ELIGIBLE" if not blocking else "INELIGIBLE",
        "RenderingLimitations": fixture.get("UnsupportedExtensionTagNames", []),
        "MissingDependencies": missing,
        "PlannerBlockingCodes": blocking,
        "RuntimeExecutionEligibility": "NOT_ATTEMPTED",
        "ActualWorkerExecution": False,
        "ComparisonPerformed": False,
        "Decision": "Deferred: current strict harness rejects blocking rendering diagnostics." if source_safe else "Blocked by unresolved dependency or source safety finding.",
    })
report = {
    "ReportVersion": "cw-m3-006c.9-safe-fixture-eligibility-v1",
    "Policies": {
        "StrictMigration": "Any planner blocking finding remains a mandatory rejection.",
        "DiagnosticImport": "May only target a disposable database, preserve source verbatim, and never alter production readiness.",
        "Runtime": "Requires a complete safe import and authorized socket worker; no execution is inferred from eligibility.",
    },
    "DiagnosticEligibleCount": sum(row["DiagnosticImportEligibility"] == "ELIGIBLE_IN_DISPOSABLE_POLICY" for row in rows),
    "StrictEligibleCount": sum(row["StrictMigrationImportEligibility"] == "ELIGIBLE" for row in rows),
    "Rows": rows,
}
output_path.parent.mkdir(parents=True, exist_ok=True)
output_path.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n")
print(f"Eligibility report written to {output_path}; fixtures={len(rows)}.")
