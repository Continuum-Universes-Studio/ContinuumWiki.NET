#!/usr/bin/env python3
"""Source-only CW-M5-026B rehearsal data-readiness assessment.

This module reads repository SQL and retained JSON evidence only. It never
launches host tools, opens sockets, reads credential files, or connects to a
database. Its inspection plan is descriptive and cannot be executed by this
program.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[2]
REPORT_VERSION = "cw-m5-026b-data-readiness-v3"
IMAGE_PREFIX = "docker.io/library/mariadb@sha256:"
HEX64 = re.compile(r"^[0-9a-f]{64}$")

EVIDENCE_PATHS = {
    "provisioning": ROOT / "secure/evidence/mediawiki-category-backup-restore-provisioning-v1.json",
    "sourceAuthorization": ROOT / "secure/evidence/cw-m5-026b-source-verification-authorization-20261012-002.json",
    "destinationAuthorization": ROOT / "secure/evidence/cw-m5-026b-destination-provisioning-authorization-20261012-002.json",
    "continuationPreflight": ROOT / "secure/evidence/cw-m5-026b-continuation-preflight-20261012-003.json",
    "continuationResult": ROOT / "secure/evidence/cw-m5-026b-continuation-result-20261012-003.json",
}

MIGRATION_FILES = (
    "TightWiki.Repository/Scripts/MySql/Pages/V1__PageSchema.sql",
    "TightWiki.Repository/Scripts/MySql/Pages/V2__PageSupportSchema.sql",
    "TightWiki.Repository/Scripts/MySql/Pages/V3__DeletedRevisionSchema.sql",
    "TightWiki.Repository/Scripts/MySql/Category/V1__CategorySchema.sql",
    "TightWiki.Repository/Scripts/MySql/Category/V2__CategoryRecoveryDurability.sql",
    "TightWiki.Repository/Scripts/MySql/Category/V3__CategoryAdministrativeGrants.sql",
)

RUNNER_REQUIRED_TABLES = (
    "Page", "PageRevision", "PageTag", "PageReference", "PageCategoryIndexState",
    "PageCategoryMembership", "TwCategoryRecoveryOperation", "TwCategoryRecoveryAuditEvent",
    "TwCategoryAdministrativeGrant", "TwCategoryAdministrativeGrantAudit",
)

IDENTITY_SQL = "SELECT DATABASE(), VERSION(), @@version_comment, CURRENT_USER();"
GRANTS_SQL = "SHOW GRANTS;"
TABLES_SQL = "SELECT TABLE_NAME, TABLE_TYPE, COALESCE(ENGINE,'') FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() ORDER BY TABLE_NAME, TABLE_TYPE LIMIT 2049;"
SCHEMA_SQL = "SELECT TABLE_NAME, COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() ORDER BY TABLE_NAME, ORDINAL_POSITION LIMIT 8193;"
INDEX_SQL = "SELECT TABLE_NAME, INDEX_NAME, NON_UNIQUE, SEQ_IN_INDEX, COLUMN_NAME FROM information_schema.STATISTICS WHERE TABLE_SCHEMA=DATABASE() ORDER BY TABLE_NAME, INDEX_NAME, SEQ_IN_INDEX LIMIT 8193;"
CONSTRAINT_SQL = "SELECT TABLE_NAME, CONSTRAINT_NAME, CONSTRAINT_TYPE FROM information_schema.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=DATABASE() ORDER BY TABLE_NAME, CONSTRAINT_NAME LIMIT 4097;"
FOREIGN_KEY_SQL = "SELECT K.TABLE_NAME, K.CONSTRAINT_NAME, K.COLUMN_NAME, K.REFERENCED_TABLE_SCHEMA, K.REFERENCED_TABLE_NAME, K.REFERENCED_COLUMN_NAME, K.ORDINAL_POSITION, COALESCE(R.DELETE_RULE,'') FROM information_schema.KEY_COLUMN_USAGE K LEFT JOIN information_schema.REFERENTIAL_CONSTRAINTS R ON R.CONSTRAINT_SCHEMA=K.CONSTRAINT_SCHEMA AND R.CONSTRAINT_NAME=K.CONSTRAINT_NAME AND R.TABLE_NAME=K.TABLE_NAME WHERE K.TABLE_SCHEMA=DATABASE() AND K.REFERENCED_TABLE_NAME IS NOT NULL ORDER BY K.TABLE_NAME,K.CONSTRAINT_NAME,K.ORDINAL_POSITION LIMIT 4097;"
LEDGER_SQL = "SELECT 'pages' AS ledger_domain, Version FROM MySqlPageSchemaVersion WHERE Domain='pages' UNION ALL SELECT 'page-support', Version FROM MySqlPageSchemaVersion WHERE Domain='page-support' UNION ALL SELECT 'deleted-revisions', Version FROM MySqlPageSchemaVersion WHERE Domain='deleted-revisions' UNION ALL SELECT 'category', Version FROM MySqlCategorySchemaVersion WHERE Domain='category' UNION ALL SELECT 'category-recovery', Version FROM MySqlCategorySchemaVersion WHERE Domain='category-recovery' UNION ALL SELECT 'category-admin-grants', Version FROM MySqlCategorySchemaVersion WHERE Domain='category-admin-grants';"
FIXTURE_AGGREGATE_SQL = "SELECT 'Page' AS FixtureClass, COUNT(*) AS AggregateCount FROM Page UNION ALL SELECT 'PageRevision', COUNT(*) FROM PageRevision UNION ALL SELECT CONCAT('IndexState:', IndexState), COUNT(*) FROM PageCategoryIndexState GROUP BY IndexState UNION ALL SELECT 'Membership', COUNT(*) FROM PageCategoryMembership UNION ALL SELECT 'RecoveryOperation', COUNT(*) FROM TwCategoryRecoveryOperation UNION ALL SELECT 'RecoveryAudit', COUNT(*) FROM TwCategoryRecoveryAuditEvent UNION ALL SELECT 'AdministrativeGrant', COUNT(*) FROM TwCategoryAdministrativeGrant UNION ALL SELECT 'AdministrativeGrantAudit', COUNT(*) FROM TwCategoryAdministrativeGrantAudit LIMIT 64;"
FIXTURE_CASE_SQL = "SELECT (SELECT COUNT(*) FROM Page P JOIN PageCategoryIndexState S ON S.PageId=P.Id WHERE P.IsDeleted=FALSE AND S.IndexState='Verified' AND S.IndexedPageRevision=P.Revision AND NOT EXISTS (SELECT 1 FROM PageCategoryMembership M WHERE M.PageId=P.Id)) AS IndexedEmptyPages, (SELECT COUNT(*) FROM Page P LEFT JOIN PageCategoryIndexState S ON S.PageId=P.Id WHERE P.IsDeleted=FALSE AND S.PageId IS NULL) AS UnindexedPages, (SELECT COUNT(DISTINCT M.PageId) FROM PageCategoryMembership M JOIN Page P ON P.Id=M.PageId JOIN PageCategoryIndexState S ON S.PageId=P.Id WHERE P.IsDeleted=FALSE AND S.IndexState='Verified' AND S.IndexedPageRevision=P.Revision AND M.SourcePageRevision=P.Revision AND M.IndexGeneration=S.IndexGeneration) AS PopulatedCurrentPages, (SELECT COUNT(*) FROM Page WHERE IsDeleted=FALSE) AS ActivePages, (SELECT COUNT(*) FROM Page WHERE IsDeleted=TRUE) AS DeletedPages;"
DESTINATION_OBJECTS_SQL = "SELECT 'TABLES_OR_VIEWS' AS ObjectType, COUNT(*) AS ObjectCount FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() UNION ALL SELECT 'ROUTINES', COUNT(*) FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA=DATABASE() UNION ALL SELECT 'EVENTS', COUNT(*) FROM information_schema.EVENTS WHERE EVENT_SCHEMA=DATABASE() UNION ALL SELECT 'TRIGGERS', COUNT(*) FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA=DATABASE();"


class EvidenceError(ValueError):
    """Raised with a non-sensitive, stable evidence diagnostic code."""


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _load_json(path: Path, code: str) -> dict[str, Any]:
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError):
        raise EvidenceError(code) from None
    if not isinstance(document, dict):
        raise EvidenceError(code)
    return document


def _parse_timestamp(value: Any) -> datetime:
    if not isinstance(value, str) or not value.endswith("Z"):
        raise EvidenceError("EVIDENCE_TIMESTAMP_INVALID")
    try:
        parsed = datetime.fromisoformat(value[:-1] + "+00:00")
    except ValueError:
        raise EvidenceError("EVIDENCE_TIMESTAMP_INVALID") from None
    if parsed.utcoffset() != timezone.utc.utcoffset(parsed):
        raise EvidenceError("EVIDENCE_TIMESTAMP_INVALID")
    return parsed.astimezone(timezone.utc)


def _require_equal(actual: Any, expected: Any, code: str = "EVIDENCE_IDENTITY_CONFLICT") -> None:
    if actual != expected:
        raise EvidenceError(code)


def _evidence_record(name: str, path: Path, document: dict[str, Any], classification: str | None = None) -> dict[str, Any]:
    record: dict[str, Any] = {
        "Reference": str(path.relative_to(ROOT)),
        "Sha256": _sha256(path),
        "TrustGrade": "RETAINED_OPERATOR_OR_REPOSITORY_EVIDENCE_STRUCTURAL_ONLY",
    }
    timestamp = document.get("GeneratedAtUtc") or document.get("ObservedAtUtc")
    if isinstance(timestamp, str):
        record["EvidenceAtUtc"] = timestamp
    if classification:
        record["RecordedClassification"] = classification
    if name.endswith("Authorization"):
        record["AuthorizationUse"] = "HISTORICAL_TARGET_BINDING_EVIDENCE_ONLY_NOT_REHEARSAL_AUTHORITY"
    return record


def _target(document: dict[str, Any], key: str) -> dict[str, Any]:
    value = document.get(key)
    if not isinstance(value, dict):
        raise EvidenceError("EVIDENCE_TARGET_SECTION_MISSING")
    return value


def _reconcile_evidence(evidence: dict[str, dict[str, Any]]) -> dict[str, Any]:
    if not isinstance(evidence, dict) or any(key not in evidence or not isinstance(evidence[key], dict)
                                              for key in EVIDENCE_PATHS):
        raise EvidenceError("REQUIRED_EVIDENCE_MISSING_OR_MALFORMED")
    provisioning = evidence["provisioning"]
    source_auth = evidence["sourceAuthorization"]
    destination_auth = evidence["destinationAuthorization"]
    preflight = evidence["continuationPreflight"]
    continuation = evidence["continuationResult"]

    _require_equal(provisioning.get("Classification"), "PROVISIONING_FAILED_RESOURCES_RETAINED")
    _require_equal(preflight.get("Classification"), "CATEGORY_CONTINUATION_AUTHORIZATIONS_VALID_NONCONNECTING")
    _require_equal(continuation.get("Classification"), "SOURCE_VERIFIED_DESTINATION_PROVISIONED")
    if preflight.get("Valid") is not True:
        raise EvidenceError("CONTINUATION_PREFLIGHT_NOT_VALID")
    for key in ("PodmanCommands", "DatabaseConnections", "DatabaseQueries", "DatabaseMutations"):
        if preflight.get(key) != 0:
            raise EvidenceError("CONTINUATION_PREFLIGHT_COUNTS_INCONSISTENT")

    old_source = _target(_target(provisioning, "Targets"), "Source")
    old_destination = _target(_target(provisioning, "Targets"), "Destination")
    source_auth_target = _target(source_auth, "Target")
    destination_auth_target = _target(destination_auth, "Target")
    current_source = continuation.get("SourceVerification")
    current_identity = continuation.get("SourceRuntimeIdentity")
    destination = continuation.get("Destination")
    if not isinstance(current_source, dict) or not isinstance(current_identity, dict) or not isinstance(destination, dict):
        raise EvidenceError("CONTINUATION_RESULT_INCOMPLETE")

    source_name_pairs = (
        (old_source.get("ContainerName"), source_auth_target.get("ContainerName")),
        (old_source.get("VolumeName"), source_auth_target.get("VolumeName")),
        (old_source.get("DatabaseName"), source_auth_target.get("DatabaseName")),
        (old_source.get("Endpoint"), source_auth_target.get("Endpoint")),
        (old_source.get("DatabaseIdentity"), source_auth_target.get("DatabaseIdentity")),
        (old_source.get("ContainerId"), source_auth_target.get("ContainerId")),
        (old_source.get("ContainerName"), continuation.get("SourceContainerName", old_source.get("ContainerName"))),
        (old_source.get("ContainerId"), continuation.get("SourceContainerId")),
        (old_source.get("VolumeName"), continuation.get("SourceVolumeName")),
    )
    for actual, expected in source_name_pairs:
        if not actual or actual != expected:
            raise EvidenceError("SOURCE_BINDING_CONFLICT")
    _require_equal(source_auth_target.get("IdentityPrefix"), old_source.get("InstanceIdentity"), "SOURCE_IDENTITY_PREFIX_CONFLICT")
    image_digest = old_source.get("ImageDigest")
    if not isinstance(image_digest, str) or not image_digest.startswith("sha256:") or not HEX64.fullmatch(image_digest[7:]):
        raise EvidenceError("SOURCE_IMAGE_EVIDENCE_INVALID")
    image_reference = IMAGE_PREFIX + image_digest[7:]
    _require_equal(source_auth_target.get("ImageReference"), image_reference, "SOURCE_IMAGE_CONFLICT")
    _require_equal(continuation.get("PinnedImageReference"), image_reference, "SOURCE_IMAGE_CONFLICT")
    for key in ("ContainerIdVerified", "ImageDigestVerified", "VolumeVerified", "LoopbackVerified"):
        if current_source.get(key) is not True:
            raise EvidenceError("SOURCE_CONTINUATION_VERIFICATION_INCOMPLETE")
    _require_equal(current_identity.get("Accepted"), True, "SOURCE_RUNTIME_IDENTITY_NOT_VERIFIED")
    checks = current_identity.get("Checks")
    if not isinstance(checks, dict) or any(checks.get(key) != "MATCH" for key in ("DatabaseName", "VersionFamily", "VersionComment")):
        raise EvidenceError("SOURCE_RUNTIME_IDENTITY_NOT_VERIFIED")

    destination_pairs = (
        (destination.get("ContainerName"), destination_auth_target.get("ContainerName")),
        (destination.get("VolumeName"), destination_auth_target.get("VolumeName")),
        (destination.get("DatabaseName"), destination_auth_target.get("DatabaseName")),
        (destination.get("Endpoint"), destination_auth_target.get("Endpoint")),
        (destination.get("DatabaseIdentity"), destination_auth_target.get("DatabaseIdentity")),
    )
    for actual, expected in destination_pairs:
        if not actual or actual != expected:
            raise EvidenceError("DESTINATION_BINDING_CONFLICT")
    _require_equal(destination_auth_target.get("IdentityPrefix"), old_destination.get("InstanceIdentity"), "DESTINATION_IDENTITY_PREFIX_CONFLICT")
    _require_equal(destination.get("InstanceIdentity"), old_destination.get("InstanceIdentity"), "DESTINATION_IDENTITY_PREFIX_CONFLICT")
    destination_id = destination.get("ContainerId")
    if not isinstance(destination_id, str) or not HEX64.fullmatch(destination_id):
        raise EvidenceError("DESTINATION_CONTAINER_ID_INVALID")
    _require_equal(destination.get("ImageDigest"), image_reference, "DESTINATION_IMAGE_CONFLICT")
    if destination.get("EngineVersionObserved") != "11.8.9-MariaDB-ubu2404" or "mariadb" not in str(destination.get("VersionComment", "")).casefold():
        raise EvidenceError("DESTINATION_RUNTIME_VERSION_EVIDENCE_INVALID")

    provision_time = _parse_timestamp(provisioning.get("GeneratedAtUtc"))
    preflight_time = _parse_timestamp(preflight.get("GeneratedAtUtc"))
    continuation_time = _parse_timestamp(continuation.get("GeneratedAtUtc"))
    if not provision_time < preflight_time < continuation_time:
        raise EvidenceError("EVIDENCE_TIMELINE_CONFLICT")

    run_id = provisioning.get("Provisioning", {}).get("RunId") if isinstance(provisioning.get("Provisioning"), dict) else None
    if not isinstance(run_id, str) or not re.fullmatch(r"\d{8}T\d{6}Z-[A-Za-z0-9]+", run_id):
        raise EvidenceError("PROVISIONING_RUN_ID_INVALID")
    source_runner_path = ROOT / "scripts/cw-m5-026b/continue_targets.py"
    try:
        source_runner = source_runner_path.read_text(encoding="utf-8")
    except (OSError, UnicodeError):
        raise EvidenceError("CONTINUATION_SOURCE_UNAVAILABLE") from None
    run_id_is_enforced = (
        'source_run_id = evidence["Provisioning"]["RunId"]' in source_runner
        and 'io.continuumwiki.m5-026b-run-id' in source_runner
        and 'volume_labels.get("io.continuumwiki.m5-026b-run-id") != run_id' in source_runner
    )
    if not run_id_is_enforced:
        raise EvidenceError("CONTINUATION_RUN_ID_GUARD_NOT_FOUND")

    source_auth_digest = _sha256(EVIDENCE_PATHS["sourceAuthorization"])
    destination_auth_digest = _sha256(EVIDENCE_PATHS["destinationAuthorization"])
    auth_hashes = continuation.get("Authorization", {})
    preflight_hashes = preflight.get("AuthorizationFiles", {})
    if (auth_hashes.get("SourceManifestSha256") != source_auth_digest
            or preflight_hashes.get("SourceSha256") != source_auth_digest
            or auth_hashes.get("DestinationManifestSha256") != destination_auth_digest
            or preflight_hashes.get("DestinationSha256") != destination_auth_digest):
        raise EvidenceError("CONTINUATION_AUTHORIZATION_HASH_MISMATCH")

    historical_source_scope = old_source.get("ApplicationIdentitySchemaScoped")
    historical_destination_scope = destination.get("ApplicationIdentitySchemaScoped")
    source_observations = [historical_source_scope] if isinstance(historical_source_scope, bool) else []
    later_source_scope = continuation.get("SourceApplicationIdentitySchemaScoped")
    if isinstance(later_source_scope, bool):
        source_observations.append(later_source_scope)
    contradictory = len(set(source_observations)) > 1
    # The continuation verifies server identity, not the source app's grants.
    return {
        "RecordedTargetBinding": {
            "SourceMatchesProvisioningAuthorizationAndContinuation": True,
            "DestinationMatchesContinuationAuthorization": True,
            "PinnedImmutableImageMatches": True,
            "SourceContainerIdSha256": hashlib.sha256(str(old_source["ContainerId"]).encode()).hexdigest(),
            "DestinationContainerIdSha256": hashlib.sha256(destination_id.encode()).hexdigest(),
            "ProvisioningRunIdSha256": hashlib.sha256(run_id.encode()).hexdigest(),
            "ContinuationImplementationCheckedProvisioningRunIdAgainstContainerAndVolumeLabels": True,
            "ContinuationResultPersistedRunId": False,
            "RunIdFreshness": "HISTORICAL_ONLY_NOT_REATTESTED",
            "SourceAndDestinationIdentityPrefixesReconciled": True,
            "ContinuationAuthorizationHashesMatchPreflightAndManifests": True,
            "ContinuationRunnerSha256": _sha256(source_runner_path),
            "LastSuccessfulContinuationAtUtc": continuation.get("GeneratedAtUtc"),
            "CurrentRuntimeExistenceHealthAndIdentity": "UNVERIFIED",
        },
        "HistoricalApplicationGrantEvidence": {
            "Source": historical_source_scope,
            "Destination": historical_destination_scope,
            "SourceEvidenceConflict": contradictory,
            "CurrentSourceStatus": "UNKNOWN/NOT_OBSERVED",
            "CurrentDestinationStatus": "UNKNOWN/NOT_OBSERVED",
            "Reason": "Provisioning and continuation timestamps are historical; continuation did not inspect current application privileges.",
        },
        "Timeline": {
            "ProvisioningEvidenceAtUtc": provision_time.isoformat().replace("+00:00", "Z"),
            "ContinuationPreflightAtUtc": preflight_time.isoformat().replace("+00:00", "Z"),
            "ContinuationCompletedAtUtc": continuation_time.isoformat().replace("+00:00", "Z"),
            "FreshHostAttestation": "NOT_PERFORMED",
        },
    }


def _migration_inventory() -> dict[str, Any]:
    migrations: list[dict[str, Any]] = []
    for relative in MIGRATION_FILES:
        path = ROOT / relative
        try:
            sql = path.read_text(encoding="utf-8")
        except (OSError, UnicodeError):
            raise EvidenceError("SCHEMA_SOURCE_ASSET_UNAVAILABLE") from None
        table_blocks = list(re.finditer(
            r"CREATE\s+TABLE\s+IF\s+NOT\s+EXISTS\s+([A-Za-z0-9_]+)\s*\((.*?)\)\s*ENGINE\s*=\s*([A-Za-z0-9_]+)",
            sql, re.I | re.S))
        tables = [match.group(1) for match in table_blocks]
        engines = [match.group(3) for match in table_blocks]
        constraints: list[dict[str, Any]] = []
        indexes: list[dict[str, Any]] = []
        expected_columns: list[dict[str, Any]] = []
        for table_block in table_blocks:
            table_name, body = table_block.group(1), table_block.group(2)
            for line in body.splitlines():
                column = re.match(r"\s*([A-Za-z0-9_]+)\s+([A-Za-z]+)(?:\(([^)]*)\))?(.*?),?\s*$", line)
                if column and column.group(1).upper() not in {"CONSTRAINT", "INDEX", "PRIMARY", "UNIQUE", "FOREIGN", "CHECK"}:
                    column_name, sql_type, arguments, remainder = column.groups()
                    normalized_type = sql_type.casefold()
                    if normalized_type in {"bool", "boolean"}:
                        column_type = "tinyint(1)"
                    else:
                        column_type = normalized_type + (f"({arguments.replace(' ', '')})" if arguments is not None else "")
                    if re.search(r"\bUNSIGNED\b", remainder, re.I):
                        column_type += " unsigned"
                    expected_columns.append({
                        "Table": table_name,
                        "Name": column_name,
                        "ColumnType": column_type,
                        "IsNullable": "NO" if re.search(r"\bNOT\s+NULL\b", remainder, re.I) else "YES",
                    })
                constraint = re.search(r"CONSTRAINT\s+([A-Za-z0-9_]+)\s+(PRIMARY\s+KEY|FOREIGN\s+KEY|UNIQUE|CHECK)\b(.*)$", line.strip(), re.I)
                if constraint:
                    name, kind, remainder = constraint.groups()
                    item: dict[str, Any] = {"Table": table_name, "Name": name, "Kind": re.sub(r"\s+", " ", kind.upper())}
                    if item["Kind"] == "FOREIGN KEY":
                        reference = re.search(r"\s*\(([^)]+)\)\s*REFERENCES\s+([A-Za-z0-9_]+)\s*\(([^)]+)\)(?:\s+ON\s+DELETE\s+([A-Z ]+))?", remainder, re.I)
                        if not reference:
                            raise EvidenceError("SCHEMA_FOREIGN_KEY_PARSE_FAILED")
                        local_columns, referenced_table, referenced_columns, on_delete = reference.groups()
                        item["Columns"] = [value.strip().strip("`") for value in local_columns.split(",")]
                        item["ReferencedTable"] = referenced_table
                        item["ReferencedColumns"] = [value.strip().strip("`") for value in referenced_columns.split(",")]
                        if on_delete:
                            item["OnDelete"] = " ".join(on_delete.split()).upper()
                    elif item["Kind"] in {"PRIMARY KEY", "UNIQUE"}:
                        columns = re.search(r"\(([^)]+)\)", remainder)
                        item["Columns"] = [value.strip().strip("`") for value in columns.group(1).split(",")] if columns else []
                    elif item["Kind"] == "CHECK":
                        item["CheckExpressionSourcePresent"] = bool(re.search(r"CHECK\s*\(", remainder, re.I))
                    constraints.append(item)
                index = re.search(r"\b(UNIQUE\s+)?INDEX\s+([A-Za-z0-9_]+)\s*\(([^)]+)\)", line.strip(), re.I)
                if index:
                    unique, name, index_columns = index.groups()
                    indexes.append({"Table": table_name, "Name": name, "Unique": bool(unique),
                                    "Columns": [value.strip().strip("`") for value in index_columns.split(",")]})
        ledgers = [(domain, int(version)) for domain, version in re.findall(
            r"VALUES\s*\(\s*'([^']+)'\s*,\s*(\d+)\s*,\s*UTC_TIMESTAMP", sql, re.I)]
        migrations.append({
            "Source": relative,
            "Sha256": _sha256(path),
            "CreatesTables": tables,
            "Columns": expected_columns,
            "ExpectedStorageEngines": sorted(set(engines)),
            "KeysAndConstraints": constraints,
            "SecondaryIndexes": indexes,
            "ExpectedLedgerRows": [{"Domain": domain, "Version": version} for domain, version in ledgers],
            "LivePresence": "UNVERIFIED",
        })
    all_tables = [table for migration in migrations for table in migration["CreatesTables"]]
    return {
        "MigrationOrder": [
            "Page V1 base", "Page V2 support", "Page V3 deleted revisions",
            "Category V1", "Category V2 recovery durability", "Category V3 administrative grants",
        ],
        "Migrations": migrations,
        "ExpectedTableCount": len(all_tables),
        "ExpectedTables": all_tables,
        "RehearsalRunnerRequiredTables": list(RUNNER_REQUIRED_TABLES),
        "RunnerChecks": {
            "BaseTableNamesAndInnoDB": True,
            "SourceHasTenRequiredTables": True,
            "DestinationHasNoBaseTablesBeforeRestore": True,
            "TableInventoryComparedAfterRestore": True,
            "PerTableDumpContentHashesCompared": True,
            "SchemaColumnsIndexesForeignKeysAndLedgerRows": False,
            "PageRevisionAndCategoryProvenanceSemantics": False,
            "RecoveryCheckpointAndAuditSemantics": False,
            "AdministrativeGrantAndAuditSemantics": False,
            "ViewsRoutinesEventsOrOtherNonBaseObjectsInDestinationEmptinessCheck": False,
        },
        "CurrentLiveSchemaStatus": {
            "Source": "UNKNOWN/NOT_OBSERVED",
            "Destination": "UNKNOWN/NOT_OBSERVED",
            "NeitherTargetMayBeAssumedMigrated": True,
        },
    }


def _fixture_contract() -> dict[str, Any]:
    return {
        "Status": "NOT_OBSERVED; NO_FIXTURES_CREATED",
        "DatasetPurpose": "SYNTHETIC_DISPOSABLE_TEST_DATA_ONLY_NOT_HISTORICAL_PRODUCTION_DATA",
        "RequiredCases": [
            {
                "Case": "INDEXED_EMPTY",
                "Shape": "Active Page with a matching PageRevision; PageCategoryIndexState=Verified and IndexedPageRevision equals Page.Revision; zero PageCategoryMembership rows; stable source provenance and generation.",
            },
            {
                "Case": "UNINDEXED",
                "Shape": "Active Page with matching PageRevision and no PageCategoryIndexState row; no memberships. Must remain distinguishable from a verified empty index.",
            },
            {
                "Case": "POPULATED_MEMBERSHIP",
                "Shape": "Active Page and matching PageRevision; Verified index state at the current revision; at least one membership with the same generation, revision and source provenance.",
            },
            {
                "Case": "VISIBILITY",
                "Shape": "One active and one deleted Page, with state fields consistent with Page constraints; category reads exclude deleted pages and return Unknown/PageUnavailable for direct deleted-page index inspection.",
            },
            {
                "Case": "RECOVERY_V2",
                "Shape": "A deterministic operation/checkpoint plus ordered audit events covering intent, attempted/committed outcome, sequence continuity, event-ID and event-key uniqueness, and an expected version/policy.",
            },
            {
                "Case": "ADMIN_GRANT_V3",
                "Shape": "Separate issued, consumed and revoked grant records with corresponding ordered audit events; preserve single-use/replay, version/CAS, validity, revocation and correlation fields.",
            },
        ],
        "FixtureSemantics": [
            "Missing PageCategoryIndexState means Unindexed; it is not an indexed empty set.",
            "Verified state at current Page.Revision is Current; older verified revision is Stale; Incomplete, Failed and Unknown remain distinct fail-closed states.",
            "Membership rows are only trusted when their verified state, source revision, generation and provenance align.",
            "Do not export source page bodies or private category rows to readiness evidence; report only bounded aggregate checks and pass/fail codes.",
        ],
        "DDLAndWritesRequired": {
            "DDL": "Apply Page V1/V2/V3 and Category V1/V2/V3 only if absent/incompatible; each requires separate explicit migration authorization and a schema/target plan.",
            "DML": "Insert the bounded synthetic Page/PageRevision/index/membership/recovery/audit/grant fixture set; update/delete fixture rows only if separately specified and authorized.",
            "AuthorizationStatus": "NOT_GRANTED_BY_PROVISIONING_CONTINUATION_OR_REHEARSAL_AUTHORIZATION",
            "RehearsalRunnerSupport": "NONE; it rejects source seeding and performs no migration or fixture writes.",
        },
    }


def _provisioning_contract(provisioning_evidence: dict[str, Any]) -> dict[str, Any]:
    relative = "scripts/cw-m5-026b/provision_targets.py"
    path = ROOT / relative
    try:
        source = path.read_text(encoding="utf-8")
    except (OSError, UnicodeError):
        raise EvidenceError("PROVISIONING_SOURCE_ASSET_UNAVAILABLE") from None
    creates_app_schema = bool(re.search(r"\b(?:CREATE|ALTER|DROP)\s+TABLE\b", source, re.I))
    seeds_rows = bool(re.search(r"\bINSERT\s+INTO\s+(?!mysql\.)", source, re.I))
    grant_mutation_position = source.find("GRANT SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER, INDEX, REFERENCES")
    empty_check_position = source.find("table_count = run(")
    empty_guard_follows_grant = grant_mutation_position >= 0 and empty_check_position > grant_mutation_position
    provisioning_verification = provisioning_evidence.get("ProvisioningVerification", {})
    source_grant_mutation_reached = provisioning_verification.get("SourceApplicationGrantMutationReached") is True
    return {
        "Source": relative,
        "Sha256": _sha256(path),
        "CreatesDatabaseFromImageEnvironment": "MARIADB_DATABASE" in source,
        "AppliesApplicationSchemaMigrations": creates_app_schema,
        "SeedsApplicationRows": seeds_rows,
        "ContainsInitialEmptySchemaGuardSourceCode": "information_schema.tables" in source and "table_count != \"0\"" in source,
        "SourceEmptySchemaGuardRunsAfterGrantSetup": empty_guard_follows_grant,
        "HistoricalSourceEmptySchemaGuardReached": ("YES" if source_grant_mutation_reached else "NO; PRIOR EVIDENCE SAYS SOURCE GRANT MUTATION WAS NOT REACHED") if empty_guard_follows_grant else "UNKNOWN",
        "HistoricalDestinationEmptySchemaCheck": "NOT_RECORDED_IN_CONTINUATION_RESULT",
        "HistoricalApplicationGrantPrivilegesInSource": ["SELECT", "INSERT", "UPDATE", "DELETE", "CREATE", "ALTER", "INDEX", "REFERENCES"],
        "GrantFindingMeaning": "Provisioning report records schema-scope check false; this is a historical failed check and does not identify the exact effective privilege set or current state.",
    }


def _inspection_plan() -> dict[str, Any]:
    return {
        "Mode": "NONCONNECTING_PLAN_ONLY",
        "Operation": "READ_ONLY_SOURCE_AND_DESTINATION_DATA_READINESS_INSPECTION",
        "FreshDistinctOperatorApprovalRequired": True,
        "ExistingProvisioningAndContinuationApprovalsTransfer": False,
        "Targets": "Exact retained CW-M5-026B source and destination only; reconfirm runtime identities on host immediately before connection.",
        "CredentialTransport": {
            "Required": "Owner-owned 0600 client option files or an already approved OS secret-store adapter.",
            "HistoricalProvisionerCreated": "0600 environment.env and application.env files in 0700 directories.",
            "Compatibility": "Those environment files are not MariaDB client option files; the rehearsal runner's --defaults-extra-file requirement is not established by the provisioning evidence.",
            "CredentialValuesReadOrChangedHere": False,
        },
        "PlannedReadOnlySql": [
            {"Purpose": "IdentityAndVersion", "Sql": IDENTITY_SQL, "ReportOutput": "MATCH/MISMATCH codes only; do not persist raw CURRENT_USER or output."},
            {"Purpose": "EffectiveGrantScope", "Sql": GRANTS_SQL, "Handling": "Read output in memory only; persist booleans/codes for global privileges, grant option, schema confinement and role/unscoped grants; never persist grant text."},
            {"Purpose": "TablesAndEngines", "Sql": TABLES_SQL, "Handling": "Compare to static migration inventory; report counts/missing object names only."},
            {"Purpose": "Columns", "Sql": SCHEMA_SQL, "Handling": "Compare exact required structural metadata; no row values."},
            {"Purpose": "Indexes", "Sql": INDEX_SQL, "Handling": "Compare primary, unique and secondary index metadata."},
            {"Purpose": "Constraints", "Sql": CONSTRAINT_SQL, "Handling": "Compare foreign/unique/check constraints and referenced tables."},
            {"Purpose": "ForeignKeyColumns", "Sql": FOREIGN_KEY_SQL, "Handling": "Compare ordered local/referenced columns and delete rules without row data."},
            {"Purpose": "MigrationLedgers", "Sql": LEDGER_SQL, "Handling": "Persist only expected domain/version match codes; no timestamps or data values."},
            {"Purpose": "FixtureAggregateSufficiency", "Sql": FIXTURE_AGGREGATE_SQL, "Handling": "Aggregate counts and index-state counts only; do not export Page.Body, ScopeJson, grant details, or private row values."},
            {"Purpose": "IndexCaseAndVisibilityCounts", "Sql": FIXTURE_CASE_SQL, "Handling": "Returns aggregate counts distinguishing verified-empty, unindexed, current populated, active, and deleted pages; cannot prove a particular synthetic fixture exists without an approved fixture marker."},
        ],
        "DestinationEmptiness": {"Sql": DESTINATION_OBJECTS_SQL, "Requirement": "All counts must be zero before considering a future restore; report counts only. The current restore runner independently checks base tables only."},
        "NoOperations": {"Podman": 0, "DatabaseConnections": 0, "DatabaseQueries": 0, "DatabaseMutations": 0, "CredentialReads": 0, "CredentialWrites": 0},
    }


def assess(evidence: dict[str, dict[str, Any]], *, source_scope_observations: list[bool] | None = None) -> dict[str, Any]:
    if not isinstance(evidence, dict) or any(key not in evidence or not isinstance(evidence[key], dict)
                                              for key in EVIDENCE_PATHS):
        raise EvidenceError("REQUIRED_EVIDENCE_MISSING_OR_MALFORMED")
    identity = _reconcile_evidence(evidence)
    if source_scope_observations:
        observed = set(source_scope_observations)
        identity["HistoricalApplicationGrantEvidence"]["SourceEvidenceConflict"] = len(observed) > 1
        if len(observed) > 1:
            identity["HistoricalApplicationGrantEvidence"]["CurrentSourceStatus"] = "UNKNOWN/NOT_OBSERVED"
    schema = _migration_inventory()
    identity["HistoricalApplicationGrantEvidence"]["CurrentSourceStatus"] = "UNKNOWN/NOT_OBSERVED"
    identity["HistoricalApplicationGrantEvidence"]["CurrentDestinationStatus"] = "UNKNOWN/NOT_OBSERVED"
    return {
        "ReportVersion": REPORT_VERSION,
        "GeneratedAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "Milestone": "CW-M5-026B-PREFLIGHT-READINESS",
        "Classification": "CATEGORY_REHEARSAL_DATA_READINESS_SOURCE_VERIFIED",
        "SourceAssessment": "SOURCE_EVIDENCE_VERIFIED",
        "HistoricalRuntimeEvidence": "HISTORICAL_ONLY",
        "LiveInspection": "LIVE_INSPECTION_REQUIRED",
        "SourceGrantRemediation": "SOURCE_REMEDIATION_REQUIRES_AUTHORIZATION",
        "SchemaAndFixtureReadiness": "SCHEMA_OR_FIXTURE_INSUFFICIENT",
        "Certification": "NOT_CERTIFIED",
        "ProductionMigration": "NO_GO",
        "RepositoryActivation": "NO_GO",
        "EvidenceIntegrity": {
            "Evidence": [
                _evidence_record("provisioning", EVIDENCE_PATHS["provisioning"], evidence["provisioning"], evidence["provisioning"].get("Classification")),
                _evidence_record("sourceAuthorization", EVIDENCE_PATHS["sourceAuthorization"], evidence["sourceAuthorization"]),
                _evidence_record("destinationAuthorization", EVIDENCE_PATHS["destinationAuthorization"], evidence["destinationAuthorization"]),
                _evidence_record("continuationPreflight", EVIDENCE_PATHS["continuationPreflight"], evidence["continuationPreflight"], evidence["continuationPreflight"].get("Classification")),
                _evidence_record("continuationResult", EVIDENCE_PATHS["continuationResult"], evidence["continuationResult"], evidence["continuationResult"].get("Classification")),
            ],
            "SensitiveValuesIncluded": False,
            "RawGrantTextIncluded": False,
            "PageOrCategoryRowsIncluded": False,
        },
        "IdentityAndRuntime": identity,
        "GrantReadiness": {
            "CurrentSource": "UNKNOWN/NOT_OBSERVED",
            "CurrentDestination": "UNKNOWN/NOT_OBSERVED",
            "HistoricalSourceObservation": "SCHEMA_SCOPE_CHECK_FAILED_AT_PROVISIONING_EVIDENCE_TIME" if evidence["provisioning"].get("Targets", {}).get("Source", {}).get("ApplicationIdentitySchemaScoped") is False else "NOT_OBSERVED",
            "HistoricalDestinationObservation": "SCHEMA_SCOPED_REPORTED_AT_CONTINUATION_TIME" if evidence["continuationResult"].get("Destination", {}).get("ApplicationIdentitySchemaScoped") is True else "NOT_OBSERVED",
            "ClassificationEnum": ["VERIFIED_SCHEMA_SCOPED", "CONFIRMED_OVERPRIVILEGED", "UNKNOWN/NOT_OBSERVED", "INSPECTION_FAILED"],
            "RunnerCheck": "Rehearsal runner rejects global grants, grant option, unscoped role/grant statements, and grants outside the target schema; it does not compare a minimum privilege allowlist.",
            "CurrentClassification": "UNKNOWN/NOT_OBSERVED",
            "ClassificationSemantics": {
                "VERIFIED_SCHEMA_SCOPED": "Fresh identity-specific evidence shows no effective global privileges, grant option, unrelated schema grant, or unscoped role; this is confinement evidence, not a least-privilege certificate.",
                "CONFIRMED_OVERPRIVILEGED": "Fresh evidence confirms effective global, unrelated-schema, grant-option, or unscoped privilege.",
                "UNKNOWN/NOT_OBSERVED": "No fresh privilege inspection or evidence is historical/ambiguous.",
                "INSPECTION_FAILED": "The bounded inspection could not complete or returned malformed/ambiguous evidence.",
            },
            "Remediation": "Only if a newly authorized read-only inspection confirms a grant defect; any grant change requires independent operation-specific approval.",
        },
        "SchemaExpectations": schema,
        "ProvisioningWorkflowEvidence": _provisioning_contract(evidence["provisioning"]),
        "FixtureSufficiency": _fixture_contract(),
        "AuthorizationGapRegister": [
            {"Operation": "Current read-only target/privilege/schema/aggregate inspection", "Status": "REQUIRES_FRESH_DISTINCT_EXACT_TARGET_APPROVAL"},
            {"Operation": "Source grant remediation", "Status": "SEPARATE_AUTHORIZATION_REQUIRED_IF_INSPECTION_CONFIRMS_NEED"},
            {"Operation": "Page V1/V2/V3 and Category V1/V2/V3 migrations", "Status": "NOT_AUTHORIZED; EACH MIGRATION_SCOPE_MUST_BE_EXPLICIT"},
            {"Operation": "Synthetic source fixture seeding", "Status": "NOT_SUPPORTED_BY_RUNNER_AND_NOT_AUTHORIZED"},
            {"Operation": "Backup/snapshot", "Status": "FRESH_REHEARSAL_AUTHORIZATION_REQUIRED"},
            {"Operation": "Destination restore", "Status": "FRESH_REHEARSAL_AUTHORIZATION_REQUIRED_AFTER_EMPTYNESS_PROOF"},
            {"Operation": "Comparison/evidence retention", "Status": "EXPLICIT_BOUNDED_AUTHORIZATION_REQUIRED"},
            {"Operation": "Fault injection/crash tests/post-snapshot writes", "Status": "EXCLUDED; SEPARATE_IMPLEMENTATION_AND_AUTHORIZATION_REQUIRED"},
        ],
        "InspectionPlan": _inspection_plan(),
        "RunnerLimitations": [
            "The current runner does not apply migrations or seed test fixtures.",
            "The current runner requires all ten base tables and InnoDB, and only compares table/engine inventories plus per-table dump content hashes.",
            "It does not validate schema columns, indexes, foreign keys, schema ledger versions, page/category provenance, recovery checkpoints/audits, or administrative-grant/audit semantics.",
            "Its destination emptiness check counts base tables only; this readiness plan requires broader object inventory before any future restore.",
            "A content-hash match is not schema/recovery certification and does not prove historical data provenance or point-in-time recovery.",
        ],
        "ResourceOperationCounts": {"Podman": 0, "DatabaseConnections": 0, "DatabaseQueries": 0, "DatabaseMutations": 0, "GrantChanges": 0, "Migrations": 0, "FixtureWrites": 0, "Backup": 0, "Restore": 0, "Cleanup": 0, "CredentialValueReads": 0, "CredentialWrites": 0},
    }


def load_repository_evidence() -> dict[str, dict[str, Any]]:
    return {
        key: _load_json(path, f"EVIDENCE_UNAVAILABLE_{key.upper()}")
        for key, path in EVIDENCE_PATHS.items()
    }


def write_report_once(path: Path, report: dict[str, Any]) -> None:
    path = path.expanduser()
    if path.exists():
        raise EvidenceError("REPORT_PATH_ALREADY_EXISTS")
    path.parent.mkdir(parents=True, exist_ok=True)
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0)
    descriptor = os.open(path, flags, 0o644)
    with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
        json.dump(report, stream, indent=2)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", default=str(ROOT / "artifacts/validation/mediawiki-category-backup-restore-data-readiness-v3.json"))
    args = parser.parse_args(argv)
    try:
        report = assess(load_repository_evidence())
        write_report_once(Path(args.report), report)
    except (EvidenceError, OSError, TypeError, KeyError) as error:
        diagnostic = str(error) if isinstance(error, EvidenceError) else "SOURCE_ASSESSMENT_FAILED"
        print(f"CW-M5-026B readiness assessment stopped: {diagnostic}", file=sys.stderr)
        return 2
    print("CW-M5-026B source-only readiness: classification=CATEGORY_REHEARSAL_DATA_READINESS_SOURCE_VERIFIED; live_inspection=required; report=" + args.report)
    return 0


if __name__ == "__main__":
    sys.exit(main())
