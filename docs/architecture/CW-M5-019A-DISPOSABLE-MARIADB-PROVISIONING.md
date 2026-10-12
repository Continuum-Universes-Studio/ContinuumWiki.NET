# CW-M5-019A — Disposable MariaDB 11.8 Provisioning

## Status

DISPOSABLE_PROVISIONING_WORKFLOW_IMPLEMENTED_NOT_EXECUTED

This package adds an operator-run, fail-closed Podman workflow for a fresh
MariaDB 11.8.x target. It has not provisioned a container, created a database,
run migrations, run certification tests, or authorized category-provider
activation.

The workflow is separate from the nonconnecting
scripts/preflight-m5-019.sh command. Provisioning creates target evidence; it
does not create or approve secure/evidence/cw-m5-019-authorization.json.

## Controls

- Podman is required; Docker is not accepted.
- The default is plan-only. --provision is an explicit operator action.
- Container, volume, and database names are generated under the
  continuumwiki-m5-019-* and cw_m5_019_* scopes.
- Existing names and occupied ports are rejected; resources are never reused
  or overwritten.
- The database is published only on 127.0.0.1 and uses an explicit
  docker.io/library/mariadb:11.8 reference by default. The resolved image
  digest and actual server version are recorded.
- Root is used through the container's private Unix socket for setup. The
  dedicated continuumwiki_m5_019_app identity receives only the exact
  target-schema DML/DDL/index/reference privilege set and no non-USAGE global
  privilege or grant option; verification uses MariaDB information-schema
  privilege metadata rather than brittle SHOW GRANTS text matching.
- Generated credentials are kept outside the repository in a 0700 directory
  and separate 0600 root and application environment files. Reports contain
  only their locations, never secret values or connection strings.
- The image is initialized with `MARIADB_ROOT_HOST=localhost`; provisioning
  fails closed if any remote root host entry is observed.
- Provisioning verifies a clean schema and performs no V1/V2 migration.
- A failed run records PROVISIONING_FAILED evidence and retains any created
  resource for explicit inspection or cleanup; it never removes resources from
  an exit trap.
- Cleanup requires a valid operator authorization manifest, exact metadata
  identity, explicit PermittedOperations.ContainerVolumeDeletion=true, and
  --execute. Schema deletion and database cleanup permissions are separate and
  are not implied by container/volume cleanup.

## Commands

Plan without contacting Podman:

~~~bash
scripts/cw-m5-019/provision-mariadb-m5-019.sh \
  --suffix operator1 --port 13318
~~~

Provision a fresh target only after reviewing that plan:

~~~bash
scripts/cw-m5-019/provision-mariadb-m5-019.sh \
  --provision \
  --suffix operator1 \
  --port 13318 \
  --metadata "$PWD/secure/evidence/cw-m5-019a-provisioning.json"
~~~

The command prints no password. It writes non-secret metadata and retains the
owner-only credential file at the path recorded in that metadata.

Inspect the exact provisioned target without mutation:

~~~bash
scripts/cw-m5-019/inspect-mariadb-m5-019.sh \
  --metadata "$PWD/secure/evidence/cw-m5-019a-provisioning.json" \
  --report "$PWD/secure/evidence/cw-m5-019a-inspection.json"
~~~

The inspection command checks container and volume labels, loopback binding,
resolved image identity, MariaDB 11.8.x, the dedicated identity, and an empty
schema. It does not apply migrations or grants.

After a separately authorized manifest is supplied, run the existing
nonconnecting preflight:

~~~bash
scripts/preflight-m5-019.sh \
  --manifest "$PWD/secure/evidence/cw-m5-019-authorization.json" \
  --report "$PWD/secure/evidence/cw-m5-019-preflight.json"
~~~

Cleanup is planned first and destructive only with the explicit execute flag:

~~~bash
scripts/cw-m5-019/cleanup-mariadb-m5-019.sh \
  --metadata "$PWD/secure/evidence/cw-m5-019a-provisioning.json" \
  --manifest "$PWD/secure/evidence/cw-m5-019-authorization.json" \
  --attestation "$PWD/secure/evidence/cw-m5-019a-cleanup.json"

scripts/cw-m5-019/cleanup-mariadb-m5-019.sh \
  --execute \
  --metadata "$PWD/secure/evidence/cw-m5-019a-provisioning.json" \
  --manifest "$PWD/secure/evidence/cw-m5-019-authorization.json" \
  --attestation "$PWD/secure/evidence/cw-m5-019a-cleanup.json"
~~~

No M5-019 certification runner is added here. The existing category and
recovery authorization tests remain the separately gated live phase described
in CW-M5-018-CATEGORY-RECOVERY-MARIADB.md; provisioning stops before that
phase.

## Authorization handoff

The operator must use the generated target identity and endpoint as evidence
when preparing the existing authorization manifest. Approval identity,
timestamps, cleanup ownership, isolation proof, and permitted mutation scope
must be supplied independently. This package never synthesizes those values,
never writes the authorization manifest, and never turns a target into a
certification target automatically.
