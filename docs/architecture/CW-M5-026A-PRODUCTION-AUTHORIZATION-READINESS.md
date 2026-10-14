# CW-M5-026A — Production Authorization and Target-Readiness Preparation

## Status

`DRAFT / UNAPPROVED`

Classification: `CATEGORY_PRODUCTION_AUTHORIZATION_READINESS_SOURCE_VERIFIED`

Production migration: `NO_GO`  
Production repository activation: `NO_GO`

This document is a read-only preparation package. It is not an approval,
authorization manifest, target proof, migration command, or deployment plan
that can be executed without a separately supplied human decision. No
production or disposable database was contacted for CW-M5-026A. No manifest
was created under `secure/evidence/`, and no credentials were read.

## Verified repository facts

1. The source migration dependency order is **existing base Page schema →
   category V1 → recovery V2 → administrative-grant V3**. V1 references
   `Page.Id` and creates the shared `MySqlCategorySchemaVersion` ledger. V2
   and V3 add their own tables and ledger rows.
2. V1, V2, and V3 are forward, repeatable-style definitions using
   `CREATE TABLE IF NOT EXISTS` and `GREATEST` version updates. No downgrade
   migration was found in the category SQL. A rollback therefore requires an
   independently reviewed application/data procedure; it is not a tested SQL
   downgrade.
3. `Program.cs` and `MySqlDevelopmentRuntime.cs` register rejecting category,
   recovery-durability, and administrative-grant implementations by default.
   The normal startup paths do not call the explicit MySQL activation helpers.
4. `appsettings.json` keeps SQLite as the default provider, MariaDB
   `ProductionReady` false, and experimental Lua disabled.
5. The activation helpers require MySQL, an explicit feature flag, the exact
   schema version, an existing connection factory, and a readiness proof. A
   connection string or successful build is not authorization.
6. The administrative recovery controller is authenticated; preview and
   execute are anti-forgery protected. The API itself requires the explicit
   configuration gate, trusted administrator context, page authorization,
   operation grant, readiness evidence, and durable recovery boundary.
7. CW-M5-019, CW-M5-020, and CW-M5-025 contain bounded disposable MariaDB
   evidence only. Their target identities, approvals, and credentials are not
   transferable to production or to this milestone.

## Production target worksheet — operator completion required

Every value in this section is intentionally unknown. The operator must
provide the value and its independent evidence in a future approval record;
this document must not be filled by inference from disposable targets.

| Field | Current value | Required evidence |
| --- | --- | --- |
| Environment identifier | `UNKNOWN / OPERATOR_REQUIRED` | Named production environment and change record |
| Engine/provider | `UNKNOWN / OPERATOR_REQUIRED` | Read-only version/provider evidence; source requirement is MariaDB 11.8.x-compatible behavior |
| Server/cluster identity | `UNKNOWN / OPERATOR_REQUIRED` | Nonsecret server or cluster fingerprint from the responsible database operator |
| Database identity/name | `UNKNOWN / OPERATOR_REQUIRED` | Exact target identity from an approved operator source |
| Endpoint/connection route | `UNKNOWN / OPERATOR_REQUIRED` | Nonsecret endpoint evidence; credentials must remain outside reports |
| Hosting/data owner | `UNKNOWN / OPERATOR_REQUIRED` | Named accountable owner and data classification |
| Application identity | `UNKNOWN / OPERATOR_REQUIRED` | Least-privilege identity and schema scope evidence |
| Existing Page schema | `UNKNOWN / OPERATOR_REQUIRED` | Read-only schema/version ledger report proving Page compatibility |
| Category V1 state | `UNKNOWN / OPERATOR_REQUIRED` | Read-only ledger/table/index/foreign-key report |
| Recovery V2 state | `UNKNOWN / OPERATOR_REQUIRED` | Read-only ledger/table/index/foreign-key report |
| Grant V3 state | `UNKNOWN / OPERATOR_REQUIRED` | Read-only ledger/table/index/foreign-key report |
| Source scope/freeze | `UNKNOWN / OPERATOR_REQUIRED` | Approved source revision, data freeze, and migration scope |
| Evidence freshness | `UNKNOWN / OPERATOR_REQUIRED` | Timestamp, producer, checksum, and expiry policy |
| Production isolation | `UNKNOWN / OPERATOR_REQUIRED` | Independently verified target and ownership boundary; never inferred from a name |

