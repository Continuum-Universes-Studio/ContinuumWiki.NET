#!/usr/bin/env python3
"""Exact-target, read-only CW-M5-026B data-readiness inspection.

All host commands and SQL are fixed allowlists. No shell is used. The runner
does not migrate, seed, back up, restore, repair grants, or clean up resources.
"""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
import pwd
import re
import socket
import stat
import subprocess
import sys
from collections import defaultdict
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable

ROOT = Path(__file__).resolve().parents[2]
REPORT_ROOT = Path("/home/cdjensen94/continuumwiki-validation")
PREFLIGHT_PATH = ROOT / "scripts/preflight-m5-026b-inspection.py"
SPEC = importlib.util.spec_from_file_location("cw_m5_026b_inspection_preflight", PREFLIGHT_PATH)
assert SPEC and SPEC.loader
PREFLIGHT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PREFLIGHT)
READINESS_PATH = ROOT / "scripts/cw-m5-026b/readiness.py"
READINESS_SPEC = importlib.util.spec_from_file_location("cw_m5_026b_readiness_for_inspector", READINESS_PATH)
assert READINESS_SPEC and READINESS_SPEC.loader
READINESS = importlib.util.module_from_spec(READINESS_SPEC)
READINESS_SPEC.loader.exec_module(READINESS)

PODMAN = "/usr/bin/podman"
MARIADB = "/usr/bin/mariadb"
RUN_ID_LABEL = "io.continuumwiki.m5-026b-run-id"
DB_LABEL = "io.continuumwiki.m5-026b-database"
MAX_SECONDS = 60
MAX_QUERY_ROWS = 8192
HEX64 = re.compile(r"^[0-9a-f]{64}$")

# Query text comes only from this module or the reviewed source-only readiness plan.
SQL: dict[str, str] = {
    "Identity": READINESS.IDENTITY_SQL,
    "Grants": READINESS.GRANTS_SQL,
    "Tables": READINESS.TABLES_SQL,
    "Columns": READINESS.SCHEMA_SQL,
    "Indexes": READINESS.INDEX_SQL,
    "Constraints": READINESS.CONSTRAINT_SQL,
    "ForeignKeys": READINESS.FOREIGN_KEY_SQL,
    "Ledgers": READINESS.LEDGER_SQL,
    "FixtureAggregate": READINESS.FIXTURE_AGGREGATE_SQL,
    "FixtureCases": READINESS.FIXTURE_CASE_SQL,
    "DestinationObjects": READINESS.DESTINATION_OBJECTS_SQL,
}


class InspectionError(ValueError):
    pass


def _now() -> datetime:
    return datetime.now(timezone.utc)


def _run(command: list[str], *, timeout: int = MAX_SECONDS) -> subprocess.CompletedProcess[str]:
    """Run only already-constructed fixed allowlist commands, without a shell."""
    try:
        return subprocess.run(command, shell=False, check=False, capture_output=True,
                              text=True, timeout=timeout,
                              env={"PATH": "/usr/bin:/bin", "HOME": str(Path.home()),
                                   "XDG_RUNTIME_DIR": f"/run/user/{os.getuid()}", "LC_ALL": "C"})
    except (OSError, subprocess.TimeoutExpired):
        raise InspectionError("AUTHORIZED_UTILITY_FAILED_OR_TIMED_OUT") from None


def _stdout(result: subprocess.CompletedProcess[str], code: str) -> str:
    if result.returncode != 0 or not isinstance(result.stdout, str):
        raise InspectionError(code)
    return result.stdout.strip()


def _parse_one_line(value: str, fields: int, code: str) -> list[str]:
    lines = value.splitlines()
    if len(lines) != 1:
        raise InspectionError(code)
    parts = lines[0].split("|")
    if len(parts) != fields:
        raise InspectionError(code)
    return parts


def _port_matches(value: str, endpoint: str) -> bool:
    rows = [line.strip() for line in value.splitlines() if line.strip()]
    if len(rows) != 1:
        return False
    row = rows[0]
    observed = row.rsplit("->", 1)[-1].strip() if "->" in row else row
    return observed == endpoint


def podman_commands(target: dict[str, Any]) -> list[list[str]]:
    """Canonical, non-mutating Podman commands; values are manifest-bound."""
    name = target["ContainerName"]
    volume = target["VolumeName"]
    image = target["ImageReference"]
    container_format = (
        "{{.Id}}|{{.State.Running}}|{{.ImageName}}|{{.Image}}|"
        f'{{{{index .Config.Labels "{RUN_ID_LABEL}"}}}}|'
        f'{{{{index .Config.Labels "{DB_LABEL}"}}}}|'
        "{{range .Mounts}}{{.Name}}={{.Destination}};{{end}}"
    )
    return [
        [PODMAN, "container", "inspect", "--format", container_format, name],
        [PODMAN, "port", name, "3306/tcp"],
        [PODMAN, "image", "inspect", "--format", '{{.Id}}|{{join .RepoDigests ","}}', image],
        [PODMAN, "volume", "inspect", "--format", f'{{{{.Name}}}}|{{{{index .Labels "{RUN_ID_LABEL}"}}}}', volume],
    ]


