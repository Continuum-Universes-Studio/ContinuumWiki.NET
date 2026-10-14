# CW-M5-026B — Credential Preparation and Readiness Inspection Approval Packet

**Status: NON-AUTHORIZING PROPOSALS ONLY.** This document is not an approval,
authorization manifest, or live-state attestation. Its three proposed IDs are
suggestions only, not issued or checked for uniqueness. No credential values,
option files, manifests, or live reports were read or created for this packet.

## Basis and current limits

The proposals below follow the separate Draft 2020-12 contracts:

- `cw-m5-026b-inspection-credential-preparation-authorization-v1` for one
  target's application credential conversion;
- `cw-m5-026b-readonly-inspection-authorization-v1` for a two-target,
  read-only readiness inspection.

The source target bindings below come from retained provisioning and
continuation evidence. The v3 readiness report explicitly labels those
bindings historical and current container existence, health, runtime identity,
grant state, option-file existence, and destination emptiness unverified. A
future runner must re-attest the live target; any mismatch is a stop condition.
No approval from provisioning, continuation, or rehearsal is reused.

The application environment file is not a MariaDB client option file. The
source-implemented converter reads only the target's `application.env`, never
the root environment file, and creates this exact format without printing its
contents:

```ini
[client]
user=<target application identity>
password=<64 lowercase hexadecimal characters>
```

The actual option file is created with mode `0600`, only if absent. The
credential directory must be owned by UID 1000 and mode `0700`; symlinks,
unexpected ownership/modes, extra environment keys, or malformed credentials
are rejected. Conversion does not connect to MariaDB or invoke Podman. The
fixed inspection runner later invokes MariaDB with
`--defaults-extra-file=<bound option path>` followed by the fixed TCP host,
port, user, database, batch flags, and one compiled allowlisted SQL statement.
No operator-supplied command or SQL is accepted.

## Historical target bindings for all proposals

These values bind the requested approval scope only; they do not assert current
resource state.

| Binding | Source | Destination |
| --- | --- | --- |
| Container | `continuumwiki-m5-026b-source-ro1` | `continuumwiki-m5-026b-destination-ro1` |
| Container ID | `6a4d4f8707ee4f5b14dddfff183db6d26ce7453eaba377b7c9d081bc1157f914` | `1c58a2f370d36af443e53283d4212604e997188e9fbf81175655446b7c3ea1f5` |
| Volume | `continuumwiki-m5-026b-source-ro1-data` | `continuumwiki-m5-026b-destination-ro1-data` |
| Database | `cw_m5_026b_source_ro1` | `cw_m5_026b_destination_ro1` |
| Endpoint | `127.0.0.1:13326` | `127.0.0.1:13327` |
| Database identity | `continuumwiki_m5_026b_source_app` | `continuumwiki_m5_026b_destination_app` |
| Credential directory | `/home/cdjensen94/.local/state/continuumwiki/cw-m5-026b/source-ro1` | `/home/cdjensen94/.local/state/continuumwiki/cw-m5-026b/destination-ro1` |
| Environment input (path only) | `/home/cdjensen94/.local/state/continuumwiki/cw-m5-026b/source-ro1/application.env` | `/home/cdjensen94/.local/state/continuumwiki/cw-m5-026b/destination-ro1/application.env` |
| Option-file destination | `/home/cdjensen94/.local/state/continuumwiki/cw-m5-026b/source-ro1/inspection-client.cnf` | `/home/cdjensen94/.local/state/continuumwiki/cw-m5-026b/destination-ro1/inspection-client.cnf` |
| Image | `docker.io/library/mariadb@sha256:6422478cb8e159f080fb1d8ccf65101e26fe51385787fde7d16c3b165a331f15` | same pinned image |
| Historical provisioning run | `20261012T093746Z-86380` | same run |
| Historical instance identity | `milestone=CW-M5-026B;role=source;container=continuumwiki-m5-026b-source-ro1;volume=continuumwiki-m5-026b-source-ro1-data;database=cw_m5_026b_source_ro1;endpoint=127.0.0.1:13326;image=docker.io/library/mariadb@sha256:6422478cb8e159f080fb1d8ccf65101e26fe51385787fde7d16c3b165a331f15;containerId=6a4d4f8707ee4f5b14dddfff183db6d26ce7453eaba377b7c9d081bc1157f914;runId=20261012T093746Z-86380;` | `milestone=CW-M5-026B;role=destination;container=continuumwiki-m5-026b-destination-ro1;volume=continuumwiki-m5-026b-destination-ro1-data;database=cw_m5_026b_destination_ro1;endpoint=127.0.0.1:13327;image=docker.io/library/mariadb@sha256:6422478cb8e159f080fb1d8ccf65101e26fe51385787fde7d16c3b165a331f15;containerId=1c58a2f370d36af443e53283d4212604e997188e9fbf81175655446b7c3ea1f5;runId=20261012T093746Z-86380;` |

