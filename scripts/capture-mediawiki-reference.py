#!/usr/bin/env python3
"""Read-only, bounded MediaWiki Action API reference capture."""

import argparse
import hashlib
import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone

MAX_RESPONSE_BYTES = 8 * 1024 * 1024
TIMEOUT_SECONDS = 20


class NoRedirectHandler(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise urllib.error.HTTPError(req.full_url, code, "redirect refused", headers, fp)


def fail(message):
    print(f"capture failed: {message}", file=sys.stderr)
    return 2


def sanitize_api_message(value):
    text = " ".join(str(value).split())
    return "".join(character for character in text if character.isprintable())[:512]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--api-url", required=True, help="Approved HTTPS MediaWiki Action API URL")
    parser.add_argument("--approved-origin", required=True, help="Explicit approved HTTPS origin, e.g. https://wiki.example.org")
    parser.add_argument("--page", required=True, help="Existing MediaWiki page title")
    parser.add_argument("--oldid", type=int, help="Historical page revision ID")
    parser.add_argument("--invocation", required=True, help="Original #invoke expression recorded in the inventory")
    parser.add_argument("--output", required=True, help="Private JSON capture path")
    parser.add_argument("--fixture-id", help="Source corpus fixture ID for diagnostics and provenance")
    args = parser.parse_args()

    parsed = urllib.parse.urlparse(args.api_url)
    if parsed.scheme != "https" or not parsed.netloc or parsed.username or parsed.password:
        return fail("--api-url must be an HTTPS URL without embedded credentials")
    origin = f"{parsed.scheme}://{parsed.netloc}"
    approved = urllib.parse.urlparse(args.approved_origin)
    approved_origin = f"{approved.scheme}://{approved.netloc}" if approved.scheme and approved.netloc else ""
    if approved.scheme != "https" or approved.username or approved.password or origin != approved_origin:
        return fail("--api-url must exactly match the explicitly approved HTTPS origin")
    fixture_key = "|".join([origin, args.page, str(args.oldid or "current"), args.invocation])
    fixture_id = args.fixture_id or ("mw-ref-" + hashlib.sha256(fixture_key.encode("utf-8")).hexdigest()[:16])
    query = {
        "action": "parse",
        "prop": "text|revid|modules|jsconfigvars",
        "format": "json",
        "formatversion": "2",
    }
    if args.oldid is not None:
        # MediaWiki parse rejects page+oldid together; oldid identifies the page.
        query["oldid"] = str(args.oldid)
    else:
        query["page"] = args.page
    url = args.api_url + ("&" if "?" in args.api_url else "?") + urllib.parse.urlencode(query)
    if urllib.parse.urlparse(url).netloc != parsed.netloc:
        return fail("constructed request left the approved origin")

    request = urllib.request.Request(url, method="GET", headers={"Accept": "application/json"})
    bearer = os.environ.get("CW_MEDIAWIKI_BEARER_TOKEN")
    if bearer:
        request.add_header("Authorization", f"Bearer {bearer}")
    opener = urllib.request.build_opener(NoRedirectHandler)
    try:
        with opener.open(request, timeout=TIMEOUT_SECONDS) as response:
            if response.geturl().split("/", 3)[:3] != origin.split("/", 3)[:3]:
                return fail("response origin differed from the approved origin")
            raw = response.read(MAX_RESPONSE_BYTES + 1)
            if len(raw) > MAX_RESPONSE_BYTES:
                return fail(f"response exceeded {MAX_RESPONSE_BYTES} bytes")
            status = response.status
    except (urllib.error.URLError, urllib.error.HTTPError, TimeoutError) as error:
        return fail(str(error))

    try:
        payload = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        return fail(f"response was not valid UTF-8 JSON: {error}")
    if not isinstance(payload, dict):
        return fail("API response was not a JSON object")
    if "error" in payload:
        error = payload.get("error")
        if isinstance(error, dict):
            code = sanitize_api_message(error.get("code", "unknown")) or "unknown"
            message = sanitize_api_message(error.get("info", error.get("message", "unspecified error")))
            return fail(f"fixture={fixture_id} MediaWiki API error code={code} message={message}; no reference fixture was written")
        return fail(f"fixture={fixture_id} MediaWiki API error code=unknown message=malformed error object; no reference fixture was written")
    parse = payload.get("parse")
    if not isinstance(parse, dict) or not isinstance(parse.get("text"), str):
        return fail("MediaWiki API response did not contain parse.text")

    capture = {
        "ReportVersion": "cw-m3-006c.2-reference-capture-v1",
        "FixtureId": fixture_id,
        "CapturedAtUtc": datetime.now(timezone.utc).isoformat(),
        "MediaWikiApiUrl": origin,
        "RequestUrl": url,
        "HttpStatus": status,
        "PageTitle": args.page,
        "RequestedRevisionId": args.oldid,
        "RenderedRevisionId": parse.get("revid"),
        "OriginalInvocation": args.invocation,
        "RenderedHtml": parse["text"],
        "Modules": parse.get("modules"),
        "Warnings": payload.get("warnings"),
        "RawApiResponseSha256": hashlib.sha256(raw).hexdigest(),
    }
    output = os.path.abspath(args.output)
    os.makedirs(os.path.dirname(output) or ".", exist_ok=True)
    with open(output, "w", encoding="utf-8") as stream:
        json.dump(capture, stream, ensure_ascii=False, indent=2)
        stream.write("\n")
    os.chmod(output, 0o600)
    print(json.dumps({"FixtureId": fixture_id, "Output": output, "RawApiResponseSha256": capture["RawApiResponseSha256"]}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