def _inspect_target_host(target: dict[str, Any], run: Callable[..., subprocess.CompletedProcess[str]]) -> None:
    container_cmd, port_cmd, image_cmd, volume_cmd = podman_commands(target)
    container = _parse_one_line(_stdout(run(container_cmd), "CONTAINER_INSPECT_FAILED"), 7, "CONTAINER_INSPECT_MALFORMED")
    container_id, running, image_name, image_id, run_id_label, database_label, mounts = container
    if container_id != target["ContainerId"]:
        raise InspectionError("CONTAINER_ID_MISMATCH")
    if running.casefold() not in {"true", "false"}:
        raise InspectionError("CONTAINER_RUNNING_STATE_MALFORMED")
    if running.casefold() != "true":
        raise InspectionError("CONTAINER_NOT_RUNNING")
    # ImageName is Podman's documented container image-name field. It is a
    # display/reference string, not proof of image identity; the immutable
    # image ID and RepoDigests are independently checked below.
    if image_name != target["ImageReference"]:
        raise InspectionError("CONFIGURED_IMAGE_REFERENCE_MISMATCH")
    if run_id_label != target["ProvisioningRunId"]:
        raise InspectionError("PROVISIONING_RUN_ID_LABEL_MISMATCH")
    if database_label != target["DatabaseName"]:
        raise InspectionError("DATABASE_LABEL_MISMATCH")
    if not any(item == f"{target['VolumeName']}=/var/lib/mysql" for item in mounts.rstrip(";").split(";") if item):
        raise InspectionError("CONTAINER_VOLUME_MOUNT_MISMATCH")
    if not _port_matches(_stdout(run(port_cmd), "PORT_INSPECT_FAILED"), target["Endpoint"]):
        raise InspectionError("LOOPBACK_PORT_BINDING_MISMATCH")
    image_fields = _parse_one_line(_stdout(run(image_cmd), "IMAGE_INSPECT_FAILED"), 2, "IMAGE_INSPECT_MALFORMED")
    if image_fields[0] != image_id or target["ImageReference"] not in image_fields[1].split(","):
        raise InspectionError("IMMUTABLE_IMAGE_DIGEST_MISMATCH")
    volume_fields = _parse_one_line(_stdout(run(volume_cmd), "VOLUME_INSPECT_FAILED"), 2, "VOLUME_INSPECT_MALFORMED")
    if volume_fields != [target["VolumeName"], target["ProvisioningRunId"]]:
        raise InspectionError("VOLUME_IDENTITY_MISMATCH")


def _check_credential_option_file(target: dict[str, Any], uid: int) -> Path:
    directory = Path(target["CredentialDirectory"])
    path = Path(target["CredentialOptionFile"])
    try:
        if directory.resolve(strict=True) != directory or path.resolve(strict=True) != path:
            raise InspectionError("CREDENTIAL_PATH_NOT_CANONICAL")
        dstat = directory.lstat()
        pstat = path.lstat()
    except OSError:
        raise InspectionError("CREDENTIAL_OPTION_FILE_UNAVAILABLE") from None
    if (not stat.S_ISDIR(dstat.st_mode) or stat.S_ISLNK(dstat.st_mode) or dstat.st_uid != uid or stat.S_IMODE(dstat.st_mode) != 0o700
            or not stat.S_ISREG(pstat.st_mode) or stat.S_ISLNK(pstat.st_mode) or pstat.st_uid != uid or stat.S_IMODE(pstat.st_mode) != 0o600
            or path.parent != directory):
        raise InspectionError("CREDENTIAL_OPTION_FILE_PERMISSIONS_INVALID")
    return path


def _db_command(target: dict[str, Any], credential: Path, sql: str) -> list[str]:
    if sql not in SQL.values():
        raise InspectionError("SQL_NOT_ALLOWLISTED")
    port = target["Endpoint"].rsplit(":", 1)[1]
    return [MARIADB, f"--defaults-extra-file={credential}", "--protocol=tcp", "--host=127.0.0.1",
            "--port", port, "--user", target["DatabaseIdentity"], "--database", target["DatabaseName"],
            "--batch", "--skip-column-names", "--raw", "--execute", sql]


def _query(target: dict[str, Any], credential: Path, name: str, run: Callable[..., subprocess.CompletedProcess[str]]) -> tuple[bool, str]:
    sql = SQL.get(name)
    if sql is None:
        raise InspectionError("QUERY_NAME_NOT_ALLOWLISTED")
    result = run(_db_command(target, credential, sql))
    if result.returncode != 0 or not isinstance(result.stdout, str):
        return False, "QUERY_FAILED"
    if len(result.stdout) > 4_000_000:
        return False, "QUERY_RESULT_TOO_LARGE"
    return True, result.stdout


def _rows(text: str, fields: int, code: str, max_rows: int = MAX_QUERY_ROWS) -> list[list[str]]:
    rows: list[list[str]] = []
    if text == "":
        return rows
    for line in text.splitlines():
        parts = line.split("\t")
        if len(parts) != fields or len(rows) >= max_rows:
            raise InspectionError(code)
        rows.append(parts)
    return rows


