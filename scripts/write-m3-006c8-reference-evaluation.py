#!/usr/bin/env python3
"""Classify preserved MediaWiki references without changing migration readiness."""
import hashlib
import json
import pathlib
import re
import sys

references_dir, blocker_path, output_path = map(pathlib.Path, sys.argv[1:4])
blockers = json.loads(blocker_path.read_text())
by_fixture = {item["FixtureId"]: item for item in blockers["Fixtures"]}

def errors(html):
    result = []
    for match in re.finditer(r"(?:Lua error[^<]*|Script error:[^<]*|Page <a[^>]*title=\"([^\"]+)\"[^>]*>[^<]*</a> has no content\.)", html):
        text = re.sub(r"\s+", " ", match.group(0)).strip()
        if text not in result:
            result.append(text)
    return result

evaluations = []
for path in sorted(references_dir.glob("mw-invoke-*.json")):
    reference = json.loads(path.read_text())
    fixture = by_fixture.get(reference["FixtureId"], {})
    html = reference.get("RenderedHtml", "")
    observed = errors(html)
    has_error = bool(observed)
    evaluations.append({
        "FixtureId": reference["FixtureId"],
        "PageTitle": reference["PageTitle"],
        "RequestedRevisionId": reference.get("RequestedRevisionId"),
        "RenderedRevisionId": reference.get("RenderedRevisionId"),
        "ReferenceBehavior": "REFERENCE_ERROR" if has_error else "REFERENCE_SUCCESS",
        "ReferenceErrors": observed,
        "RawApiResponseSha256": reference["RawApiResponseSha256"],
        "RawApiResponseSha256Recorded": bool(reference.get("RawApiResponseSha256")),
        "ReferenceFileSha256": hashlib.sha256(path.read_bytes()).hexdigest(),
        "SourceAvailability": "AVAILABLE" if fixture.get("SourceRevisionPresent") else "MISSING",
        "StrictImportEligibility": "ELIGIBLE" if fixture.get("Outcome") == "READY" else "INELIGIBLE",
        "DependencyIndependent": not fixture.get("GroundedMissingModuleDependencyTitles", []),
        "DiagnosticRuntimeEligibility": "CANDIDATE" if not fixture.get("GroundedMissingModuleDependencyTitles", []) else "BLOCKED_BY_MISSING_DEPENDENCY",
        "ComparisonPerformed": False,
        "NegativeTestEvidence": has_error,
        "PlannerCorrelation": {
            "Outcome": fixture.get("Outcome"),
            "GroundedMissingModuleDependencyTitles": fixture.get("GroundedMissingModuleDependencyTitles", []),
            "MissingDependencyProvenance": fixture.get("MissingDependencyProvenance", []),
        },
        "Notes": [
            "Reference-side errors are diagnostic evidence, not successful compatibility matches.",
            "Strict migration-import readiness remains independent of reference behavior.",
        ],
    })

report = {
    "ReportVersion": "cw-m3-006c.8-reference-aware-evaluation-v1",
    "Policy": {
        "DiagnosticEvaluation": "Reference errors are valid negative-test evidence; they never relax import gates.",
        "StrictImportReadiness": "Planner blocking findings remain mandatory.",
        "ComparisonSuccess": "Requires actual ContinuumWiki output and comparison; matching errors are not parity.",
    },
    "FixtureCount": len(evaluations),
    "ReferenceSuccessCount": sum(x["ReferenceBehavior"] == "REFERENCE_SUCCESS" for x in evaluations),
    "ReferenceErrorCount": sum(x["ReferenceBehavior"] == "REFERENCE_ERROR" for x in evaluations),
    "Evaluations": evaluations,
}
output_path.parent.mkdir(parents=True, exist_ok=True)
output_path.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n")
print(f"Reference-aware evaluation written to {output_path}; fixtures={len(evaluations)}.")