The schema pins the host to `christian-laptop`, Unix user `cdjensen94`, UID
`1000`, rootless Podman. The operator must freshly affirm/verify these values
before any applicable operation; this packet does not attest present host or
target state.

## Proposal A — source inspection credential preparation

**Suggested, unissued ID:** `CW-M5-026B-CREDPREP-SOURCE-PROPOSAL-A`

> I, [approver name], acting as [approver role], approve [operator name and
> role] to perform only `InspectCredentialFileMetadata`,
> `ReadApplicationCredentialValues`, and
> `CreateInspectionClientOptionFile` for the exact historical CW-M5-026B
> source target shown above. Target owner: [owner name]. The sole credential
> input is the bound source `application.env`; the sole output is the bound
> source `inspection-client.cnf`, created only if absent. The authorized host
> is `christian-laptop`, user `cdjensen94`, UID `1000`, rootless Podman. All
> remaining operations in the credential-preparation contract are false,
> including reading the root environment file, modifying provisioning
> credentials, database connections, Podman commands, migrations, grant
> changes, fixture writes, backup, restore, cleanup, deletion, production
> access, and credential-value logging. Approval ID:
> `CW-M5-026B-CREDPREP-SOURCE-PROPOSAL-A` [replace with a genuinely unique
> approved ID if this suggestion is already used]. ApprovedAtUtc: [actual UTC
> approval submission time]. ExpiresAtUtc: [actual UTC expiry, after approval
> and no more than 24 hours later].

Proposed contract: `cw-m5-026b-inspection-credential-preparation-authorization-v1`,
`TargetRole=Source`. Proposed private manifest path:
`/home/cdjensen94/continuumwiki-validation/authorizations/cw-m5-026b-credprep-source.json`.
Proposed create-only preflight report path:
`/home/cdjensen94/continuumwiki-validation/cw-m5-026b-credential-preparation-preflight-CW-M5-026B-CREDPREP-SOURCE-PROPOSAL-A.json`.

The exact proposed `PermittedOperations` object for A (and identically for B)
is:

```json
{
  "InspectCredentialFileMetadata": true,
  "ReadApplicationCredentialValues": true,
  "CreateInspectionClientOptionFile": true,
  "ReadRootEnvironmentFile": false,
  "ModifyProvisioningCredentialFiles": false,
  "DatabaseConnections": false,
  "PodmanCommands": false,
  "Migrations": false,
  "GrantChanges": false,
  "FixtureWrites": false,
  "Backup": false,
  "Restore": false,
  "Cleanup": false,
  "DeleteExistingCredentialFiles": false,
  "ProductionAccess": false,
  "CredentialValueLogging": false
}
```

## Proposal B — destination inspection credential preparation

**Suggested, unissued ID:** `CW-M5-026B-CREDPREP-DESTINATION-PROPOSAL-B`

> I, [approver name], acting as [approver role], approve [operator name and
> role] to perform only `InspectCredentialFileMetadata`,
> `ReadApplicationCredentialValues`, and
> `CreateInspectionClientOptionFile` for the exact historical CW-M5-026B
> destination target shown above. Target owner: [owner name]. The sole
> credential input is the bound destination `application.env`; the sole
> output is the bound destination `inspection-client.cnf`, created only if
> absent. The authorized host is `christian-laptop`, user `cdjensen94`, UID
> `1000`, rootless Podman. All remaining operations in the
> credential-preparation contract are false, including reading the root
> environment file, modifying provisioning credentials, database
> connections, Podman commands, migrations, grant changes, fixture writes,
> backup, restore, cleanup, deletion, production access, and credential-value
> logging. Approval ID:
> `CW-M5-026B-CREDPREP-DESTINATION-PROPOSAL-B` [replace with a genuinely
> unique approved ID if this suggestion is already used]. ApprovedAtUtc:
> [actual UTC approval submission time]. ExpiresAtUtc: [actual UTC expiry,
> after approval and no more than 24 hours later].

Proposed contract: `cw-m5-026b-inspection-credential-preparation-authorization-v1`,
`TargetRole=Destination`. Proposed private manifest path:
`/home/cdjensen94/continuumwiki-validation/authorizations/cw-m5-026b-credprep-destination.json`.
Proposed create-only preflight report path:
`/home/cdjensen94/continuumwiki-validation/cw-m5-026b-credential-preparation-preflight-CW-M5-026B-CREDPREP-DESTINATION-PROPOSAL-B.json`.

