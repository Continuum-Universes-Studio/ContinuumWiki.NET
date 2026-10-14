#!/usr/bin/env python3
"""Offline tests for CW-M5-026B rehearsal readiness assessment."""

import copy
import importlib.util
import json
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("cw_m5_026b_readiness", ROOT / "scripts/cw-m5-026b/readiness.py")
assert SPEC and SPEC.loader
READINESS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(READINESS)


class M5026BReadinessTests(unittest.TestCase):
    def setUp(self):
        self.evidence = READINESS.load_repository_evidence()

    def test_retained_evidence_reconciles_but_live_state_remains_unknown(self):
        with patch.object(subprocess, "run") as run:
            report = READINESS.assess(self.evidence)
        run.assert_not_called()
        self.assertEqual("CATEGORY_REHEARSAL_DATA_READINESS_SOURCE_VERIFIED", report["Classification"])
        self.assertEqual("SOURCE_EVIDENCE_VERIFIED", report["SourceAssessment"])
        self.assertEqual("HISTORICAL_ONLY", report["HistoricalRuntimeEvidence"])
        self.assertEqual("LIVE_INSPECTION_REQUIRED", report["LiveInspection"])
        binding = report["IdentityAndRuntime"]["RecordedTargetBinding"]
        self.assertTrue(binding["SourceMatchesProvisioningAuthorizationAndContinuation"])
        self.assertTrue(binding["DestinationMatchesContinuationAuthorization"])
        self.assertEqual("UNVERIFIED", binding["CurrentRuntimeExistenceHealthAndIdentity"])
        self.assertEqual("UNKNOWN/NOT_OBSERVED", report["GrantReadiness"]["CurrentSource"])
        self.assertEqual("UNKNOWN/NOT_OBSERVED", report["GrantReadiness"]["CurrentDestination"])

    def test_historical_source_scope_finding_is_not_promoted_to_current_truth(self):
        report = READINESS.assess(self.evidence)
        grants = report["GrantReadiness"]
        self.assertEqual("SCHEMA_SCOPE_CHECK_FAILED_AT_PROVISIONING_EVIDENCE_TIME", grants["HistoricalSourceObservation"])
        self.assertEqual("UNKNOWN/NOT_OBSERVED", grants["CurrentSource"])

    def test_contradictory_historical_scope_observations_remain_unknown(self):
        report = READINESS.assess(self.evidence, source_scope_observations=[False, True])
        grant_evidence = report["IdentityAndRuntime"]["HistoricalApplicationGrantEvidence"]
        self.assertTrue(grant_evidence["SourceEvidenceConflict"])
        self.assertEqual("UNKNOWN/NOT_OBSERVED", grant_evidence["CurrentSourceStatus"])

    def test_missing_evidence_fails_closed(self):
        evidence = dict(self.evidence)
        evidence.pop("continuationResult")
        with self.assertRaises(READINESS.EvidenceError):
            READINESS.assess(evidence)

    def test_malformed_timestamp_fails_closed(self):
        evidence = copy.deepcopy(self.evidence)
        evidence["continuationResult"]["GeneratedAtUtc"] = "not-a-timestamp"
        with self.assertRaisesRegex(READINESS.EvidenceError, "EVIDENCE_TIMESTAMP_INVALID"):
            READINESS.assess(evidence)

    def test_stale_or_out_of_order_evidence_is_not_accepted_as_a_current_attestation(self):
        evidence = copy.deepcopy(self.evidence)
        evidence["provisioning"]["GeneratedAtUtc"] = "2026-10-13T13:00:00Z"
        with self.assertRaisesRegex(READINESS.EvidenceError, "EVIDENCE_TIMELINE_CONFLICT"):
            READINESS.assess(evidence)

    def test_conflicting_target_identity_fails_closed(self):
        evidence = copy.deepcopy(self.evidence)
        evidence["continuationResult"]["Destination"]["DatabaseName"] = "different_database"
        with self.assertRaisesRegex(READINESS.EvidenceError, "DESTINATION_BINDING_CONFLICT"):
            READINESS.assess(evidence)

    def test_blank_database_and_missing_fixtures_are_never_inferred_as_ready(self):
        report = READINESS.assess(self.evidence)
        self.assertEqual("UNKNOWN/NOT_OBSERVED", report["SchemaExpectations"]["CurrentLiveSchemaStatus"]["Source"])
        self.assertEqual("UNKNOWN/NOT_OBSERVED", report["SchemaExpectations"]["CurrentLiveSchemaStatus"]["Destination"])
        self.assertEqual("NOT_OBSERVED; NO_FIXTURES_CREATED", report["FixtureSufficiency"]["Status"])
        self.assertEqual("SCHEMA_OR_FIXTURE_INSUFFICIENT", report["SchemaAndFixtureReadiness"])

    def test_incompatible_schema_versions_are_only_expectations_not_live_evidence(self):
        report = READINESS.assess(self.evidence)
        expected = {
            (row["Domain"], row["Version"])
            for migration in report["SchemaExpectations"]["Migrations"]
            for row in migration["ExpectedLedgerRows"]
        }
        self.assertEqual({("pages", 1), ("page-support", 2), ("deleted-revisions", 3),
                          ("category", 1), ("category-recovery", 2), ("category-admin-grants", 3)}, expected)
        self.assertEqual("UNVERIFIED", report["SchemaExpectations"]["Migrations"][0]["LivePresence"])

    def test_provisioning_source_did_not_apply_schema_or_fixture_rows(self):
        contract = READINESS._provisioning_contract(self.evidence["provisioning"])
        self.assertTrue(contract["CreatesDatabaseFromImageEnvironment"])
        self.assertFalse(contract["AppliesApplicationSchemaMigrations"])
        self.assertFalse(contract["SeedsApplicationRows"])
        self.assertTrue(contract["ContainsInitialEmptySchemaGuardSourceCode"])
        self.assertFalse(contract["HistoricalSourceEmptySchemaGuardReached"].startswith("YES"))
        self.assertEqual("NOT_RECORDED_IN_CONTINUATION_RESULT", contract["HistoricalDestinationEmptySchemaCheck"])
        self.assertEqual(["SELECT", "INSERT", "UPDATE", "DELETE", "CREATE", "ALTER", "INDEX", "REFERENCES"],
                         contract["HistoricalApplicationGrantPrivilegesInSource"])

    def test_inventory_coverage_and_runner_gaps_are_explicit(self):
        report = READINESS.assess(self.evidence)
        expectations = report["SchemaExpectations"]
        self.assertEqual(21, expectations["ExpectedTableCount"])
        self.assertEqual(list(READINESS.RUNNER_REQUIRED_TABLES), expectations["RehearsalRunnerRequiredTables"])
        self.assertFalse(expectations["RunnerChecks"]["SchemaColumnsIndexesForeignKeysAndLedgerRows"])
        self.assertFalse(expectations["RunnerChecks"]["PageRevisionAndCategoryProvenanceSemantics"])
        self.assertTrue(all(m["ExpectedStorageEngines"] == ["InnoDB"] for m in expectations["Migrations"]))

    def test_schema_inventory_lists_key_columns_foreign_keys_and_index_columns(self):
        report = READINESS.assess(self.evidence)
        migrations = report["SchemaExpectations"]["Migrations"]
        all_constraints = [constraint for migration in migrations for constraint in migration["KeysAndConstraints"]]
        all_indexes = [index for migration in migrations for index in migration["SecondaryIndexes"]]
        fk = next(item for item in all_constraints if item["Name"] == "FK_TwCategoryRecoveryAuditEvent_Operation")
        self.assertEqual(["OperationId"], fk["Columns"])
        self.assertEqual("TwCategoryRecoveryOperation", fk["ReferencedTable"])
        self.assertEqual(["OperationId"], fk["ReferencedColumns"])
        index = next(item for item in all_indexes if item["Name"] == "IX_PageCategoryMembership_CategorySort")
        self.assertEqual(["CategoryNavigation", "SortKey", "PageId"], index["Columns"])
        self.assertTrue(any(item["Kind"] == "PRIMARY KEY" and "Columns" in item for item in all_constraints))

    def test_report_contract_version_advances_without_overwriting_v1(self):
        report = READINESS.assess(self.evidence)
        self.assertEqual("cw-m5-026b-data-readiness-v3", report["ReportVersion"])
        self.assertTrue((ROOT / "artifacts/validation/mediawiki-category-backup-restore-data-readiness-v1.json").is_file())
        self.assertTrue((ROOT / "artifacts/validation/mediawiki-category-backup-restore-data-readiness-v2.json").is_file())

    def test_fixture_contract_distinguishes_indexed_empty_from_unindexed(self):
        cases = {item["Case"]: item["Shape"] for item in READINESS._fixture_contract()["RequiredCases"]}
        self.assertIn("zero PageCategoryMembership rows", cases["INDEXED_EMPTY"])
        self.assertIn("no PageCategoryIndexState row", cases["UNINDEXED"])
        self.assertIn("current revision", cases["POPULATED_MEMBERSHIP"])
        self.assertEqual("SYNTHETIC_DISPOSABLE_TEST_DATA_ONLY_NOT_HISTORICAL_PRODUCTION_DATA",
                         READINESS._fixture_contract()["DatasetPurpose"])

    def test_authorization_gaps_keep_migrations_fixtures_and_grants_separate(self):
        report = READINESS.assess(self.evidence)
        operations = {item["Operation"]: item["Status"] for item in report["AuthorizationGapRegister"]}
        self.assertIn("FRESH_DISTINCT_EXACT_TARGET_APPROVAL", operations["Current read-only target/privilege/schema/aggregate inspection"])
        self.assertIn("NOT_AUTHORIZED", operations["Page V1/V2/V3 and Category V1/V2/V3 migrations"])
        self.assertIn("NOT_SUPPORTED", operations["Synthetic source fixture seeding"])

    def test_report_does_not_include_credentials_grant_text_or_page_rows(self):
        report = READINESS.assess(self.evidence)
        serialized = json.dumps(report).casefold()
        self.assertNotIn("credentialreference", serialized)
        self.assertNotIn("password=", serialized)
        self.assertNotIn("grant all privileges on", serialized)
        self.assertNotIn("/home/cdjensen94/.local/state/continuumwiki", serialized)
        self.assertFalse(report["EvidenceIntegrity"]["SensitiveValuesIncluded"])

    def test_sql_inspection_plan_is_static_and_has_no_secret_transport(self):
        plan = READINESS._inspection_plan()
        self.assertEqual("NONCONNECTING_PLAN_ONLY", plan["Mode"])
        self.assertTrue(plan["FreshDistinctOperatorApprovalRequired"])
        self.assertFalse(plan["ExistingProvisioningAndContinuationApprovalsTransfer"])
        self.assertEqual(0, plan["NoOperations"]["DatabaseConnections"])
        self.assertEqual(READINESS.IDENTITY_SQL, next(x["Sql"] for x in plan["PlannedReadOnlySql"] if x["Purpose"] == "IdentityAndVersion"))
        self.assertEqual("SHOW GRANTS;", READINESS.GRANTS_SQL)
        self.assertIn("not MariaDB client option files", plan["CredentialTransport"]["Compatibility"])

    def test_report_write_is_create_only(self):
        report = READINESS.assess(self.evidence)
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp, "report.json")
            READINESS.write_report_once(path, report)
            original = path.read_text(encoding="utf-8")
            with self.assertRaisesRegex(READINESS.EvidenceError, "REPORT_PATH_ALREADY_EXISTS"):
                READINESS.write_report_once(path, report)
            self.assertEqual(original, path.read_text(encoding="utf-8"))

    def test_generated_report_is_sanitized_and_operations_are_zero(self):
        report = READINESS.assess(self.evidence)
        self.assertEqual({"Podman": 0, "DatabaseConnections": 0, "DatabaseQueries": 0, "DatabaseMutations": 0,
                          "GrantChanges": 0, "Migrations": 0, "FixtureWrites": 0, "Backup": 0, "Restore": 0,
                          "Cleanup": 0, "CredentialValueReads": 0, "CredentialWrites": 0}, report["ResourceOperationCounts"])
        self.assertEqual("NOT_CERTIFIED", report["Certification"])
        self.assertEqual("NO_GO", report["ProductionMigration"])
        self.assertEqual("NO_GO", report["RepositoryActivation"])


if __name__ == "__main__":
    unittest.main()
