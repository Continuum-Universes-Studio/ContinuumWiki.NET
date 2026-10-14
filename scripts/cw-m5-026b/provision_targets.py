#!/usr/bin/env python3
"""Explicitly gated, two-target CW-M5-026B disposable provisioning.

Default mode is plan-only.  Live mode is intentionally separate from the
M5-026B rehearsal authorization and never performs migration, backup, restore,
certification, or cleanup.
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import os
import secrets
import socket
import stat
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parents[2]
PREFLIGHT_PATH = ROOT / "scripts" / "preflight-m5-026b-provisioning.py"
SPEC = importlib.util.spec_from_file_location("cw_m5_026b_provisioning_preflight", PREFLIGHT_PATH)
assert SPEC and SPEC.loader
PREFLIGHT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PREFLIGHT)


def die(message: str) -> "NoReturn":
    raise RuntimeError(message)


class RuntimeIdentityVerificationError(RuntimeError):
    """Fail-closed runtime verification error with sanitized diagnostics."""

    def __init__(self, diagnostic: dict[str, Any]):
        self.diagnostic = diagnostic
        codes = ",".join(diagnostic.get("Codes", ["RUNTIME_IDENTITY_VERIFICATION_FAILED"]))
        super().__init__(f"runtime identity/version mismatch: {codes}")


def run(command: list[str], *, input_text: str | None = None) -> str:
    result = subprocess.run(command, input=input_text, text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False)
    if result.returncode != 0:
        raise RuntimeError(f"command failed: {command[0]} (exit {result.returncode})")
    return result.stdout.strip()


def parse_runtime_identity_output(stdout: str) -> dict[str, Any]:
    """Parse exactly one tabular MariaDB identity row without repairing it."""

    codes: list[str] = []
    if not isinstance(stdout, str):
        return {"Fields": None, "Codes": ["OUTPUT_FORMAT_INVALID"]}

    body = stdout[:-1] if stdout.endswith("\n") else stdout
    if "\n" in body or "\r" in body:
        codes.extend(("OUTPUT_FORMAT_INVALID", "ROW_SHAPE_INVALID"))
    if not body:
        codes.extend(("FIELD_EMPTY", "ROW_SHAPE_INVALID"))

    fields = body.split("\t") if body else []
    if len(fields) != 3:
        codes.append("ROW_SHAPE_INVALID")
        fields = None
    elif not codes:
        for field in fields:
            if field == "":
                codes.append("FIELD_EMPTY")
            elif field == "NULL":
                codes.append("FIELD_NULL")
            if field != field.strip():
                codes.append("WHITESPACE_MISMATCH")

    return {"Fields": fields, "Codes": list(dict.fromkeys(codes))}


def evaluate_runtime_identity(fields: list[str] | None, expected_database: str, initial_codes: list[str] | None = None) -> dict[str, Any]:
    """Evaluate the existing strict identity predicates deterministically."""

    codes = list(initial_codes or [])
    checks = {
        "DatabaseName": "NOT_EVALUATED",
        "VersionFamily": "NOT_EVALUATED",
        "VersionComment": "NOT_EVALUATED",
    }
    if fields is None or len(fields) != 3:
        codes.append("ROW_SHAPE_INVALID")
        return {"Accepted": False, "Codes": list(dict.fromkeys(codes)), "Checks": checks, "Fields": None}

    database, version, comment = fields
    if database not in ("", "NULL"):
        checks["DatabaseName"] = "MATCH" if database == expected_database else "MISMATCH"
        if database != expected_database:
            codes.append("DATABASE_NAME_MISMATCH")
    if version not in ("", "NULL"):
        checks["VersionFamily"] = "MATCH" if version.startswith("11.8.") else "MISMATCH"
        if not version.startswith("11.8."):
            codes.append("VERSION_FAMILY_MISMATCH")
    if comment not in ("", "NULL"):
        checks["VersionComment"] = "MATCH" if "mariadb" in comment.casefold() else "MISMATCH"
        if "mariadb" not in comment.casefold():
            codes.append("VERSION_COMMENT_MISMATCH")

    return {
        "Accepted": not codes,
        "Codes": list(dict.fromkeys(codes)),
        "Checks": checks,
        "Fields": fields,
    }


def _field_shape(value: str | None, *, expected_database: str | None = None) -> dict[str, Any]:
    if value is None:
        return {"Present": False}
    return {
        "Present": True,
        "Length": len(value),
        "IsNullLiteral": value == "NULL",
        "HasLeadingOrTrailingWhitespace": value != value.strip(),
        **({"MatchesExpectedDatabase": value == expected_database} if expected_database is not None else {}),
    }


def sanitize_runtime_identity_diagnostic(evaluation: dict[str, Any], *, exit_code: int | None = None) -> dict[str, Any]:
    """Return bounded diagnostics without returning field values or stderr."""

    fields = evaluation.get("Fields")
    database = version = comment = None
    if isinstance(fields, list) and len(fields) == 3:
        database, version, comment = fields
    diagnostic: dict[str, Any] = {
        "Accepted": bool(evaluation.get("Accepted")),
        "Codes": list(evaluation.get("Codes", [])),
        "Checks": dict(evaluation.get("Checks", {})),
        "FieldShape": {
            "Database": _field_shape(database),
            "Version": _field_shape(version),
            "VersionComment": _field_shape(comment),
        },
    }
    if version is not None:
        diagnostic["FieldShape"]["Version"]["Family"] = "11.8" if version.startswith("11.8.") else "other"
    if comment is not None:
        diagnostic["FieldShape"]["VersionComment"]["ContainsCaseSensitiveMariaDB"] = "MariaDB" in comment
        diagnostic["FieldShape"]["VersionComment"]["ContainsCaseInsensitiveMariaDB"] = "mariadb" in comment.casefold()
    if exit_code is not None:
        diagnostic["ExitCode"] = exit_code
    return diagnostic


def classify_runtime_identity_query(return_code: int, stdout: str, expected_database: str) -> dict[str, Any]:
    """Separate command failure, output shape, and strict predicate failure."""

    if return_code != 0:
        return {
            "Accepted": False,
            "Codes": ["RUNTIME_QUERY_COMMAND_FAILED"],
            "Checks": {
                "DatabaseName": "NOT_EVALUATED",
                "VersionFamily": "NOT_EVALUATED",
                "VersionComment": "NOT_EVALUATED",
            },
            "ExitCode": return_code,
            "Fields": None,
        }
    parsed = parse_runtime_identity_output(stdout)
    return evaluate_runtime_identity(parsed["Fields"], expected_database, parsed["Codes"])


def resource_exists(command: list[str]) -> bool:
    result = subprocess.run(command, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
    return result.returncode == 0


def port_available(endpoint: str) -> bool:
    port = int(endpoint.rsplit(":", 1)[1])
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try:
            sock.bind(("127.0.0.1", port))
        except OSError:
            return False
    return True


def safe_directory(path: str, *, must_be_new: bool) -> Path:
    candidate = Path(path).expanduser().resolve()
    if candidate == ROOT or ROOT in candidate.parents:
        die("credential directory must be outside the repository")
    if any(part.lower() in {"password", "secret", "token", "credentialmaterial"} for part in candidate.parts):
        die("credential directory contains a secret-bearing path component")
    if must_be_new and candidate.exists():
        die(f"refusing to overwrite credential directory: {candidate}")
    return candidate


def write_json(path: Path, payload: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(f".{path.name}.tmp.{os.getpid()}")
    temporary.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    os.chmod(temporary, stat.S_IRUSR | stat.S_IWUSR)
    os.replace(temporary, path)


def load_manifest(path: Path, preflight_report: Path) -> dict[str, Any]:
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        die(f"cannot read provisioning manifest: {error}")
    errors = PREFLIGHT.validate_manifest(document)
    report = PREFLIGHT.build_report(document, errors, str(path))
    write_json(preflight_report, report)
    if errors:
        die("provisioning preflight rejected the manifest; no target operation was attempted")
    return document


def target_evidence(target: dict[str, Any], credential_directory: Path, container_id: str = "", image_digest: str = "", version: str = "", comment: str = "", app_scoped: bool = False, provisioning_run_id: str | None = None) -> dict[str, Any]:
    evidence = {
        "ContainerName": target["ContainerName"],
        "ContainerId": container_id or None,
        "VolumeName": target["VolumeName"],
        "DatabaseName": target["DatabaseName"],
        "Endpoint": target["Endpoint"],
        "InstanceIdentity": target["IdentityPrefix"],
        "ImageDigest": image_digest or None,
        "EngineVersionObserved": version or None,
        "VersionComment": comment or None,
        "DatabaseIdentity": target["DatabaseIdentity"],
        "CredentialDirectory": str(credential_directory),
        "ApplicationIdentitySchemaScoped": app_scoped,
    }
    if provisioning_run_id:
        evidence["ProvisioningRunId"] = provisioning_run_id
    return evidence


def metadata(document: dict[str, Any], status: str, run_id: str, targets: dict[str, Any], source_credentials: Path, destination_credentials: Path, failure: dict[str, Any] | None = None) -> dict[str, Any]:
    report = {
        "ReportVersion": "cw-m5-026b-disposable-target-provisioning-v1",
        "GeneratedAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "Milestone": "CW-M5-026B",
        "Classification": status,
        "Provisioning": {
            "Executed": status != "PLAN_ONLY",
            "Outcome": status,
            "RunId": run_id,
            "RootlessPodman": True,
            "PinnedImageReference": document["Image"]["Reference"],
            "MigrationsExecuted": False,
            "BackupExecuted": False,
            "RestoreExecuted": False,
            "CertificationExecuted": False,
            "AutomaticCleanup": False,
        },
        "Targets": targets,
        "CredentialPaths": {"Source": str(source_credentials), "Destination": str(destination_credentials), "ValuesIncluded": False},
        "CrossTargetIsolation": {
            "DistinctContainers": targets["Source"]["ContainerName"] != targets["Destination"]["ContainerName"],
            "DistinctVolumes": targets["Source"]["VolumeName"] != targets["Destination"]["VolumeName"],
            "DistinctDatabases": targets["Source"]["DatabaseName"] != targets["Destination"]["DatabaseName"],
            "DistinctEndpoints": targets["Source"]["Endpoint"] != targets["Destination"]["Endpoint"],
            "DistinctCredentialDirectories": source_credentials != destination_credentials,
            "LoopbackOnly": True,
            "ProductionContacted": False,
            "PreviousTargetReused": False,
        },
        "DatabaseContacts": {
            "ConnectionCount": "not-instrumented",
            "MutationCount": "not-instrumented",
            "HealthChecksPerformed": status == "DISPOSABLE_TARGETS_PROVISIONED_AUTHORIZATION_BLOCKED",
            "MigrationConnections": 0,
            "BackupConnections": 0,
            "RestoreConnections": 0,
            "CertificationConnections": 0,
        },
        "Authorization": {
            "ProvisioningManifestValidated": True,
            "RehearsalAuthorizationCreated": False,
            "RehearsalAuthorized": False,
            "CleanupAuthorized": False,
        },
        "Cleanup": {"Performed": False, "SeparateAuthorizationRequired": True},
    }
    if failure is not None:
        report["Failure"] = failure
    return report


def provision_one(target: dict[str, Any], credential_directory: Path, image: str, run_id: str) -> dict[str, Any]:
    credential_directory.mkdir(parents=True, mode=0o700)
    os.chmod(credential_directory, 0o700)
    root_password = secrets.token_hex(32)
    app_password = secrets.token_hex(32)
    environment = credential_directory / "environment.env"
    application = credential_directory / "application.env"
    environment.write_text(
        f"MARIADB_ROOT_PASSWORD={root_password}\nMYSQL_PWD={root_password}\n"
        f"MARIADB_DATABASE={target['DatabaseName']}\nMARIADB_ROOT_HOST=localhost\n",
        encoding="utf-8",
    )
    application.write_text(
        f"CW_M5_APP_USER={target['DatabaseIdentity']}\nCW_M5_APP_PASSWORD={app_password}\n",
        encoding="utf-8",
    )
    os.chmod(environment, 0o600)
    os.chmod(application, 0o600)
    run(["podman", "pull", "--quiet", image])
    digest = run(["podman", "image", "inspect", "--format", "{{range .RepoDigests}}{{println .}}{{end}}", image]).splitlines()[0]
    if digest != image:
        die("resolved image digest does not match the authorized immutable reference")
    role = target["DatabaseIdentity"].rsplit("_", 2)[1]
    run(["podman", "volume", "create", "--label", f"io.continuumwiki.m5-026b-run-id={run_id}", "--label", f"io.continuumwiki.m5-026b-role={role}", target["VolumeName"]])
    # Podman volume create is idempotent for an existing name. Confirm this run's
    # unique ownership labels before ever attaching the volume to a new container.
    volume_raw = run(["podman", "volume", "inspect", "--format", "{{json .}}", target["VolumeName"]])
    try:
        volume_info = json.loads(volume_raw)
        if isinstance(volume_info, list) and len(volume_info) == 1:
            volume_info = volume_info[0]
    except json.JSONDecodeError:
        die("new disposable volume inspection was malformed; refusing to attach it")
    labels = volume_info.get("Labels", {}) if isinstance(volume_info, dict) else {}
    if (not isinstance(volume_info, dict) or volume_info.get("Name") != target["VolumeName"]
            or labels.get("io.continuumwiki.m5-026b-run-id") != run_id
            or labels.get("io.continuumwiki.m5-026b-role") != role):
        die("disposable volume is not owned by this provisioning run; refusing to attach it")
    run([
        "podman", "run", "--detach", "--name", target["ContainerName"],
        "--label", f"io.continuumwiki.m5-026b-run-id={run_id}",
        "--label", f"io.continuumwiki.m5-026b-database={target['DatabaseName']}",
        "--publish", f"{target['Endpoint']}:3306", "--env-file", str(environment),
        "--volume", f"{target['VolumeName']}:/var/lib/mysql", image,
        "--character-set-server=utf8mb4", "--collation-server=utf8mb4_unicode_ci",
    ])
    container_id = run(["podman", "container", "inspect", "--format", "{{.Id}}", target["ContainerName"]])
    ready = False
    for _ in range(120):
        result = subprocess.run(["podman", "exec", "--env-file", str(environment), target["ContainerName"], "healthcheck.sh", "--connect", "--innodb_initialized"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
        if result.returncode == 0:
            ready = True
            break
        time.sleep(1)
    if not ready:
        die(f"{target['ContainerName']} did not become healthy; resources retained")
    published = run(["podman", "port", target["ContainerName"], "3306/tcp"])
    if f"127.0.0.1:{target['Endpoint'].rsplit(':', 1)[1]}" not in published:
        die(f"{target['ContainerName']} is not loopback-bound")
    runtime_result = subprocess.run(
        ["podman", "exec", "--env-file", str(environment), target["ContainerName"], "mariadb", "--protocol=socket", "-uroot", "-D", target["DatabaseName"], "-N", "-B", "-e", "SELECT DATABASE(), VERSION(), @@version_comment;"],
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )
    evaluation = classify_runtime_identity_query(runtime_result.returncode, runtime_result.stdout, target["DatabaseName"])
    if not evaluation["Accepted"]:
        raise RuntimeIdentityVerificationError(sanitize_runtime_identity_diagnostic(evaluation, exit_code=runtime_result.returncode if runtime_result.returncode != 0 else None))
    database, version, comment = evaluation["Fields"]
    bad_root_hosts = run([
        "podman", "exec", "--env-file", str(environment), target["ContainerName"], "mariadb",
        "--protocol=socket", "-uroot", "-N", "-B", "-e",
        "SELECT GROUP_CONCAT(Host ORDER BY Host SEPARATOR ',') FROM mysql.user WHERE User='root' AND Host NOT IN ('localhost','127.0.0.1','::1');",
    ])
    if bad_root_hosts not in ("", "NULL"):
        die(f"{target['ContainerName']} has an unexpected remote root host")
    sql = (
        f"CREATE USER IF NOT EXISTS '{target['DatabaseIdentity']}'@'%' IDENTIFIED BY '{app_password}';\n"
        f"ALTER USER '{target['DatabaseIdentity']}'@'%' IDENTIFIED BY '{app_password}';\n"
        f"REVOKE ALL PRIVILEGES, GRANT OPTION FROM '{target['DatabaseIdentity']}'@'%';\n"
        f"GRANT SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER, INDEX, REFERENCES ON `{target['DatabaseName']}`.* TO '{target['DatabaseIdentity']}'@'%';\nFLUSH PRIVILEGES;\n"
    )
    run(["podman", "exec", "--env-file", str(environment), "-i", target["ContainerName"], "mariadb", "--protocol=socket", "-uroot"], input_text=sql)
    grantee = f"CONCAT(CHAR(39),'{target['DatabaseIdentity']}',CHAR(39),'@',CHAR(39),'%',CHAR(39))"
    schema_names = run([
        "podman", "exec", "--env-file", str(environment), target["ContainerName"], "mariadb",
        "--protocol=socket", "-uroot", "-N", "-B", "-e",
        f"SELECT GROUP_CONCAT(DISTINCT TABLE_SCHEMA ORDER BY TABLE_SCHEMA SEPARATOR ',') FROM information_schema.SCHEMA_PRIVILEGES WHERE GRANTEE={grantee};",
    ])
    if schema_names != target["DatabaseName"]:
        die(f"{target['ContainerName']} application identity is not schema-scoped")
    global_privileges = run([
        "podman", "exec", "--env-file", str(environment), target["ContainerName"], "mariadb",
        "--protocol=socket", "-uroot", "-N", "-B", "-e",
        f"SELECT GROUP_CONCAT(DISTINCT PRIVILEGE_TYPE ORDER BY PRIVILEGE_TYPE SEPARATOR ',') FROM information_schema.USER_PRIVILEGES WHERE GRANTEE={grantee} AND PRIVILEGE_TYPE <> 'USAGE';",
    ])
    if global_privileges not in ("", "NULL"):
        die(f"{target['ContainerName']} application identity has global privileges")
    schema_privileges = run([
        "podman", "exec", "--env-file", str(environment), target["ContainerName"], "mariadb",
        "--protocol=socket", "-uroot", "-N", "-B", "-e",
        f"SELECT GROUP_CONCAT(DISTINCT PRIVILEGE_TYPE ORDER BY PRIVILEGE_TYPE SEPARATOR ',') FROM information_schema.SCHEMA_PRIVILEGES WHERE GRANTEE={grantee} AND TABLE_SCHEMA='{target['DatabaseName']}';",
    ])
    if schema_privileges != "ALTER,CREATE,DELETE,INDEX,INSERT,REFERENCES,SELECT,UPDATE":
        die(f"{target['ContainerName']} application identity has an unexpected schema privilege set")
    grants = run([
        "podman", "exec", "--env-file", str(environment), target["ContainerName"], "mariadb",
        "--protocol=socket", "-uroot", "-N", "-B", "-e",
        f"SHOW GRANTS FOR '{target['DatabaseIdentity']}'@'%';",
    ])
    if "WITH GRANT OPTION" in grants:
        die(f"{target['ContainerName']} application identity has grant option")
    table_count = run(["podman", "exec", "--env-file", str(environment), target["ContainerName"], "mariadb", "--protocol=socket", "-uroot", "-N", "-B", "-e", f"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema='{target['DatabaseName']}';"])
    if table_count != "0":
        die(f"{target['ContainerName']} was not empty after provisioning")
    return target_evidence(target, credential_directory, container_id, digest, version, comment, True, run_id)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--metadata", required=True)
    parser.add_argument("--preflight-report")
    parser.add_argument("--provision", action="store_true")
    parser.add_argument("--confirm-provisioning", action="store_true")
    args = parser.parse_args(argv)
    metadata_path = Path(args.metadata).expanduser()
    preflight_report = Path(args.preflight_report or metadata_path.with_name("cw-m5-026b-provisioning-preflight.json"))
    document: dict[str, Any] | None = None
    source_credentials: Path | None = None
    destination_credentials: Path | None = None
    provisioned: dict[str, Any] | None = None
    run_id = "not-executed"
    try:
        document = load_manifest(Path(args.manifest), preflight_report)
        source = document["TargetPlan"]["Source"]
        destination = document["TargetPlan"]["Destination"]
        source_credentials = safe_directory(source["CredentialDirectory"], must_be_new=args.provision)
        destination_credentials = safe_directory(destination["CredentialDirectory"], must_be_new=args.provision)
        if source_credentials == destination_credentials:
            die("source and destination credential directories must differ")
        provisioned = {
            "Source": target_evidence(source, source_credentials),
            "Destination": target_evidence(destination, destination_credentials),
        }
        if not args.provision:
            write_json(metadata_path, metadata(document, "PLAN_ONLY", "not-executed", provisioned, source_credentials, destination_credentials))
            print("CW-M5-026B provisioning plan-only: no Podman, database, credentials, migrations, backup, restore, certification, or cleanup will run.")
            print(f"Metadata: {metadata_path}")
            return 0
        if not args.confirm_provisioning:
            die("--provision requires --confirm-provisioning")
        rootless = run(["podman", "info", "--format", "{{.Host.Security.Rootless}}"])
        if rootless != "true":
            die("Podman must be rootless")
        for target in (source, destination):
            if not port_available(target["Endpoint"]):
                die(f"loopback endpoint is unavailable: {target['Endpoint']}")
            if resource_exists(["podman", "container", "exists", target["ContainerName"]]):
                die(f"refusing existing container: {target['ContainerName']}")
            if resource_exists(["podman", "volume", "exists", target["VolumeName"]]):
                die(f"refusing existing volume: {target['VolumeName']}")
        run_id = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + f"-{os.getpid()}"
        provisioned["Source"] = provision_one(source, source_credentials, document["Image"]["Reference"], run_id)
        provisioned["Destination"] = provision_one(destination, destination_credentials, document["Image"]["Reference"], run_id)
        write_json(metadata_path, metadata(document, "DISPOSABLE_TARGETS_PROVISIONED_AUTHORIZATION_BLOCKED", run_id, provisioned, source_credentials, destination_credentials))
        print("CW-M5-026B provisioned two fresh rootless disposable targets; rehearsal authorization, migrations, backup, restore, certification, and cleanup remain disabled.")
        print(f"Metadata: {metadata_path}")
        return 0
    except (OSError, RuntimeError, ValueError, KeyError) as error:
        if document is not None and args.provision and provisioned is not None and source_credentials is not None and destination_credentials is not None:
            try:
                failure = error.diagnostic if isinstance(error, RuntimeIdentityVerificationError) else None
                write_json(metadata_path, metadata(document, "PROVISIONING_FAILED_RESOURCES_RETAINED", run_id, provisioned, source_credentials, destination_credentials, failure))
            except OSError:
                pass
        print(f"CW-M5-026B provisioning: {error}", file=sys.stderr)
        return 64


if __name__ == "__main__":
    sys.exit(main())