Each credential-preparation approval is independent, one-target-only, and
does not authorize the other conversion or any database inspection. If the
bound output option file already exists, the converter refuses to overwrite
it; stop rather than remove or replace it under these permissions.

## Proposal C — read-only inspection of both targets

**Suggested, unissued ID:** `CW-M5-026B-INSPECT-PROPOSAL-C`

> I, [approver name], acting as [approver role], approve [operator name and
> role], with [target owner], to perform one bounded read-only CW-M5-026B
> readiness inspection of both exact targets in the table above. The targets
> must be disposable, non-production, and not shared with production, freshly
> verified on `christian-laptop` by `cdjensen94` (UID `1000`) under rootless
> Podman. The only permitted operations are the exact true-valued operations
> in `cw-m5-026b-readonly-inspection-authorization-v1`: host/target isolation
> inspection; read-only connections to the named source and destination
> databases; current identity/version and grant-scope classification; bounded
> schema/index/constraint/foreign-key/ledger metadata; aggregate-only source
> fixture counts; destination emptiness counts; and retention of sanitized
> evidence at the bound create-only report path. All other contract
> operations are false, including database mutations, grant changes,
> migrations, fixture writes, backup, restore, fault injection, certification,
> cleanup, schema deletion, container/volume deletion, and production access.
> The inspection uses only the separately prepared option files at the two
> paths shown above; their existence and validity are not asserted here.
> Approval ID: `CW-M5-026B-INSPECT-PROPOSAL-C` [replace with a genuinely
> unique approved ID if this suggestion is already used]. ApprovedAtUtc:
> [actual UTC approval submission time]. ExpiresAtUtc: [actual UTC expiry,
> after approval and no more than 24 hours later].

Proposed contract: `cw-m5-026b-readonly-inspection-authorization-v1`.
Proposed private manifest path:
`/home/cdjensen94/continuumwiki-validation/authorizations/cw-m5-026b-readonly-inspection.json`.
Proposed fresh, create-only preflight report path:
`/home/cdjensen94/continuumwiki-validation/cw-m5-026b-inspection-preflight-CW-M5-026B-INSPECT-PROPOSAL-C.json`.
Proposed final sanitized, create-only report path:
`/home/cdjensen94/continuumwiki-validation/cw-m5-026b-inspection-result-CW-M5-026B-INSPECT-PROPOSAL-C.json`.

The exact proposed `PermittedOperations` object for C is:

```json
{
  "InspectHostIsolation": true,
  "ConnectSourceReadOnly": true,
  "InspectSourceMetadata": true,
  "InspectSourceGrantScope": true,
  "InspectSourceFixtureAggregates": true,
  "ConnectDestinationReadOnly": true,
  "InspectDestinationMetadata": true,
  "InspectDestinationEmptiness": true,
  "RetainSanitizedEvidence": true,
  "DatabaseMutations": false,
  "GrantChanges": false,
  "Migrations": false,
  "FixtureWrites": false,
  "Backup": false,
  "Restore": false,
  "FaultInjection": false,
  "Certification": false,
  "Cleanup": false,
  "SchemaDeletion": false,
  "ContainerVolumeDeletion": false,
  "ProductionAccess": false
}
```

The inspection contract binds both `CredentialOptionFile` paths to the
respective credential directory. The inspection preflight is nonconnecting;
the live runner separately checks the host, both container/image/volume/port
bindings, and both option-file ownership/modes before database queries. Its
report is diagnostic readiness evidence, not certification or proof of
historical fixture provenance.

## Operator decisions and evidence still required

Before any authoritative manifest can be created, the operator must:

1. Explicitly approve or replace each suggested ID, verify each is genuinely
   unused, and authorize each proposal independently. The contracts do not
   maintain a consumed-ID registry.
2. Provide actual `ApprovedAtUtc` and `ExpiresAtUtc` for each approval (UTC `Z`,
   strictly after approval, no more than 24 hours); identify approver,
   operator, target owner, and each person's role. No names, roles, times, or
   signatures have been inferred here.
3. Reaffirm target ownership and that the two exact targets remain disposable,
   non-production, and not shared with production. Historical container IDs,
   image, labels, mounts, ports, and credentials must be rechecked by the
   authorized host runner; disagreement stops the operation.
4. Confirm the option-file outputs do not already exist and that the bound
   application credential files and directories pass the converter's
   ownership/mode/shape checks. This packet did not check paths or read file
   metadata/content on the host.