## Approval worksheet — unapproved

| Approval field | Required future value |
| --- | --- |
| Approver name and role | `OPERATOR_REQUIRED` |
| Operator name and role | `OPERATOR_REQUIRED` |
| Target/data owner | `OPERATOR_REQUIRED` |
| Approval/change ID | `OPERATOR_REQUIRED` |
| ApprovedAtUtc | `OPERATOR_REQUIRED` |
| ExpiresAtUtc | `OPERATOR_REQUIRED`; finite validity required |
| Maintenance window | `OPERATOR_REQUIRED` |
| Emergency contact and rollback decision owner | `OPERATOR_REQUIRED` |
| Independent reviewer | `OPERATOR_REQUIRED` where separation of duties applies |
| Production authorization established | `false` |

### Proposed separated permissions

These are requirements for a future approval record, not permissions granted by
CW-M5-026A:

```text
ReadOnlyTargetPrechecks             OPERATOR_REQUIRED
SnapshotAndChecksum                 OPERATOR_REQUIRED
IndependentRestore                  OPERATOR_REQUIRED
ApplyBasePagePrerequisite            OPERATOR_REQUIRED
ApplyCategoryV1                      OPERATOR_REQUIRED
ApplyRecoveryV2                      OPERATOR_REQUIRED
ApplyAdministrativeGrantV3           OPERATOR_REQUIRED
CategoryBackfill                     OPERATOR_REQUIRED
MonitoringAndAlertConfiguration     OPERATOR_REQUIRED
ProviderActivation                  OPERATOR_REQUIRED
AdministrativeGrantIssuance         OPERATOR_REQUIRED
AdministrativeGrantConsumption      OPERATOR_REQUIRED
AdministrativeGrantRevocation       OPERATOR_REQUIRED
RollbackOrDisable                   OPERATOR_REQUIRED
SchemaDeletion                      SEPARATE_EXPLICIT_APPROVAL_REQUIRED
DatabaseCleanup                     SEPARATE_EXPLICIT_APPROVAL_REQUIRED
ContainerOrVolumeDeletion           SEPARATE_EXPLICIT_APPROVAL_REQUIRED
```

Migration, activation, rollback, and destructive cleanup must not be implied
by one another. A production connection string is not an approval.

## Gate and dependency matrix

| Gate | Current state | Evidence required for closure | Authority |
| --- | --- | --- | --- |
| Production target identification | `BLOCKED / UNKNOWN` | Exact target, engine, owner, identity, endpoint, freshness, and isolation proof | Database operator and data owner |
| Production approval | `BLOCKED / ABSENT` | Signed/recorded target-bound approval with finite validity and scoped permissions | Project Owner/System Administrator plus operations approver |
| Category-inclusive backup/restore | `NOT_CERTIFIED` | Checksummed snapshot, distinct-target restore, category/recovery/grant reconciliation, smoke test, and post-snapshot write plan | Database/backup owner |
| Migration prerequisites | `BLOCKED` | Base Page → V1 → V2 → V3 rehearsal, schema readback, lock/latency review, repeatability result, and abort conditions | Database operator |
| Backfill/reconciliation | `NOT_CERTIFIED` | Dry-run counts and privacy-safe evidence for empty, stale, incomplete, partial, cancel/resume, and generation cases | Category data owner |
| Monitoring/fault readiness | `OPERATIONALLY_INCOMPLETE` | Alerts and runbooks for unavailable, stale/inconsistent, indeterminate, audit failure, disabled provider, and failed readiness states | Operations/reliability owner |
| Administrative grant policy | `OPERATIONALLY_INCOMPLETE` | Production identity provenance, issuer/revoker ownership, expiry, single-use behavior, audit retention, and incident response | Security/operations owner |
| Provider activation | `BLOCKED` | Exact production readiness proofs, explicit feature flags, and separate activation approval | Application owner and operations approver |
| Rollback/disable | `NOT_CERTIFIED` | Reviewed disable-to-rejecting procedure, write journal/reconciliation, restore decision, and abort thresholds | Change owner and rollback decision owner |
| Historical compatibility | `NOT_CERTIFIED` | Independent MediaWiki/Cargo/Scribunto/extension evidence | Migration owner |