def _identity_status(text: str, target: dict[str, Any]) -> str:
    fields = text.rstrip("\n").split("\t")
    if len(fields) != 4:
        return "IDENTITY_RESPONSE_MALFORMED"
    database, version, comment, current_user = fields
    if (database != target["DatabaseName"] or not version.startswith("11.8.")
            or "mariadb" not in comment.casefold()
            or not current_user.startswith(target["DatabaseIdentity"] + "@")):
        return "DATABASE_IDENTITY_MISMATCH"
    return "MATCH"


def classify_grants(raw: str, database: str) -> dict[str, Any]:
    """Reduce SHOW GRANTS in memory; never return the source lines."""
    lines = [line.strip() for line in raw.splitlines() if line.strip()]
    if not lines or any(not line.upper().startswith("GRANT ") or " TO " not in re.sub(r"\s+", " ", line.upper()) for line in lines):
        return {"Classification": "INSPECTION_FAILED", "Codes": ["GRANT_OUTPUT_MALFORMED"]}
    global_privileges = False
    grant_option = False
    unscoped = False
    unrelated = False
    for line in lines:
        upper = re.sub(r"\s+", " ", line.upper())
        grant_option |= "GRANT OPTION" in upper
        if " ON " not in upper:
            if not upper.startswith("GRANT USAGE "):
                unscoped = True
            continue
        scope = upper.split(" ON ", 1)[1].split(" TO ", 1)[0].strip()
        if scope == "*.*":
            if not upper.startswith("GRANT USAGE "):
                global_privileges = True
            continue
        match = re.fullmatch(r"`?([A-Z0-9_]+)`?\.`?(?:\*|[A-Z0-9_]+)`?", scope)
        if not match or match.group(1) != database.upper():
            unrelated = True
    codes: list[str] = []
    if global_privileges:
        codes.append("GLOBAL_PRIVILEGES_PRESENT")
    if grant_option:
        codes.append("GRANT_OPTION_PRESENT")
    if unscoped:
        codes.append("UNSCOPED_ROLE_OR_GRANT_PRESENT")
    if unrelated:
        codes.append("UNRELATED_OR_UNPARSEABLE_SCOPE_PRESENT")
    return {
        "Classification": "VERIFIED_SCHEMA_SCOPED" if not codes else "CONFIRMED_OVERPRIVILEGED",
        "GlobalPrivilegesAbsent": not global_privileges,
        "GrantOptionAbsent": not grant_option,
        "AllGrantsConfinedToTargetSchema": not unrelated and not global_privileges,
        "UnexpectedRoleOrUnscopedGrantAbsent": not unscoped,
        "Codes": codes,
    }


def _expected_schema() -> dict[str, Any]:
    inventory = READINESS._migration_inventory()
    tables: set[str] = set()
    columns: dict[tuple[str, str], dict[str, str]] = {}
    indexes: dict[tuple[str, str], tuple[bool, list[str]]] = {}
    constraints: dict[tuple[str, str], str] = {}
    foreign: dict[tuple[str, str], dict[str, Any]] = {}
    ledgers: dict[str, int] = {}
    for migration in inventory["Migrations"]:
        tables.update(migration["CreatesTables"])
        for column in migration["Columns"]:
            columns[(column["Table"], column["Name"])] = {"ColumnType": column["ColumnType"], "IsNullable": column["IsNullable"]}
        for constraint in migration["KeysAndConstraints"]:
            key = (constraint["Table"], constraint["Name"])
            constraints[key] = constraint["Kind"]
            if constraint["Kind"] == "FOREIGN KEY":
                foreign[key] = {k: constraint[k] for k in ("Columns", "ReferencedTable", "ReferencedColumns", "OnDelete") if k in constraint}
            elif constraint["Kind"] == "PRIMARY KEY":
                indexes[(constraint["Table"], "PRIMARY")] = (False, constraint["Columns"])
            elif constraint["Kind"] == "UNIQUE":
                indexes[(constraint["Table"], constraint["Name"])] = (False, constraint["Columns"])
        for index in migration["SecondaryIndexes"]:
            indexes[(index["Table"], index["Name"])] = (not index["Unique"], index["Columns"])
        for row in migration["ExpectedLedgerRows"]:
            ledgers[row["Domain"]] = row["Version"]
    return {"Tables": tables, "Columns": columns, "Indexes": indexes,
            "Constraints": constraints, "ForeignKeys": foreign, "Ledgers": ledgers}


def _normalize_type(value: str) -> str:
    value = re.sub(r"\s+", " ", value.casefold().strip())
    value = re.sub(r"\b(int|integer|bigint|smallint|mediumint|tinyint)\(\d+\)", r"\1", value)
    return value


