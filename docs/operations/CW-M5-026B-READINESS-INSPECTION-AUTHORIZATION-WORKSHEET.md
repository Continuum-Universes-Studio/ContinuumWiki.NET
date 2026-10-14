# CW-M5-026B — Read-only Data-Readiness Inspection Request

**Non-authorizing worksheet.** No approval is recorded here, and this is not
an executable manifest. Provisioning/continuation authorizations do not grant
rehearsal inspection rights. A new, distinct, exact-target operator approval
and a fresh nonconnecting preflight must precede any host-side inspection.

## Purpose and limits

The only requested live operation is read-only verification of the retained
CW-M5-026B source and destination targets: current runtime identity and
loopback/image/volume binding, application-grant scope, schema objects and
ledger versions, and privacy-safe aggregate fixture sufficiency/emptiness.
No schema or row changes, grant repair, fixture writes, migrations, backup,
restore, certification, cleanup, or deletion are included.

The assessment report is
`artifacts/validation/mediawiki-category-backup-restore-data-readiness-v3.json`.
It records hashes and source claims, not passwords, raw grants, page bodies,
or category/recovery/grant row data. Exact runtime container IDs remain bound
to the retained secure continuation result
`secure/evidence/cw-m5-026b-continuation-result-20261012-003.json`; an operator
must re-attest them immediately before any future connection. Do not copy
credential values or raw grant output into this worksheet or public evidence.

## Exact target bindings from retained evidence (historical, not current attestation)

These values are copied from retained provisioning and successful continuation
records, reconciled by readiness report v3. They bind the proposal; they do not
prove that either resource still exists or has the same runtime configuration.
The operator must compare them to fresh host inspection before any database
connection. Any mismatch is a stop condition.

| Field | Source | Destination |
| --- | --- | --- |
| Container name | `continuumwiki-m5-026b-source-ro1` | `continuumwiki-m5-026b-destination-ro1` |
| Retained container ID | `6a4d4f8707ee4f5b14dddfff183db6d26ce7453eaba377b7c9d081bc1157f914` | `1c58a2f370d36af443e53283d4212604e997188e9fbf81175655446b7c3ea1f5` |
| Volume | `continuumwiki-m5-026b-source-ro1-data` | `continuumwiki-m5-026b-destination-ro1-data` |
| Database | `cw_m5_026b_source_ro1` | `cw_m5_026b_destination_ro1` |
| Endpoint | `127.0.0.1:13326` | `127.0.0.1:13327` |
| Application identity | `continuumwiki_m5_026b_source_app` | `continuumwiki_m5_026b_destination_app` |
| Credential directory (path only; values not inspected) | `/home/cdjensen94/.local/state/continuumwiki/cw-m5-026b/source-ro1` | `/home/cdjensen94/.local/state/continuumwiki/cw-m5-026b/destination-ro1` |

Both targets are bound to immutable image
`docker.io/library/mariadb@sha256:6422478cb8e159f080fb1d8ccf65101e26fe51385787fde7d16c3b165a331f15`.
The historical provisioning RunId is `20261012T093746Z-86380`; verify the
container and volume labels afresh. Historical grant observations were `false`
for source and `true` for destination schema scope; neither is current evidence.
Current grants remain `UNKNOWN/NOT_OBSERVED`; runtime existence, health, target
binding and destination emptiness require fresh inspection.

## Proposed operation-specific approval

The operator must explicitly establish each field; blanks remain unresolved.

- New unique approval ID and actual UTC approval/expiry timestamps (maximum
  24 hours); approver, operator, target owner, roles, and target ownership.
- Exact host name, Unix user, UID, rootless Podman identity, and freshly
  attested source/destination container IDs, immutable image, volumes,
  database names, endpoints, and dedicated identities.
- Confirmation that both targets are disposable and not production/shared.
- A bounded read-only scope: connect to each named database; inspect identity,
  MariaDB 11.8 family/comment, current application-grant scope, table/engine,
  column/index/constraint metadata, expected ledger versions, and aggregate
  fixture sufficiency only. On destination, inspect emptiness only.
- Explicitly deny all of: grant changes, DDL/migrations, DML/fixture writes,
  backup, restore, fault injection, cleanup, schema deletion, and
  container/volume deletion.
- Specify a create-only sanitized report path and owner-only permissions.
  Raw grants and query outputs must be parsed in memory and reduced to status
  codes/counts before evidence is persisted.
- Confirm the credential transport. The historical provisioner created
  owner-only environment files, while the rehearsal runner requires MariaDB
  option files (`--defaults-extra-file`). Their compatibility is not
  established. Do not read/reformat credentials without a separately explicit
  operator decision and a suitable approved transport.