5. Choose a new, absent private manifest location for each approval if any
   proposed path already exists. The parent directory must be operator-owned
   mode `0700`; manifests/reports are owner-only mode `0600` and create-only.
   An existing report or option-file path must never be overwritten.
6. Approve C separately after both preparation operations succeed. C authorizes
   only read-only inspection; it grants no repairs or follow-up actions.

Schema-required but deliberately unresolved in this packet: all approval
timestamps; approver/operator names and roles; target-owner identity; final
approval IDs; confirmation of ID uniqueness; and any changed report or
manifest paths. The contract pins host identity, target values, and exact
permission maps; it does not make these historical target facts current.

## Staged host procedure — only after genuine approvals

Use the operator's normal `christian-laptop` terminal. Do not begin until all
three decisions are genuine, the IDs are unused, manifests are complete, and
each applicable preflight succeeds. Keep manifests and reports in
owner-controlled mode-`0700` directories; manifests/reports mode `0600`.
For each completed manifest, the source-supported Draft 2020-12 validation
form is:

```bash
python3 -c 'import json,sys; from jsonschema import Draft202012Validator as V; s=json.load(open(sys.argv[1], encoding="utf-8")); d=json.load(open(sys.argv[2], encoding="utf-8")); V.check_schema(s); V(s).validate(d)' \
  docs/operations/cw-m5-026b-inspection-credential-preparation-authorization.schema.json \
  /home/cdjensen94/continuumwiki-validation/authorizations/cw-m5-026b-credprep-source.json
```

For A, validate the completed manifest against the credential-preparation
schema with the installed Draft 2020-12 validator, then run the source-defined
nonconnecting gate:

```bash
python3 scripts/preflight-m5-026b-inspection-credential-preparation.py \
  --manifest /home/cdjensen94/continuumwiki-validation/authorizations/cw-m5-026b-credprep-source.json
```

Require `CREDENTIAL_PREPARATION_AUTHORIZATION_VALID_NONCONNECTING` and zero
credential reads/writes, database connections, and Podman commands in its
create-only report. Only then may the separately approved source converter
run, using the exact report path recorded in its manifest:

```bash
python3 scripts/cw-m5-026b/prepare_inspection_credentials.py \
  --manifest /home/cdjensen94/continuumwiki-validation/authorizations/cw-m5-026b-credprep-source.json \
  --preflight-report /home/cdjensen94/continuumwiki-validation/cw-m5-026b-credential-preparation-preflight-CW-M5-026B-CREDPREP-SOURCE-PROPOSAL-A.json \
  --approval-id CW-M5-026B-CREDPREP-SOURCE-PROPOSAL-A \
  --prepare
```

Repeat A's independent schema-validation, nonconnecting preflight, and
converter steps for B using only the destination manifest, preflight report,
approval ID, and target binding. The exact B commands, after a genuine B
approval, are:

```bash
python3 -c 'import json,sys; from jsonschema import Draft202012Validator as V; s=json.load(open(sys.argv[1], encoding="utf-8")); d=json.load(open(sys.argv[2], encoding="utf-8")); V.check_schema(s); V(s).validate(d)' \
  docs/operations/cw-m5-026b-inspection-credential-preparation-authorization.schema.json \
  /home/cdjensen94/continuumwiki-validation/authorizations/cw-m5-026b-credprep-destination.json

python3 scripts/preflight-m5-026b-inspection-credential-preparation.py \
  --manifest /home/cdjensen94/continuumwiki-validation/authorizations/cw-m5-026b-credprep-destination.json

python3 scripts/cw-m5-026b/prepare_inspection_credentials.py \
  --manifest /home/cdjensen94/continuumwiki-validation/authorizations/cw-m5-026b-credprep-destination.json \
  --preflight-report /home/cdjensen94/continuumwiki-validation/cw-m5-026b-credential-preparation-preflight-CW-M5-026B-CREDPREP-DESTINATION-PROPOSAL-B.json \
  --approval-id CW-M5-026B-CREDPREP-DESTINATION-PROPOSAL-B \
  --prepare
```

Never combine A and B into one manifest.
Stop on any failed check, stale/expired approval, host/identity mismatch,
unexpected existing option file, nonzero preflight operation count, or
credential diagnostic. Do not print or inspect credential values.

### Fresh preflight renewal for the approved A/B manifests

`PreflightReportPath` remains bound by each unchanged authorization manifest.
When that path already has a report or a report has aged past 600 seconds, do
not overwrite or edit it. `--renew` asks the preflight to create a fresh,
uniquely named sibling report derived internally from the bound path. The
report records its own exact `ReportPath`; the canonical manifest hash is
unchanged. The converter rejects a report whose path differs from the exact
file supplied to `--preflight-report`, whose name is not the bound path or a
generated renewal sibling, or whose hash, approval ID, role, counts, expiry,
or freshness does not match. All report files remain create-only and mode
`0600`.