def compare_schema(data: dict[str, str], query_results: dict[str, str]) -> dict[str, Any]:
    expected = _expected_schema()
    tables = _rows(query_results["Tables"], 3, "TABLES_RESPONSE_MALFORMED", 2048)
    columns = _rows(query_results["Columns"], 4, "COLUMNS_RESPONSE_MALFORMED", 8192)
    stats = _rows(query_results["Indexes"], 5, "INDEXES_RESPONSE_MALFORMED", 8192)
    constraints = _rows(query_results["Constraints"], 3, "CONSTRAINTS_RESPONSE_MALFORMED", 4096)
    fks = _rows(query_results["ForeignKeys"], 8, "FOREIGN_KEYS_RESPONSE_MALFORMED", 4096)
    table_rows = {(r[0], r[1]): r[2] for r in tables}
    observed_tables = {name for name, kind in table_rows if kind == "BASE TABLE"}
    missing_tables = sorted(expected["Tables"] - observed_tables)
    unexpected_tables = sorted(observed_tables - expected["Tables"])
    unexpected_objects = sorted(f"{name}:{kind}" for name, kind in table_rows if kind != "BASE TABLE" or name not in expected["Tables"])
    non_innodb = sorted(name for (name, kind), engine in table_rows.items() if kind == "BASE TABLE" and engine.casefold() != "innodb")

    observed_columns = {(r[0], r[1]): {"ColumnType": _normalize_type(r[2]), "IsNullable": r[3].upper()} for r in columns}
    missing_columns = sorted(f"{t}.{c}" for t, c in expected["Columns"] if (t, c) not in observed_columns)
    unexpected_columns = sorted(f"{t}.{c}" for t, c in observed_columns if t in observed_tables and (t, c) not in expected["Columns"])
    incompatible_columns = sorted(f"{t}.{c}" for (t, c), meta in expected["Columns"].items()
                                  if (t, c) in observed_columns and any(observed_columns[(t, c)][field] != (_normalize_type(meta[field]) if field == "ColumnType" else meta[field]) for field in meta))

    actual_indexes_group: dict[tuple[str, str], list[tuple[int, bool, str]]] = defaultdict(list)
    for table, name, non_unique, sequence, column in stats:
        if non_unique not in {"0", "1"} or not sequence.isdigit():
            raise InspectionError("INDEXES_RESPONSE_MALFORMED")
        actual_indexes_group[(table, name)].append((int(sequence), non_unique == "1", column))
    actual_indexes = {key: (all(item[1] for item in sorted(vals)), [item[2] for item in sorted(vals)]) for key, vals in actual_indexes_group.items()}
    missing_indexes = sorted(f"{t}.{n}" for (t, n) in expected["Indexes"] if (t, n) not in actual_indexes)
    unexpected_indexes = sorted(f"{t}.{n}" for t, n in actual_indexes if t in observed_tables and (t, n) not in expected["Indexes"])
    incompatible_indexes = sorted(f"{t}.{n}" for (t, n), value in expected["Indexes"].items() if (t, n) in actual_indexes and actual_indexes[(t, n)] != value)

    observed_constraints = {(r[0], r[1]): r[2].upper() for r in constraints}
    expected_constraint_kinds = {key: value for key, value in expected["Constraints"].items()}
    missing_constraints = sorted(f"{t}.{n}" for (t, n) in expected_constraint_kinds if (t, n) not in observed_constraints)
    unexpected_constraints = sorted(f"{t}.{n}" for t, n in observed_constraints if t in observed_tables and (t, n) not in expected_constraint_kinds)
    kind_map = {"PRIMARY KEY": "PRIMARY KEY", "FOREIGN KEY": "FOREIGN KEY", "UNIQUE": "UNIQUE", "CHECK": "CHECK"}
    incompatible_constraints = sorted(f"{t}.{n}" for (t, n), kind in expected_constraint_kinds.items()
                                      if (t, n) in observed_constraints and observed_constraints[(t, n)] != kind_map.get(kind, kind))
    fk_group: dict[tuple[str, str], list[list[str]]] = defaultdict(list)
    for row in fks:
        fk_group[(row[0], row[1])].append(row)
    actual_foreign: dict[tuple[str, str], dict[str, Any]] = {}
    for key, rows in fk_group.items():
        ordered = sorted(rows, key=lambda x: int(x[6]) if x[6].isdigit() else -1)
        actual_foreign[key] = {"Columns": [x[2] for x in ordered], "ReferencedSchema": ordered[0][3],
                               "ReferencedTable": ordered[0][4], "ReferencedColumns": [x[5] for x in ordered],
                               "OnDelete": ordered[0][7].upper()}
        if any(x[3] != ordered[0][3] or x[4] != ordered[0][4] or x[7].upper() != ordered[0][7].upper() for x in ordered):
            raise InspectionError("FOREIGN_KEYS_RESPONSE_INCONSISTENT")
    expected_foreign = {
        key: {**value, "ReferencedSchema": data["DatabaseName"]}
        for key, value in expected["ForeignKeys"].items()
    }
    missing_fks = sorted(f"{t}.{n}" for (t, n) in expected_foreign if (t, n) not in actual_foreign)
    unexpected_fks = sorted(f"{t}.{n}" for t, n in actual_foreign if (t, n) not in expected_foreign)
    incompatible_fks = sorted(f"{t}.{n}" for (t, n), value in expected_foreign.items() if (t, n) in actual_foreign and actual_foreign[(t, n)] != value)

    ledger_rows = _rows(query_results["Ledgers"], 2, "LEDGERS_RESPONSE_MALFORMED")
    ledger_actual: dict[str, list[int]] = defaultdict(list)
    for domain, version in ledger_rows:
        if not version.isdigit():
            raise InspectionError("LEDGERS_RESPONSE_MALFORMED")
        ledger_actual[domain].append(int(version))
    ledger_missing = sorted(domain for domain in expected["Ledgers"] if domain not in ledger_actual)
    ledger_mismatch = sorted(domain for domain, version in expected["Ledgers"].items()
                             if domain in ledger_actual and ledger_actual[domain] != [version])
    ledger_unexpected = sorted(domain for domain in ledger_actual if domain not in expected["Ledgers"])
    checks = {
        "MissingTables": missing_tables, "UnexpectedTables": unexpected_tables, "UnexpectedSchemaObjects": unexpected_objects,
        "NonInnoDbTables": non_innodb,
        "MissingColumns": missing_columns, "UnexpectedColumns": unexpected_columns, "IncompatibleColumns": incompatible_columns,
        "MissingIndexes": missing_indexes, "UnexpectedIndexes": unexpected_indexes, "IncompatibleIndexes": incompatible_indexes,
        "MissingConstraints": missing_constraints, "UnexpectedConstraints": unexpected_constraints,
        "IncompatibleConstraints": incompatible_constraints, "MissingForeignKeys": missing_fks,
        "UnexpectedForeignKeys": unexpected_fks, "IncompatibleForeignKeys": incompatible_fks,
        "MissingLedgerDomains": ledger_missing, "MismatchedLedgerDomains": ledger_mismatch,
        "UnexpectedLedgerDomains": ledger_unexpected,
    }
    okay = not any(checks.values())
    return {"Classification": "SCHEMA_MATCH" if okay else "SCHEMA_MISMATCH",
            "ExpectedTableCount": len(expected["Tables"]), "ObservedBaseTableCount": len(observed_tables),
            "ExpectedColumnCount": len(expected["Columns"]), "ObservedColumnCount": len(observed_columns),
            "ExpectedIndexCount": len(expected["Indexes"]), "ObservedIndexCount": len(actual_indexes),
            "ExpectedForeignKeyCount": len(expected["ForeignKeys"]), "ObservedForeignKeyCount": len(actual_foreign),
            "LedgerVersionStatus": "MATCH" if not (ledger_missing or ledger_mismatch or ledger_unexpected) else "MISMATCH",
            "Differences": checks}


