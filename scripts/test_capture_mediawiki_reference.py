import importlib.util
import io
import json
import tempfile
import unittest
from contextlib import redirect_stderr
from unittest.mock import patch


SPEC = importlib.util.spec_from_file_location(
    "capture_mediawiki_reference", "scripts/capture-mediawiki-reference.py"
)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class FakeResponse:
    status = 200

    def __init__(self, payload):
        self.payload = json.dumps(payload).encode("utf-8")

    def __enter__(self):
        return self

    def __exit__(self, *_):
        return False

    def geturl(self):
        return "https://wiki.example.test/w/api.php"

    def read(self, _limit):
        return self.payload


class FakeOpener:
    def __init__(self, response):
        self.response = response
        self.request = None

    def open(self, request, timeout):
        self.request = request
        self.timeout = timeout
        return self.response


class CaptureTests(unittest.TestCase):
    def run_capture(self, payload):
        opener = FakeOpener(FakeResponse(payload))
        argv = [
            "capture-mediawiki-reference.py",
            "--api-url", "https://wiki.example.test/w/api.php",
            "--approved-origin", "https://wiki.example.test",
            "--page", "Jovan",
            "--oldid", "9482",
            "--invocation", "{{#invoke:DivineTree|renderLocal}}",
            "--fixture-id", "mw-invoke-testfixture",
            "--output", "/tmp/unused-capture-test.json",
        ]
        stderr = io.StringIO()
        with patch.object(MODULE.sys, "argv", argv), patch.object(
            MODULE.urllib.request, "build_opener", return_value=opener
        ), redirect_stderr(stderr):
            status = MODULE.main()
        return status, opener, stderr.getvalue()

    def test_historical_request_uses_oldid_without_page(self):
        status, opener, _ = self.run_capture({"parse": {"text": "ok", "revid": 9482}})
        self.assertEqual(status, 0)
        query = opener.request.full_url.split("?", 1)[1]
        self.assertIn("oldid=9482", query)
        self.assertNotIn("page=", query)

    def test_api_error_reports_fixture_code_and_sanitized_message(self):
        status, _, stderr = self.run_capture({
            "error": {
                "code": "badvalue",
                "info": "bad\nvalue\twith\x00control",
            }
        })
        self.assertEqual(status, 2)
        self.assertIn("fixture=mw-invoke-testfixture", stderr)
        self.assertIn("code=badvalue", stderr)
        self.assertIn("message=bad value withcontrol", stderr)
        self.assertNotIn("\\x00", stderr)


if __name__ == "__main__":
    unittest.main()
