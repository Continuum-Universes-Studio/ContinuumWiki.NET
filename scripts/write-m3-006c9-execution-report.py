#!/usr/bin/env python3
"""Record diagnostic execution outcomes without fabricating render results."""
import json
import pathlib
import sys

eligibility_path, import_path, output_path = map(pathlib.Path, sys.argv[1:4])
eligibility = json.loads(eligibility_path.read_text())
import_report = json.loads(import_path.read_text())
rows = []
for item in eligibility["Rows"]:
    if item["DiagnosticImportEligibility"] != "ELIGIBLE_IN_DISPOSABLE_POLICY":
        continue
    rows.append({
        "FixtureId": item["FixtureId"],
        "PageTitle": item["PageTitle"],
        "ImportBatchId": import_report.get("ImportBatchId"),
        "ImportStatus": import_report.get("Verdict"),
        "ExecutionStatus": "WORKER_UNAVAILABLE",
        "WorkerExecutionAttempted": True,
        "WorkerProvenance": "authorized Unix-domain socket inaccessible to this execution environment",
        "ContinuumOutputArtifact": None,
        "ComparisonArtifact": None,
        "ComparisonClassification": "NOT_EVALUATED",
        "Diagnostics": ["ScribuntoWorkerUnavailable", "SocketPermissionDenied"],
    })
report = {
    "ReportVersion": "cw-m3-006c.9-execution-v1",
    "ImportReconciliation": {
        "Verdict": import_report.get("Verdict"),
        "ImportBatchId": import_report.get("ImportBatchId"),
        "SourcePageCount": import_report.get("SourcePageCount"),
        "ImportedPageCount": import_report.get("ImportedPageCount"),
        "SourceRevisionCount": import_report.get("SourceRevisionCount"),
        "ImportedRevisionCount": import_report.get("ImportedRevisionCount"),
    },
    "AttemptedFixtureCount": len(rows),
    "RenderedFixtureCount": 0,
    "ComparedFixtureCount": 0,
    "Fixtures": rows,
}
output_path.parent.mkdir(parents=True, exist_ok=True)
output_path.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n")
print(f"Execution report written to {output_path}; attempted={len(rows)} rendered=0 compared=0.")