def _fixture_summary(aggregate_text: str, cases_text: str) -> dict[str, Any]:
    aggregate = _rows(aggregate_text, 2, "FIXTURE_AGGREGATE_MALFORMED")
    counts: dict[str, int] = {}
    allowed_prefixes = {"Page", "PageRevision", "Membership", "RecoveryOperation", "RecoveryAudit", "AdministrativeGrant", "AdministrativeGrantAudit", "IndexState:"}
    for key, value in aggregate:
        if not value.isdigit():
            raise InspectionError("FIXTURE_AGGREGATE_MALFORMED")
        if key.startswith("IndexState:") and key.partition(":")[2] not in {"Verified", "Incomplete", "Failed", "Unknown"}:
            key = "OtherIndexState"
        elif key not in allowed_prefixes and not any(key.startswith(x) for x in allowed_prefixes if x.endswith(":")):
            key = "OtherIndexState"
        counts[key] = counts.get(key, 0) + int(value)
    case_rows = _rows(cases_text, 5, "FIXTURE_CASES_MALFORMED", 16)
    if len(case_rows) != 1 or any(not value.isdigit() for value in case_rows[0]):
        raise InspectionError("FIXTURE_CASES_MALFORMED")
    names = ("IndexedEmptyPages", "UnindexedPages", "PopulatedCurrentPages", "ActivePages", "DeletedPages")
    case_counts = dict(zip(names, (int(value) for value in case_rows[0])))
    basic_cases = all(case_counts[name] > 0 for name in ("IndexedEmptyPages", "UnindexedPages", "PopulatedCurrentPages", "ActivePages", "DeletedPages"))
    deep_cases = counts.get("RecoveryOperation", 0) > 0 and counts.get("RecoveryAudit", 0) > 0 and counts.get("AdministrativeGrant", 0) > 0 and counts.get("AdministrativeGrantAudit", 0) > 0
    return {"Classification": "AGGREGATE_CASES_PRESENT_SEMANTICS_UNVERIFIED" if basic_cases and deep_cases else "AGGREGATE_FIXTURE_SUFFICIENCY_INCOMPLETE",
            "Counts": counts, "IndexAndVisibilityCounts": case_counts,
            "Coverage": {"BasicIndexAndVisibilityCasesPresent": basic_cases,
                         "RecoveryAndGrantRowsPresent": deep_cases,
                         "RecoveryAndGrantSemanticsProven": False,
                         "ExplicitSyntheticFixtureIdentityProven": False}}


def _destination_empty(text: str) -> dict[str, Any]:
    rows = _rows(text, 2, "DESTINATION_OBJECT_COUNTS_MALFORMED", 8)
    values: dict[str, int] = {}
    for object_type, count in rows:
        if object_type not in {"TABLES_OR_VIEWS", "ROUTINES", "EVENTS", "TRIGGERS"} or not count.isdigit() or object_type in values:
            raise InspectionError("DESTINATION_OBJECT_COUNTS_MALFORMED")
        values[object_type] = int(count)
    if set(values) != {"TABLES_OR_VIEWS", "ROUTINES", "EVENTS", "TRIGGERS"}:
        raise InspectionError("DESTINATION_OBJECT_COUNTS_INCOMPLETE")
    return {"Classification": "EMPTY" if all(value == 0 for value in values.values()) else "NOT_EMPTY", "ObjectCounts": values}


