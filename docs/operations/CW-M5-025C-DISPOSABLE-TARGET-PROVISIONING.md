# CW-M5-025C — Disposable MariaDB Target Provisioning

## Status

`CATEGORY_ADMIN_GRANT_TARGET_PROVISIONED_AUTHORIZATION_REQUIRED`

The provisioning workflow is implemented and source-tested, and the operator
has now created one fresh live target. Provisioning-only health, identity,
privilege, and empty-schema checks succeeded. No V1/V2/V3 migration,
certification test, or cleanup operation was run.
Production migration and repository activation remain `NO_GO`.

## Workflow

The M5-025-specific workflow is:

```text
scripts/cw-m5-025/common.sh
scripts/cw-m5-025/provision-mariadb-m5-025.sh
```

It is separate from CW-M5-019A and has these gates:

- default invocation is plan-only and does not require or invoke Podman;
- live mode requires both `--provision` and
  `--confirm-disposable-target`;
- live mode requires rootless Podman and refuses a rootful engine;
- suffix `operator4` is rejected explicitly;
- generated names are scoped to `continuumwiki-m5-025-*`,
  `cw_m5_025_*`, and `continuumwiki-m5-025-*-data`;
- an explicitly supplied port is checked, otherwise a free loopback port is
  selected from 13325–13424 and checked again immediately before creation;
- the published binding must be `127.0.0.1:<port>`;
- the image must be MariaDB 11.8 or an immutable MariaDB 11.8 digest;
- credentials are generated outside the repository in a 0700 directory with
  0600 files and are never written to metadata or reports;
- the application identity is exactly `continuumwiki_m5_025_app`, has only
  the target-schema DDL/data privileges needed for the later certification,
  and has no global privileges or grant option;
- the new database must have zero tables before certification; and
- failure retains resources for separately authorized inspection/cleanup. No
  automatic cleanup is performed.

The workflow never applies V1, V2, or V3, never runs certification tests, and
never creates an authorization manifest.

## Source-only plan command

This command was used as the safe source test shape; it creates no resources:

```bash
scripts/cw-m5-025/provision-mariadb-m5-025.sh \
  --suffix source-test \
  --port 13327 \
  --metadata /tmp/cw-m5-025-plan.json \
  --secrets-dir /tmp/cw-m5-025-secrets-test
```

For a separately authorized operator-created target, the future command shape
is:

```bash
scripts/cw-m5-025/provision-mariadb-m5-025.sh \
  --provision \
  --confirm-disposable-target \
  --metadata secure/evidence/cw-m5-025c-provisioning.json
```

That command is not an authorization manifest and does not authorize V3 or
certification. It must not be run until the operator separately approves the
creation of a new disposable target. A unique suffix and available port are
generated when omitted.

## Metadata-to-authorization mapping

After an authorized provisioning run, the nonsecret metadata will provide the
exact values needed for the M5-025 authorization target:

| M5-025 manifest field | Provisioning metadata source |
| --- | --- |
| `Target.Disposable` | `Target.Disposable` |
| `Target.ContainerName` | `Target.ContainerName` |
| `Target.VolumeName` | `Target.VolumeName` |
| `Target.DatabaseName` | `Target.DatabaseName` |
| `Target.Endpoint` | `Target.Endpoint` |
| `Target.InstanceIdentity` | `Target.InstanceIdentity` |
| `Target.EngineVersion` | fixed contract value `MariaDB 11.8.x`; actual value in `EngineVersionObserved` |
| `Target.DatabaseIdentity` | fixed value `continuumwiki_m5_025_app` |

The metadata also records the resolved image digest, rootless Podman status,
loopback-only binding, schema-scoped identity evidence, credential paths and
modes without credential values, zero migration/certification activity, and
the requirement for separate cleanup authorization.

## Remaining authorization requirements

Provisioning still requires separate operator approval. Certification then
requires a distinct genuine `cw-m5-025-authorization-v1` manifest with:

- real approver, operator, target-owner, approval ID, and UTC validity window;
- the exact newly provisioned container, volume, database, endpoint, instance
  identity, MariaDB 11.8.x identity, and `continuumwiki_m5_025_app` identity;
- all required V1/V2/V3, grant mutation, concurrency, restart-simulation,
  acknowledgment-loss, and sanitized-evidence permissions set to `true`; and
- `DatabaseCleanup`, `SchemaDeletion`, and `ContainerVolumeDeletion` set
  explicitly to `false`.

That manifest must pass the nonconnecting M5-025 preflight before any
certification connection is opened. The existing M5-019 operator4 target and
approval remain unusable for this purpose.

## Current provisioned target

The authoritative nonsecret provisioning record is
`secure/evidence/cw-m5-025c-provisioning.json`. It records:

- container: `continuumwiki-m5-025-20261012050509-c4a0edf8`;
- volume: `continuumwiki-m5-025-20261012050509-c4a0edf8-data`;
- database: `cw_m5_025_20261012050509_c4a0edf8`;
- endpoint: `127.0.0.1:13325`;
- database identity: `continuumwiki_m5_025_app`;
- observed engine: `11.8.9-MariaDB-ubu2404`; and
- resolved image digest:
  `docker.io/library/mariadb@sha256:6422478cb8e159f080fb1d8ccf65101e26fe51385787fde7d16c3b165a331f15`.

The target remains `DISPOSABLE_TARGET_PROVISIONED_NOT_AUTHORIZED` for M5-025
certification. The credentials remain only at the owner-only paths recorded in
the metadata; their values are not copied into repository artifacts.
