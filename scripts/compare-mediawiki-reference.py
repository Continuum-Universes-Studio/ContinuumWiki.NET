#!/usr/bin/env python3
"""Conservative comparison of a private MediaWiki capture and CW output."""

import argparse
import hashlib
import json
import os
import sys
from datetime import datetime, timezone


def fail(message):
    print(f"comparison failed: {message}", file=sys.stderr)
    return 2


def load(path):
    try:
        with open(path, encoding="utf-8") as stream:
            value = json.load(stream)
    except (OSError, json.JSONDecodeError) as error:
        raise ValueError(f"cannot read {path}: {error}") from error
    if not isinstance(value, dict):
        raise ValueError(f"{path} is not a JSON object")
    return value


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reference", required=True, help="Private capture JSON")
    parser.add_argument("--continuum-output", required=True, help="JSON containing the validated CW rendered output")
    parser.add_argument("--output", required=True, help="Comparison report path")
    parser.add_argument("--classification", choices=["SEMANTICALLY_EQUIVALENT", "PARTIAL", "UNSUPPORTED", "FAILED"],
                        help="Explicit operator evidence for a non-exact comparison")
    args = parser.parse_args()
    try:
        reference = load(args.reference)
        continuum = load(args.continuum_output)
    except ValueError as error:
        return fail(str(error))

    required = ["FixtureId", "RawApiResponseSha256", "PageTitle", "OriginalInvocation", "RenderedHtml"]
    missing = [key for key in required if key not in reference]
    if missing:
        return fail("reference capture missing: " + ", ".join(missing))
    if not isinstance(continuum.get("RenderedHtml"), str):
        return fail("ContinuumWiki output must contain string RenderedHtml")
    if continuum.get("FixtureId") not in (None, reference["FixtureId"]):
        return fail("fixture identity mismatch")

    mediawiki = reference["RenderedHtml"]
    current = continuum["RenderedHtml"]
    normalized_mediawiki = mediawiki.replace("\r\n", "\n").replace("\r", "\n")
    normalized_current = current.replace("\r\n", "\n").replace("\r", "\n")
    if mediawiki == current:
        classification = "EXACT_MATCH"
    elif normalized_mediawiki == normalized_current:
        classification = "EXACT_MATCH"
    elif args.classification:
        classification = args.classification
    else:
        classification = "FAILED"

    report = {
        "ReportVersion": "cw-m3-006c.2-output-comparison-v1",
        "ComparedAtUtc": datetime.now(timezone.utc).isoformat(),
        "FixtureId": reference["FixtureId"],
        "PageTitle": reference["PageTitle"],
        "RequestedRevisionId": reference.get("RequestedRevisionId"),
        "RenderedRevisionId": reference.get("RenderedRevisionId"),
        "OriginalInvocation": reference["OriginalInvocation"],
        "ReferenceCaptureSha256": hashlib.sha256(json.dumps(reference, sort_keys=True, ensure_ascii=False).encode()).hexdigest(),
        "ReferenceRawApiResponseSha256": reference["RawApiResponseSha256"],
        "ContinuumOutputSha256": hashlib.sha256(json.dumps(continuum, sort_keys=True, ensure_ascii=False).encode()).hexdigest(),
        "Classification": classification,
        "OriginalOutputsEqual": mediawiki == current,
        "LineEndingNormalizedOutputsEqual": normalized_mediawiki == normalized_current,
        "SecurityPreservingNormalization": "line-endings-only",
        "MediaWikiApiWarnings": reference.get("Warnings"),
        "Notes": [
            "Links, escaping, whitespace other than line endings, scripts, and dangerous markup are not discarded.",
            "Non-exact classifications require explicit operator evidence; successful execution alone is insufficient."
        ]
    }
    output = os.path.abspath(args.output)
    os.makedirs(os.path.dirname(output) or ".", exist_ok=True)
    with open(output, "w", encoding="utf-8") as stream:
        json.dump(report, stream, ensure_ascii=False, indent=2)
        stream.write("\n")
    os.chmod(output, 0o600)
    print(json.dumps({"FixtureId": report["FixtureId"], "Classification": classification, "Output": output}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