def _target_inspection(target: dict[str, Any], role: str, run: Callable[..., subprocess.CompletedProcess[str]], uid: int) -> dict[str, Any]:
    output: dict[str, Any] = {"HostIsolation": "MATCH", "DatabaseIdentity": "UNKNOWN", "GrantScope": {"Classification": "UNKNOWN"},
                              "Schema": {"Classification": "UNKNOWN"}, "QueryStatus": {}}
    try:
        credential = _check_credential_option_file(target, uid)
    except InspectionError as error:
        output["HostIsolation"] = "MATCH"
        output["CredentialTransport"] = str(error)
        output["Classification"] = "INSPECTION_BLOCKED"
        return output
    ok, identity = _query(target, credential, "Identity", run)
    output["QueryStatus"]["Identity"] = "COMPLETED" if ok else identity
    if not ok:
        output["DatabaseIdentity"] = "INSPECTION_FAILED"
        output["Classification"] = "INSPECTION_FAILED"
        return output
    status = _identity_status(identity, target)
    output["DatabaseIdentity"] = status
    if status != "MATCH":
        output["Classification"] = "TARGET_IDENTITY_MISMATCH"
        return output
    query_text: dict[str, str] = {}
    query_names = ["Grants", "Tables", "Columns", "Indexes", "Constraints", "ForeignKeys", "Ledgers"]
    if role == "Source":
        query_names.extend(["FixtureAggregate", "FixtureCases"])
    else:
        query_names.append("DestinationObjects")
    for name in query_names:
        ok, value = _query(target, credential, name, run)
        output["QueryStatus"][name] = "COMPLETED" if ok else value
        if ok:
            query_text[name] = value
    if "Grants" in query_text:
        output["GrantScope"] = classify_grants(query_text.pop("Grants"), target["DatabaseName"])
    schema_names = {"Tables", "Columns", "Indexes", "Constraints", "ForeignKeys", "Ledgers"}
    if schema_names.issubset(query_text):
        try:
            output["Schema"] = compare_schema(target, query_text)
        except InspectionError as error:
            output["Schema"] = {"Classification": "INSPECTION_FAILED", "Codes": [str(error)]}
    else:
        output["Schema"] = {"Classification": "INSPECTION_INCOMPLETE", "MissingQueryResults": sorted(schema_names - query_text.keys())}
    if role == "Source":
        if {"FixtureAggregate", "FixtureCases"}.issubset(query_text):
            try:
                output["FixtureSufficiency"] = _fixture_summary(query_text["FixtureAggregate"], query_text["FixtureCases"])
            except InspectionError as error:
                output["FixtureSufficiency"] = {"Classification": "INSPECTION_FAILED", "Codes": [str(error)]}
        else:
            output["FixtureSufficiency"] = {"Classification": "INSPECTION_INCOMPLETE"}
    else:
        if "DestinationObjects" in query_text:
            try:
                output["DestinationEmptiness"] = _destination_empty(query_text["DestinationObjects"])
            except InspectionError as error:
                output["DestinationEmptiness"] = {"Classification": "INSPECTION_FAILED", "Codes": [str(error)]}
        else:
            output["DestinationEmptiness"] = {"Classification": "INSPECTION_INCOMPLETE"}
    parsed_statuses = {"SCHEMA_MATCH", "SCHEMA_MISMATCH"}
    parsed = output["Schema"].get("Classification") in parsed_statuses
    if role == "Source":
        parsed = parsed and output.get("FixtureSufficiency", {}).get("Classification") in {
            "AGGREGATE_CASES_PRESENT_SEMANTICS_UNVERIFIED", "AGGREGATE_FIXTURE_SUFFICIENCY_INCOMPLETE"}
    else:
        parsed = parsed and output.get("DestinationEmptiness", {}).get("Classification") in {"EMPTY", "NOT_EMPTY"}
    grants_parsed = output.get("GrantScope", {}).get("Classification") in {"VERIFIED_SCHEMA_SCOPED", "CONFIRMED_OVERPRIVILEGED"}
    output["Classification"] = ("INSPECTION_COMPLETED" if parsed and grants_parsed
                                 and all(x == "COMPLETED" for x in output["QueryStatus"].values())
                                 else "INSPECTION_INCOMPLETE")
    return output