Use the current authorization and approval ID for each target. For source:

```bash
set -o pipefail
SOURCE_PREFLIGHT_OUTPUT="$(python3 scripts/preflight-m5-026b-inspection-credential-preparation.py \
  --manifest /home/cdjensen94/continuumwiki-validation/authorizations/cw-m5-026b-credprep-source.json \
  --renew)" || exit $?
printf '%s\n' "$SOURCE_PREFLIGHT_OUTPUT"
SOURCE_PREFLIGHT_REPORT="${SOURCE_PREFLIGHT_OUTPUT##*; report=}"
test -n "$SOURCE_PREFLIGHT_REPORT"
python3 scripts/cw-m5-026b/prepare_inspection_credentials.py \
  --manifest /home/cdjensen94/continuumwiki-validation/authorizations/cw-m5-026b-credprep-source.json \
  --preflight-report "$SOURCE_PREFLIGHT_REPORT" \
  --approval-id CW-M5-026B-CREDPREP-SOURCE-PROPOSAL-A \
  --prepare
```

For destination, repeat as a separate operation, changing the manifest path
to `cw-m5-026b-credprep-destination.json`, variable names to
`DESTINATION_PREFLIGHT_OUTPUT` and `DESTINATION_PREFLIGHT_REPORT`, and the
approval ID to `CW-M5-026B-CREDPREP-DESTINATION-PROPOSAL-B`. Stop unless the
fresh preflight classification is valid and every operation count is zero.
The preflight itself does not convert credentials; the converter commands
above remain gated by the existing operation-specific approvals. This
renewal procedure does not authorize inspection C.

After both conversions succeed, validate C's completed manifest against the
read-only inspection schema and run its independent nonconnecting preflight:

```bash
python3 -c 'import json,sys; from jsonschema import Draft202012Validator as V; s=json.load(open(sys.argv[1], encoding="utf-8")); d=json.load(open(sys.argv[2], encoding="utf-8")); V.check_schema(s); V(s).validate(d)' \
  docs/operations/cw-m5-026b-readonly-inspection-authorization.schema.json \
  /home/cdjensen94/continuumwiki-validation/authorizations/cw-m5-026b-readonly-inspection.json

python3 scripts/preflight-m5-026b-inspection.py \
  --manifest /home/cdjensen94/continuumwiki-validation/authorizations/cw-m5-026b-readonly-inspection.json \
  --report /home/cdjensen94/continuumwiki-validation/cw-m5-026b-inspection-preflight-CW-M5-026B-INSPECT-PROPOSAL-C.json
```

Proceed only if classification is
`CATEGORY_READONLY_INSPECTION_AUTHORIZATION_VALID_NONCONNECTING`, the
operation counts are all zero, the report is fresh (the runner enforces ten
minutes), its manifest hash matches C, both targets/options pass runtime
checks, and C remains valid. Then and only then, with a genuinely approved
ID/path substituted if needed, the source-defined runner invocation is:

```bash
python3 scripts/cw-m5-026b/inspect_readiness.py \
  --manifest /home/cdjensen94/continuumwiki-validation/authorizations/cw-m5-026b-readonly-inspection.json \
  --preflight-report /home/cdjensen94/continuumwiki-validation/cw-m5-026b-inspection-preflight-CW-M5-026B-INSPECT-PROPOSAL-C.json \
  --execute-readonly-inspection \
  --approval-id CW-M5-026B-INSPECT-PROPOSAL-C
```

All paths and IDs above are proposal examples, not executable authorization.
Do not run these example commands with the suggested IDs. Any operation
failure is a stop; no repairs, retries under expired/consumed approval, or
cleanup are authorized.

## Source validation performed for this packet

The two schemas are Draft 2020-12 contracts. The credential conversion
contract and read-only inspection contract remain separate; the test suite
uses synthetic test-only records and credentials and does not touch retained
credential files, Podman, or MariaDB. An incomplete proposal cannot validate
as a manifest until the operator supplies the schema-required approval fields;
that is intentional and no placeholder timestamps or identities should be
inserted to make it pass. Validation run: `python3 -m unittest discover -s
scripts -p 'test_m5_026b*.py'` — 108 tests passed. Both contracts passed
`Draft202012Validator.check_schema`; the proposal's fixed target, host,
permission, suggested-ID syntax, and report-path fields validated. Full
manifest instance validation is intentionally pending genuine operator
approval fields.

Production migration and repository activation remain `NO_GO`.