- Record fresh host name, Unix user/UID, rootless Podman status, approval ID,
  actual approval/expiry timestamps (maximum 24 hours), approver/operator/owner
  identities and roles, and confirmation that both exact targets are disposable
  and not production/shared. Do not reuse historical host claims without
  re-attestation.

### Contract compatibility and non-authorizing proposal

The existing `cw-m5-026b-authorization-v2` schema authorizes a backup/restore
rehearsal; its runner may write the destination and is not permitted for this
inspection. The separate `cw-m5-026b-readonly-inspection-authorization-v1`
schema, nonconnecting preflight, fixed-allowlist runner, and mocked regression
tests are now source-verified. This worksheet remains a decision packet, not an
approval or executable manifest. No previous provisioning, continuation, or
rehearsal approval transfers to this operation.

Proposed operator decision (not an approval and not an executable grant):

> I, [approver name], acting as [approver role], approve [operator name and
> role] to perform one read-only CW-M5-026B readiness inspection. Target owner:
> [owner name]. The source is container `continuumwiki-m5-026b-source-ro1`, ID
> `6a4d4f8707ee4f5b14dddfff183db6d26ce7453eaba377b7c9d081bc1157f914`, volume
> `continuumwiki-m5-026b-source-ro1-data`, database
> `cw_m5_026b_source_ro1`, endpoint `127.0.0.1:13326`, identity
> `continuumwiki_m5_026b_source_app`. The destination is container
> `continuumwiki-m5-026b-destination-ro1`, ID
> `1c58a2f370d36af443e53283d4212604e997188e9fbf81175655446b7c3ea1f5`, volume
> `continuumwiki-m5-026b-destination-ro1-data`, database
> `cw_m5_026b_destination_ro1`, endpoint `127.0.0.1:13327`, identity
> `continuumwiki_m5_026b_destination_app`. Both are bound to image
> `docker.io/library/mariadb@sha256:6422478cb8e159f080fb1d8ccf65101e26fe51385787fde7d16c3b165a331f15`
> and provisioning RunId `20261012T093746Z-86380`. The authorized host is
> `christian-laptop`, Unix user `cdjensen94`, UID `1000`, rootless Podman; these
> host and runtime values must be freshly verified before any connection.
>
> Authorized operations are limited to host/target isolation checks; read-only
> connections to these two databases; one identity/version check and grant
> scope classification per database; schema, index, constraint, foreign-key,
> and ledger metadata inspection; aggregate-only source fixture counts;
> destination object counts; and retention of sanitized evidence at
> [new create-only report path]. Approval ID: [new unique
> `CW-M5-026B-INSPECT-*` ID]. ApprovedAtUtc: [actual UTC submission time].
> ExpiresAtUtc: [actual UTC time no more than 24 hours later]. The credential
> transport is [approved pre-existing MariaDB option files or separately
> authorized conversion]. Both targets are disposable and not production or
> shared. All other operations are denied.

The proposed authorization must encode exactly these inspection permissions;
these values define the proposed scope, not an issued approval:

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

Unresolved before any executable authorization: genuine approval of the exact
scope above; new unique approval ID and actual timestamps; approver/operator/
target-owner names and roles; fresh host/user/UID/rootless identity; confirmation
that both exact targets remain disposable and non-production; fresh runtime
confirmation of container IDs, volumes, database names, endpoints, immutable
image and run labels; approved credential transport; selected option-file paths
(or separately authorized conversion); and a new private create-only report
path. Historical target values are fixed by the inspection contract and cannot
be silently substituted if live inspection differs.

The contract checks approval-ID syntax but has no consumed-ID registry; it
cannot prove that a proposed ID is globally unused. The approving operator
must establish a unique new ID and limit the grant to one serial invocation.
Create-only result paths prevent ordinary overwrite but are not an approval
consumption mechanism. Do not launch concurrent or repeated runs.

## Separate credential-preparation authorization

Provisioning source writes two dedicated files: `environment.env` contains
MariaDB root initialization variables (including a root password), and
`application.env` contains exactly `CW_M5_APP_USER` and
`CW_M5_APP_PASSWORD`. Neither file is a MariaDB client option file. The
inspection runner supplies an option-file path through MariaDB's
`--defaults-extra-file`; it does not parse the file contents. The existing
environment files therefore cannot be passed directly to the inspection
runner. Their presence and current modes were not checked for this worksheet.

