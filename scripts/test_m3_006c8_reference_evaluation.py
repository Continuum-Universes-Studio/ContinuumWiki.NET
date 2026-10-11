import json
import pathlib
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[1]
REPORT = ROOT / "artifacts/validation/mediawiki-reference-aware-evaluation-v1.json"


class ReferenceAwareEvaluationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.report = json.loads(REPORT.read_text())
        cls.by_id = {item["FixtureId"]: item for item in cls.report["Evaluations"]}

    def test_reference_errors_are_negative_evidence(self):
        mbox = self.by_id["mw-invoke-075b2608d7c07c57"]
        extension = self.by_id["mw-invoke-72066581f5f001e8"]
        self.assertEqual(mbox["ReferenceBehavior"], "REFERENCE_ERROR")
        self.assertTrue(mbox["NegativeTestEvidence"])
        self.assertIn("Module:Message box/configuration", mbox["ReferenceErrors"][0])
        self.assertIn("Template translation", " ".join(extension["ReferenceErrors"]))
        self.assertIn("Template:Infobox/styles.css", " ".join(extension["ReferenceErrors"]))
        self.assertIn("Template:Extension/status.css", " ".join(extension["ReferenceErrors"]))

    def test_successful_references_do_not_imply_continuum_comparison(self):
        successful = [item for item in self.report["Evaluations"] if item["ReferenceBehavior"] == "REFERENCE_SUCCESS"]
        self.assertEqual(len(successful), 6)
        self.assertTrue(all(not item["ComparisonPerformed"] for item in successful))

    def test_reference_hashes_and_strict_policy_are_preserved(self):
        self.assertEqual(self.report["ReferenceErrorCount"], 2)
        self.assertTrue(all(item["RawApiResponseSha256Recorded"] for item in self.report["Evaluations"]))
        self.assertEqual(self.report["Policy"]["StrictImportReadiness"], "Planner blocking findings remain mandatory.")


if __name__ == "__main__":
    unittest.main()