def _write_once(path: Path, report: dict[str, Any]) -> None:
    if not path.is_absolute() or not str(path).startswith(str(REPORT_ROOT) + "/"):
        raise InspectionError("REPORT_PATH_UNSAFE")
    try:
        if path.parent.resolve(strict=True) != path.parent or os.path.commonpath((str(REPORT_ROOT.resolve()), str(path.parent.resolve(strict=True)))) != str(REPORT_ROOT.resolve()):
            raise InspectionError("REPORT_PATH_UNSAFE")
    except OSError:
        raise InspectionError("REPORT_DIRECTORY_UNAVAILABLE") from None
    parent = path.parent
    try:
        info = parent.lstat()
    except OSError:
        raise InspectionError("REPORT_DIRECTORY_UNAVAILABLE") from None
    if not stat.S_ISDIR(info.st_mode) or stat.S_ISLNK(info.st_mode) or info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) != 0o700:
        raise InspectionError("REPORT_DIRECTORY_PERMISSIONS_INVALID")
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0), 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as stream:
        json.dump(report, stream, indent=2, sort_keys=True)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())


def _validate_report_target(path: Path, uid: int) -> None:
    if not path.is_absolute() or not str(path).startswith(str(REPORT_ROOT) + "/"):
        raise InspectionError("REPORT_PATH_UNSAFE")
    if any(part in {".", ".."} for part in path.parts):
        raise InspectionError("REPORT_PATH_UNSAFE")
    try:
        parent = path.parent.lstat()
        path.lstat()
    except FileNotFoundError:
        # The directory must exist; only the leaf may be absent.
        if not path.parent.exists():
            raise InspectionError("REPORT_DIRECTORY_UNAVAILABLE") from None
    except OSError:
        raise InspectionError("REPORT_PATH_UNAVAILABLE") from None
    else:
        raise InspectionError("REPORT_PATH_ALREADY_EXISTS")
    parent = path.parent.lstat()
    if (path.parent.resolve(strict=True) != path.parent
            or os.path.commonpath((str(REPORT_ROOT.resolve()), str(path.parent.resolve(strict=True)))) != str(REPORT_ROOT.resolve())
            or not stat.S_ISDIR(parent.st_mode) or stat.S_ISLNK(parent.st_mode)
            or parent.st_uid != uid or stat.S_IMODE(parent.st_mode) != 0o700):
        raise InspectionError("REPORT_DIRECTORY_PERMISSIONS_INVALID")