A new converter supports only the generated application-file shape: exact
target identity plus a 64-character lowercase hexadecimal password generated
by the existing provisioner. It creates a deterministic MariaDB `[client]`
option file containing only the application user and password. It never opens
`environment.env` and never runs MariaDB or Podman. Its in-process syntax
validation is strict and content is not sent to logs, output, exception text,
preflight reports, or inspection evidence. It cannot independently prove that
the local MariaDB client accepts a file; a future authorized connection is
still separately needed to verify authentication.

Credential preparation has its own Draft 2020-12 contract and nonconnecting
preflight, distinct from inspection authorization. It is one-target-per-approval;
source and destination preparation require separate, independent approvals.
The exact allowed `PermittedOperations` are:

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

The approver must separately approve: a unique `CW-M5-026B-CREDPREP-*` ID;
actual UTC approval and expiry timestamps (no more than 24 hours); approver,
operator and target-owner identities/roles; host `christian-laptop`, user
`cdjensen94`, UID `1000`; the one exact target role and all bound container,
volume, database, endpoint, image, run ID and application identity fields; the
exact `application.env` input and `inspection-client.cnf` output paths in that
target's existing 0700 credential directory; and a private create-only
preflight report path. For either role, target details are the matching column
in the table above. Existing option files are never overwritten. The tool
validates the source directory and file ownership/modes and rejects symlinks,
hard links, malformed/extra environment keys, identity mismatch, and invalid
password syntax. A failed partial write removes only the file created by that
invocation; if it cannot safely remove it, it returns a sanitized
`OPTION_FILE_PARTIAL_RETAINED_OWNER_ONLY` diagnostic for operator attention.

The credential-preparation preflight reads only the nonsecret authorization
manifest and retained identity evidence. It performs zero credential-value
reads, writes, Podman commands, or database connections. After separate genuine
approval, the operator must validate the manifest against
`cw-m5-026b-inspection-credential-preparation-authorization.schema.json`, run
the preflight to its absent `.../cw-m5-026b-credential-preparation-preflight-<approval-id>.json`
path, confirm the valid nonconnecting classification and zero operation
counts, and only then run the converter with `--prepare`, the same manifest,
fresh preflight report, and exact approval ID. The executable command is
intentionally deferred to a separate post-approval handoff with the chosen
paths and ID. This authorization creates no inspection rights: the distinct
read-only inspection manifest and preflight are still required afterward.

### Renewing an expired preflight report

`PreflightReportPath` in each signed-off manifest remains the canonical path.
The preflight CLI's `--renew` option derives a unique sibling filename from
that bound path, records the exact path in the report, and creates it with
`O_EXCL` and mode `0600`. It does not change the manifest or overwrite prior
reports. The canonical manifest hash is unchanged. The converter requires
`--preflight-report` to name that exact report and checks the recorded path,
manifest hash, approval ID, role, operation counts, approval expiry, and the
existing 600-second freshness limit. If a generated filename collides, that
run fails closed; rerun the preflight with `--renew` for a different name.

For a renewed source report, from the repository root:

```bash
set -o pipefail
SOURCE_PREFLIGHT_OUTPUT="$(python3 scripts/preflight-m5-026b-inspection-credential-preparation.py \
  --manifest /home/cdjensen94/continuumwiki-validation/authorizations/cw-m5-026b-credprep-source.json \
  --renew)" || exit $?
printf '%s\n' "$SOURCE_PREFLIGHT_OUTPUT"
SOURCE_PREFLIGHT_REPORT="${SOURCE_PREFLIGHT_OUTPUT##*; report=}"
test -n "$SOURCE_PREFLIGHT_REPORT"
```

Use the reported path exactly, and only while the same approval remains
valid, in the converter's existing `--preflight-report "$SOURCE_PREFLIGHT_REPORT"`
argument. For destination, repeat with
`cw-m5-026b-credprep-destination.json` and a separate
`DESTINATION_PREFLIGHT_OUTPUT` / `DESTINATION_PREFLIGHT_REPORT`. Do not pass a
caller-chosen path to preflight; renewal paths are generated internally from
the manifest-bound path. This does not authorize running the converter or
grant database inspection rights.

Prior source-verification, destination-provisioning, and continuation
approvals are not reusable for this inspection. Their timestamps and identity
bindings only establish historical provenance.

## Inspection SQL plan (not executed)

These statements describe inspection categories, not a manual execution
script. The runner's exact bounded SQL (including row limits and referenced
schema checks) is compiled into `readiness.py` and `inspect_readiness.py`.
The offline `readiness.py` planner does not connect; `inspect_readiness.py` is
the separate host runner and accepts no operator-supplied SQL. Do not run the
illustrative statements below directly. Raw output is transient and is not
persisted.