Dependencies must remain ordered:

```text
target identity and approval
        ↓
backup/checksum + independent restore
        ↓
base Page schema proof → category V1 → recovery V2 → grant V3
        ↓
backfill/reconciliation and monitoring readiness
        ↓
explicit provider activation approval
```

## Proposed future target-bound preflight

An authorized operator may later define a preflight that performs only the
following, in order:

1. Validate the human-supplied approval record, expiry, target identity,
   permissions, and production/nonshared scope without opening a connection.
2. Verify the exact production target and least-privilege identity through the
   approved operator mechanism; record sanitized identity evidence only.
3. Verify engine/version, selected database, base Page schema, and category
   schema ledgers read-only before any mutation.
4. Verify snapshot/checksum and distinct restore evidence.
5. Abort on target mismatch, stale evidence, unsupported schema, missing
   permissions, connection ambiguity, or any production/shared-target
   uncertainty.

No command is supplied here that bypasses those approvals or silently contacts
the target.

## Proposed migration and backfill sequence

This is a review checklist, not an execution authorization:

1. Enter the approved maintenance window and establish the source freeze.
2. Capture and checksum the category-inclusive backup; retain a separate
   restore and verify it before migration.
3. Apply/read back the existing base Page prerequisite, then V1, V2, and V3 in
   separate reviewed steps. Stop on a foreign-key, version, lock, or latency
   anomaly.
4. Keep rejecting providers active until schema, backfill, monitoring, and
   readiness evidence are accepted.
5. Dry-run category extraction and backfill, preserving source page revision,
   index generation, empty-index state, visibility filtering, incomplete
   expansion, partial failures, cancellation, and resume evidence.
6. Reconcile counts and authoritative revisions. Do not treat an empty result
   as success when the index is stale, incomplete, failed, or unavailable.
7. Obtain a separate activation decision; only then change feature flags and
   register providers. Record the ability to revert to rejecting providers.

## Backup, restore, and rollback requirements

Future evidence must include category membership/index-state tables, recovery
operation/checkpoint/audit tables, and administrative grant/audit tables, plus
their schema ledgers and source-provenance reconciliation. A logical backup
is not point-in-time recovery; binary-log capture/replay is not currently
implemented. Post-snapshot writes require a write journal or explicit manual
reconciliation plan. No downgrade SQL is currently present for category V1–V3.

## Operations and security requirements

Monitor only bounded, privacy-safe states: provider unavailable, stale or
inconsistent index, indeterminate recovery, grant/audit failure, disabled
provider, expired authorization, and failed readiness proof. Do not include
passwords, connection strings, tokens, page/category identifiers, grant
payloads, or unrestricted counts in reports or alerts.

The current contract has no lease owner/expiry fields. A single-worker
deployment may continue to rely on CAS and replay, but any multi-worker
deployment needs an explicit lease decision and evidence. OS/container/
MariaDB-server crash behavior and live mid-transaction SQL-fault behavior
remain unverified.

## Next separately authorized milestone

The next recommended nonproduction milestone is **CW-M5-026B**:
category-inclusive backup/restore rehearsal on the separately provisioned
source/destination pair recorded in
`secure/evidence/cw-m5-026b-continuation-result-20261012-003.json`. That
continuation does not authorize rehearsal operations; a fresh, exact
two-target rehearsal approval, current isolation and emptiness checks, and
resolution of source schema/fixture and privilege-scope gaps are still
required. Do not reuse M5-019, M5-025, provisioning, or continuation
approvals. Production migration and activation remain `NO_GO`.