def run_inspection(manifest: dict[str, Any], preflight_report: dict[str, Any], *, approval_id: str,
                   run: Callable[..., subprocess.CompletedProcess[str]] = _run,
                   host: tuple[str, str, int] | None = None) -> dict[str, Any]:
    validated = PREFLIGHT.validate_manifest(manifest)
    digest = hashlib.sha256(json.dumps(manifest, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
    now = _now()
    try:
        preflight_time = datetime.fromisoformat(preflight_report["GeneratedAtUtc"].replace("Z", "+00:00"))
        expiry = datetime.fromisoformat(manifest["ExpiresAtUtc"].replace("Z", "+00:00"))
    except (KeyError, ValueError, TypeError, AttributeError):
        raise InspectionError("PREFLIGHT_EVIDENCE_MALFORMED") from None
    if (approval_id != manifest["ApprovalId"] or preflight_report.get("Classification") != "CATEGORY_READONLY_INSPECTION_AUTHORIZATION_VALID_NONCONNECTING"
            or preflight_report.get("ManifestSha256") != digest or (now - preflight_time).total_seconds() > 600
            or preflight_time > now or now >= expiry):
        raise InspectionError("FRESH_PREFLIGHT_OR_APPROVAL_MISMATCH")
    actual_host = host or (socket.gethostname(), pwd.getpwuid(os.getuid()).pw_name, os.getuid())
    authorized = manifest["ExecutionHost"]
    attempts = {"PodmanReadOnlyCommands": 0, "DatabaseConnections": 0, "DatabaseQueries": 0}
    allowed_podman = {tuple([PODMAN, "info", "--format", "{{.Host.Security.Rootless}}"])}
    for role in ("Source", "Destination"):
        allowed_podman.update(tuple(command) for command in podman_commands(manifest[role]))

    def counted_run(command: list[str]) -> subprocess.CompletedProcess[str]:
        if command and command[0] == PODMAN:
            attempts["PodmanReadOnlyCommands"] += 1
            if tuple(command) not in allowed_podman:
                raise InspectionError("UNSAFE_PODMAN_COMMAND_REJECTED")
        elif command and command[0] == MARIADB:
            attempts["DatabaseConnections"] += 1
            attempts["DatabaseQueries"] += 1
            bound_target = next((manifest[name] for name in ("Source", "Destination")
                                 if command[1] == f"--defaults-extra-file={manifest[name]['CredentialOptionFile']}"), None)
            if len(command) != 15 or command[1] not in {
                f"--defaults-extra-file={manifest['Source']['CredentialOptionFile']}",
                f"--defaults-extra-file={manifest['Destination']['CredentialOptionFile']}",
            } or bound_target is None or command[2:4] != ["--protocol=tcp", "--host=127.0.0.1"] or command[4:6] != ["--port", bound_target["Endpoint"].rsplit(":", 1)[1]] or command[6:8] != ["--user", bound_target["DatabaseIdentity"]] or command[8:10] != ["--database", bound_target["DatabaseName"]] or command[10:14] != ["--batch", "--skip-column-names", "--raw", "--execute"] or command[-1] not in SQL.values():
                raise InspectionError("UNSAFE_DATABASE_COMMAND_REJECTED")
        else:
            raise InspectionError("UNAUTHORIZED_EXECUTABLE_REJECTED")
        return run(command)

    base_report: dict[str, Any] = {
        "ReportVersion": "cw-m5-026b-readonly-inspection-result-v1",
        "GeneratedAtUtc": now.isoformat().replace("+00:00", "Z"),
        "Classification": "CATEGORY_READONLY_INSPECTION_INCOMPLETE",
        "ReadinessClassification": "READINESS_NOT_CERTIFIED",
        "Certification": "NOT_CERTIFIED",
        "Authorization": {"ApprovalId": approval_id, "ManifestSha256": digest,
                          "PreflightClassification": preflight_report["Classification"]},
        "Targets": {}, "Errors": [],
        "Sanitization": {"RawGrantTextPersisted": False, "RawSqlOutputPersisted": False,
                         "CredentialsPersisted": False, "PageBodiesPersisted": False,
                         "SensitiveRowValuesPersisted": False},
        "ResourceOperationCounts": {**attempts, "DatabaseMutations": 0, "GrantChanges": 0,
                                     "Migrations": 0, "FixtureWrites": 0, "Backup": 0,
                                     "Restore": 0, "Cleanup": 0, "CredentialValueReadsByInspector": 0,
                                     "CredentialWrites": 0},
        "ProductionMigration": "NO_GO", "RepositoryActivation": "NO_GO",
    }
    try:
        if actual_host != (authorized["HostName"], authorized["UnixUser"], authorized["Uid"]):
            raise InspectionError("EXECUTION_HOST_IDENTITY_MISMATCH")
        podman_info = _stdout(counted_run([PODMAN, "info", "--format", "{{.Host.Security.Rootless}}"]), "PODMAN_ROOTLESS_CHECK_FAILED")
        if podman_info.casefold() != "true":
            raise InspectionError("PODMAN_NOT_ROOTLESS")
        # Verify both targets completely before allowing any database connection.
        _inspect_target_host(manifest["Source"], counted_run)
        _inspect_target_host(manifest["Destination"], counted_run)
    except InspectionError as error:
        base_report["Errors"] = [str(error)]
        base_report["ResourceOperationCounts"].update(attempts)
        return base_report
    report = base_report
    # Count the fixed SQL invocations actually made, not an assumed full set.
    for role in ("Source", "Destination"):
        try:
            report["Targets"][role] = _target_inspection(manifest[role], role, counted_run, authorized["Uid"])
        except InspectionError as error:
            report["Targets"][role] = {"Classification": "INSPECTION_FAILED", "Codes": [str(error)]}
    report["ResourceOperationCounts"].update(attempts)
    report["ReadinessClassification"] = "READINESS_NOT_CERTIFIED"
    source = report["Targets"].get("Source", {})
    destination = report["Targets"].get("Destination", {})
    report["ReadinessFindings"] = {
        "SourceSchema": source.get("Schema", {}).get("Classification", "UNKNOWN"),
        "SourceGrantScope": source.get("GrantScope", {}).get("Classification", "UNKNOWN"),
        "SourceFixtureSufficiency": source.get("FixtureSufficiency", {}).get("Classification", "UNKNOWN"),
        "DestinationSchema": destination.get("Schema", {}).get("Classification", "UNKNOWN"),
        "DestinationEmptiness": destination.get("DestinationEmptiness", {}).get("Classification", "UNKNOWN"),
        "Certification": "NOT_CERTIFIED",
    }
    report["Classification"] = ("CATEGORY_READONLY_INSPECTION_INCOMPLETE"
                                 if any(target.get("Classification") != "INSPECTION_COMPLETED" for target in report["Targets"].values())
                                 else "CATEGORY_READONLY_INSPECTION_COMPLETED")
    return report


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--preflight-report", required=True)
    parser.add_argument("--execute-readonly-inspection", action="store_true")
    parser.add_argument("--approval-id", required=True)
    args = parser.parse_args(argv)
    if not args.execute_readonly_inspection:
        print("Inspection not executed: pass --execute-readonly-inspection only after genuine approval and successful fresh preflight.", file=sys.stderr)
        return 2
    try:
        manifest = PREFLIGHT._read_private_json(Path(args.manifest), "MANIFEST_JSON_INVALID")
        preflight_report = PREFLIGHT._read_private_json(Path(args.preflight_report), "PREFLIGHT_REPORT_INVALID")
        _validate_report_target(Path(manifest["ReportPath"]), os.getuid())
        result = run_inspection(manifest, preflight_report, approval_id=args.approval_id)
        _write_once(Path(manifest["ReportPath"]), result)
    except (OSError, KeyError, TypeError, InspectionError, PREFLIGHT.InspectionPreflightError) as error:
        code = str(error) if isinstance(error, (InspectionError, PREFLIGHT.InspectionPreflightError)) else "INSPECTION_STOPPED"
        print(f"CW-M5-026B read-only inspection stopped: {code}", file=sys.stderr)
        return 3
    print(f"CW-M5-026B read-only inspection: classification={result['Classification']}; database_mutations=0; report={manifest['ReportPath']}")
    return 0 if result["Classification"] == "CATEGORY_READONLY_INSPECTION_COMPLETED" else 4


if __name__ == "__main__":
    raise SystemExit(main())