The authorized runner will verify rootless mode; exact container IDs, running
states, pinned image reference and repository digest; provisioning run labels;
volume names and `/var/lib/mysql` mounts; and the precise loopback port mapping.
It will connect only to the selected database identities and return database
name, MariaDB version family/comment and current account identity as a
match/mismatch classification. No full container configuration or environment
is emitted. No host or database command has been run for this packet.

The operator-side parser should use the selected account’s `SHOW GRANTS` only
in memory and persist booleans such as `GlobalPrivilegesAbsent`,
`GrantOptionAbsent`, `AllGrantsConfinedToTargetSchema`, and
`UnexpectedRoleOrUnscopedGrantAbsent`. Never persist the grant lines. Also
check the six expected schema ledger domain/version pairs and collect only
aggregate counts for `Page`, `PageRevision`, category index states/memberships,
V2 operations/audits, and V3 grants/audits. Do not select `Page.Body`,
`ScopeJson`, credentials, grant payloads, or other row values.

The readiness script and runner source record the exact bounded read-only
ledger, fixture aggregate, foreign-key and category-state/visibility queries.
The latter
distinguishes verified-empty pages, pages without an index-state row, populated
current memberships, active pages, and deleted pages using counts only. Such
counts do not identify a particular synthetic fixture; a separately approved
fixture marker/specification is still needed to prove test-data sufficiency.

Destination emptiness must include base tables and non-table schema objects
(views, routines, events, triggers), with no row values. Any mismatch, access
error, malformed or ambiguous identity/grant output, missing schema or
fixture, unexpected object, stale provenance, or non-InnoDB table is recorded
as an explicit failed, incomplete, or mismatch finding; it is never repaired
or treated as certification. The runner may finish its remaining fixed
read-only queries to provide a complete diagnostic, but no state-changing
follow-up is permitted by this contract.

## After approval

The dedicated inspection runner and separate inspection-only contract are
source-implemented. No authoritative manifest exists. The inspector accepts
only an owner-owned 0600 option-file path inside the matching owner-owned 0700
target credential directory. Python checks path ownership and mode but neither
creates the file nor reads its contents; the MariaDB client will consume the
file during a future authorized connection, and its values are never copied
into evidence. Do not create or convert option files without separate explicit
authorization. No preflight or live command is to be run before genuine
approval, schema validation, and a fresh nonconnecting preflight.

The later host-side procedure will use a unique, absent preflight result name
under `/home/cdjensen94/continuumwiki-validation/`, for example
`cw-m5-026b-inspection-preflight-CW-M5-026B-INSPECT-uniqueid.json`. The
manifest's `ReportPath` will similarly be a unique absent
`cw-m5-026b-inspection-result-CW-M5-026B-INSPECT-uniqueid.json` under that
directory. Parent directories must be operator-owned mode `0700`; preflight
and result files are created once with mode `0600`. Existing paths are never
overwritten. The preflight must report
`CATEGORY_READONLY_INSPECTION_AUTHORIZATION_VALID_NONCONNECTING`, zero
connections/queries/mutations, and the same manifest hash later supplied to
the runner. Stop on any other classification, stale (>10 minute) preflight,
identity mismatch, missing option file, or expired approval. Exact host
command lines are deliberately withheld until genuine approval and final
approved paths/ID are established.

First, the offline readiness report can be regenerated independently; it does
not inspect host or database state:

```bash
python3 scripts/cw-m5-026b/readiness.py \
  --report /home/cdjensen94/continuumwiki-validation/mediawiki-category-backup-restore-data-readiness-operator-v1.json
```

No live execution command is issued in this source-only worksheet. After
source verification and genuine, target-specific operator approval, provide a
separate execution handoff with the exact approved manifest path, unique
approval ID, and create-only preflight/result paths. That handoff must run
Draft 2020-12 validation, the nonconnecting preflight, and only then the
read-only inspector. The inspector requires `--execute-readonly-inspection`
and `--approval-id`; without that explicit switch it refuses to run.

The manifest binds the create-only result path. The preflight must be fresh
(within ten minutes), valid, and for the same manifest hash. The runner also
requires the exact host/user/UID and rootless Podman identity, verifies both
targets fully before connecting, and stops on any mismatch or missing option
file. It never accepts a caller-supplied command or SQL. Do not substitute
`scripts/cw-m5-026b/rehearse-m5-026b.sh`; that runner performs backup/restore
and is not read-only. These commands have not been run against live resources.

Production migration and repository activation remain `NO_GO`.
