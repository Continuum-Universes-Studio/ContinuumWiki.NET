#!/usr/bin/env python3
"""Write a credential-free fixture-level compatibility blocker report."""
import json
import pathlib
import sys

corpus_path, plan_path, output_path = map(pathlib.Path, sys.argv[1:4])
corpus = json.loads(corpus_path.read_text())
plan = json.loads(plan_path.read_text())
findings = plan.get("Findings", [])
# Evidence recorded from the preserved export's historical source.  These are
# deliberately provenance annotations, not synthetic dependency resolutions.
DEPENDENCY_PROVENANCE = {
    "Category handler": {
        "ImportingModule": "Module:Message box", "ImportingRevisionId": "6209",
        "SourceLine": 466, "Expression": "require('Module:Category handler')._main{",
        "Mode": "conditional", "Condition": "MessageBox:renderCategories only after categories are added",
        "Reachability": "not established from the captured {{#invoke:Message box|mbox}} arguments",
    },
    "libraryUtil": {
        "ImportingModule": "Module:Arguments", "ImportingRevisionId": "6203",
        "SourceLine": 5, "Expression": "local libraryUtil = require('libraryUtil')",
        "Mode": "unconditional", "Condition": None,
        "Reachability": "reachable through the captured Template:Mbox -> Message box dependency closure",
    },
    "strict": {
        "ImportingModule": "Module:Message box", "ImportingRevisionId": "6209",
        "SourceLine": 1, "Expression": "require('strict')",
        "Mode": "unconditional", "Condition": None,
        "Reachability": "reachable when the captured Template:Mbox invocation loads Message box",
    },
    "Extension": {
        "ImportingPage": "Template:Extension", "ImportingRevisionId": "7245",
        "SourceLines": [9, 45, 80, 91, 98, 108, 118, 143, 149, 164, 200, 241, 268, 273, 284, 289, 303, 347, 353, 359, 412, 414],
        "Expression": "{{#invoke:Extension|...}}",
        "Mode": "conditional-per-template-branch", "Condition": "invocations occur in #if/#ifeq branches and table cells",
        "Reachability": "directly present in the captured Template:Extension revision; branch reachability depends on template arguments",
    },
    "Template translation": {
        "ImportingPage": "Template:Extension", "ImportingRevisionId": "7245",
        "SourceLine": 416, "Expression": "{{#invoke:Template translation|renderTranslatedTemplate|template=Template:Extension|noshift=1}}",
        "Mode": "direct-in-template-source", "Condition": None,
        "Reachability": "present in the captured Template:Extension source; not proven executable without rendering that fixture",
    },
}
pages = {
    (str(page.get("SourcePageId", "")), str(revision.get("SourceRevisionId", ""))): (page, revision)
    for page in plan.get("Pages", [])
    for revision in page.get("Revisions", [])
}
outcomes = []
for fixture in corpus.get("Cases", []):
    key = (str(fixture.get("SourcePageId", "")), str(fixture.get("SourceRevisionId", fixture.get("RequestedRevisionId", ""))))
    page, revision = pages.get(key, ({}, {}))
    matched = [f for f in findings if str(f.get("SourcePageId", "")) == key[0] and str(f.get("SourceRevisionId", "")) == key[1]]
    deps = revision.get("Dependencies", {})
    page_revision_ids = [str(item.get("SourceRevisionId", "")) for item in page.get("Revisions", [])]
    requested_for_page = sorted({
        str(item.get("SourceRevisionId", item.get("RequestedRevisionId", "")))
        for item in corpus.get("Cases", [])
        if str(item.get("SourcePageId", "")) == key[0]
    })
    codes = {str(f.get("Code", "")) for f in matched if f.get("Severity") == "Blocking"}
    missing_modules = sorted({
        str(f.get("Message", "")).split("Module dependency '", 1)[1].split("'", 1)[0]
        for f in matched
        if f.get("Code") == "MissingModuleDependency" and "Module dependency '" in str(f.get("Message", ""))
    }, key=str.casefold)
    dependency_provenance = [
        {"DependencyTitle": f"Module:{title}", **DEPENDENCY_PROVENANCE[title]}
        for title in missing_modules if title in DEPENDENCY_PROVENANCE
    ]
    heuristic_templates = sorted({
        str(f.get("Message", "")).split("Template dependency '", 1)[1].split("'", 1)[0]
        for f in matched
        if f.get("Code") == "MissingTemplateDependency" and "Template dependency '" in str(f.get("Message", ""))
    }, key=str.casefold)
    unsupported_tags = sorted({
        str(f.get("Message", "")).split("tag <", 1)[1].split(">", 1)[0]
        for f in matched
        if f.get("Code") == "UnsupportedExtensionTag" and "tag <" in str(f.get("Message", ""))
    }, key=str.casefold)
    if not revision:
        outcome = "NOT_EVALUATED"
    elif codes and codes <= {"UnsupportedExtensionTag"}:
        outcome = "UNSUPPORTED"
    elif codes:
        outcome = "BLOCKED"
    else:
        outcome = "READY"
    outcomes.append({
        "FixtureId": fixture.get("FixtureId"),
        "PageTitle": fixture.get("PageTitle"),
        "SourcePageId": key[0],
        "RequestedRevisionId": key[1],
        "RawInvocation": fixture.get("RawInvocation"),
        "Templates": sorted(set(deps.get("Templates", [])), key=str.casefold),
        "Modules": sorted(set(deps.get("Modules", []) + deps.get("ModuleDependencies", [])), key=str.casefold),
        "PlannerFindings": matched,
        "GroundedMissingModuleDependencyTitles": [f"Module:{title}" for title in missing_modules],
        "MissingDependencyProvenance": dependency_provenance,
        "UnverifiedHeuristicTemplateDependencyCandidates": heuristic_templates,
        "UnsupportedExtensionTagNames": unsupported_tags,
        "ClassificationNotes": [
            "MissingModuleDependency findings are direct module references absent from the export.",
            "MissingTemplateDependency findings remain heuristic lexical candidates and are not treated as grounded titles.",
        ],
        "Outcome": outcome,
        "SourcePagePresent": bool(page),
        "SourceRevisionPresent": bool(revision),
        "HistoricalRevisionContext": {
            "RequestedRevisionIdsForSourcePage": requested_for_page,
            "SelectedRevisionIds": page_revision_ids,
            "AllRequestedRevisionsPresent": all(value in page_revision_ids for value in requested_for_page),
            "Conflict": False,
        },
        "LuaCompatibilityStatus": "NOT_RUN",
    })
report = {
    "ReportVersion": "cw-m3-006c.5-fixture-blockers-v1",
    "PlanDisposition": plan.get("OverallDisposition"),
    "PlannedPages": plan.get("PlannedPageCount"),
    "PlannedRevisions": plan.get("PlannedRevisionCount"),
    "FixtureCount": len(outcomes),
    "Fixtures": outcomes,
}
output_path.parent.mkdir(parents=True, exist_ok=True)
output_path.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n")
print(f"Fixture blocker report written to {output_path}; fixtures={len(outcomes)}.")
