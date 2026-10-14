# CW-M5-025B — Operator Authorization Worksheet

## Status

`DRAFT / UNAPPROVED`

This worksheet is not an authorization manifest. It must not be copied to
`secure/evidence/cw-m5-025-authorization.json`, passed to the preflight, or
used to authorize a database connection. The task prompt and the prior
CW-M5-019/operator4 approval do not authorize CW-M5-025.

The authoritative contract is
`docs/operations/cw-m5-025-authorization-manifest.schema.json`, version
`cw-m5-025-authorization-v1`. A genuine operator must supply every value below
and independently approve the exact target and operations.

## Approval metadata — operator must supply

| Field | Required value |
| --- | --- |
| `ManifestVersion` | Exactly `cw-m5-025-authorization-v1` |
| `ApprovalId` | Real, unique string matching `^[A-Za-z0-9._:-]{1,128}$` |
| `ApprovedAtUtc` | String UTC `Z` timestamp; actual approval time and not future-dated |
| `ExpiresAtUtc` | String UTC `Z` timestamp after approval and still valid at preflight |
| `Approver.Name` / `Role` | Genuine nonempty strings, each at most 160 characters |
| `Operator.Name` / `Role` | Genuine nonempty strings, each at most 160 characters |
| `TargetOwner` | Genuine nonempty string, at most 160 characters, owning the exact disposable target |

## Exact target — operator must supply and independently verify

The target must be disposable, loopback-bound, and match all M5-025 patterns:

| Field | M5-025 requirement |
| --- | --- |
| `Target.Disposable` | `true` |
| `Target.ContainerName` | String matching `^continuumwiki-m5-025-[a-z0-9-]{1,48}$` |
| `Target.VolumeName` | String matching `^continuumwiki-m5-025-[a-z0-9-]{1,48}-data$` |
| `Target.DatabaseName` | String matching `^cw_m5_025_[a-z0-9_]{1,48}$` |
| `Target.Endpoint` | String matching loopback plus port; the preflight requires an unprivileged port above 1024 |
| `Target.InstanceIdentity` | Nonempty string, at most 512 characters, beginning `container=...;database=...;endpoint=...;volume=...;` |
| `Target.EngineVersion` | Exactly `MariaDB 11.8.x` in the manifest; verify actual version live only after authorization |
| `Target.DatabaseIdentity` | Exactly `continuumwiki_m5_025_app` |

`Target` has no additional properties. `EngineVersion` is the literal
`MariaDB 11.8.x`; `DatabaseIdentity` is the dedicated schema-scoped database
user identity, not a password or connection string.

The existing `operator4` evidence is not a usable M5-025 target: it is named
`continuumwiki-m5-019-operator4`, uses database
`cw_m5_019_operator4`, and has identity `continuumwiki_m5_019_app`. Those facts
are recorded only as non-transferable evidence in the companion draft JSON.

## Required permissions

The operator must explicitly set every certification permission to `true`:

`Connect`, `VerifyTargetIsolation`, `ApplyV1Prerequisites`,
`ApplyV2Prerequisites`, `ApplyV3Migration`, `GrantIssueMutations`,
`GrantConsumeMutations`, `GrantRevocationMutations`, `GrantAuditMutations`,
`ConcurrentConnectionTests`, `RestartRecoveryTests`,
`AcknowledgmentLossTests`, and `RetainSanitizedEvidence`.

The operator must explicitly set every destructive permission to `false`:

`DatabaseCleanup`, `SchemaDeletion`, and `ContainerVolumeDeletion`.

Any test requiring a permission not granted must be skipped or blocked. No
permission may be inferred from a connection string, an older milestone, or a
successful preflight for another manifest version.

All permission values are JSON booleans. The permissions object has no
additional properties.

## Isolation and test scope

Supply all of:

- `Isolation.Production = false`;
- `Isolation.SharedWithProduction = false`;
- `Isolation.DedicatedCredentials = true`;
- a nonempty, nonsecret `Isolation.Proof` string of at most 2048 characters
  for the exact target;
- a nonempty `TestScope.Suite` array of strings, each at most 128 characters;
- `TestScope.MaxConcurrentWorkers` as an integer from 1 through 4; and
- exactly these crash-boundary labels:
  `after-grant-commit-before-response`,
  `forced-failure-before-grant-audit-commit`, and
  `recreated-service-instance`.

Credentials belong only in the protected operator environment used by the
authorized test command. They must not appear in this worksheet, the draft
JSON, the manifest, or evidence reports.

## Operator sequence after genuine approval

1. Create the authoritative manifest at
   `secure/evidence/cw-m5-025-authorization.json` with restrictive
   permissions, using only genuine supplied values.
2. Run the nonconnecting preflight and stop on any failure:

   ```bash
   scripts/preflight-m5-025.sh \
     --manifest secure/evidence/cw-m5-025-authorization.json \
     --report secure/evidence/cw-m5-025-preflight.json
   ```

3. Before the first connection, provide the exact target environment and
   protected test connection described in the M5-025 runbook.
4. Run only the separately authorized M5-025 certification tests. Do not
   delete schema objects, rows, containers, or volumes.

Until step 1 is performed by the responsible operator, the M5-025 result is
authorization-blocked and production migration/repository activation remain
`NO_GO`.
