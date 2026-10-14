# CW-M5-026B Restricted Query Diagnostic Authorization Worksheet

This worksheet is for preparing a new, operation-specific approval only. It does not itself authorize execution. The unused approval `CW-M5-026B-QUERY-DIAG-20261014-001` is specifically excluded from execution by the preflight and must not be reused.

## Scope

One attempt, on the normal rootless Podman host, to execute exactly the following fixed bindings:

| Query ID | Target | Fixed operation |
| --- | --- | --- |
| `SOURCE_DATABASE_IDENTITY` | `continuumwiki-m5-026b-source-ro1` | Existing `IDENTITY_SQL` constant in `readiness.py` |
| `DESTINATION_MIGRATION_LEDGER` | `continuumwiki-m5-026b-destination-ro1` | Existing `LEDGER_SQL` constant in `readiness.py` |

The query text is not an authorization field and cannot be supplied or overridden. No other query, target pairing, retry, mutation, migration, grant operation, backup, restore, production access, or repository activation is in scope.

## Required manifest

Create a new directory with mode `0700` under `/home/cdjensen94/continuumwiki-validation/` named `cw-m5-026b-query-diagnostic-<fresh-approval-id>`. Place `authorization.json` there with mode `0600`. Create it exclusively; do not replace existing evidence. Use schema `cw-m5-026b-restricted-query-diagnostic-authorization.schema.json`.

The manifest must include:

- A genuinely unused `CW-M5-026B-QUERY-DIAG-*` approval ID, actual UTC approval and expiry timestamps, named approver/operator, and the exact host/user/UID.
- The exact source and destination container names, IDs, image, volume, database, endpoint, independent provisioning RunIds, and canonical existing `inspection-client.cnf` paths. Values must agree with retained evidence at preflight time.
- `AuthorizedQueries` exactly equal to the two fixed QueryId/TargetRole pairs in the schema.
- Limits exactly 60 seconds per query and 600 seconds overall.
- Evidence paths in that same create-only directory: `authorization.json`, `preflight.json`, and `result.json`.

Do not copy an old manifest and merely change its approval ID or timestamps. Construct a fresh approval from current exact-target evidence. Never include credential contents or arbitrary SQL.

## Gating and execution

1. Independently establish the approval and its exact scope before preparing the manifest.
2. Run the nonconnecting preflight:

   ```sh
   python3 scripts/preflight-m5-026b-restricted-query-diagnostic.py \
     --manifest /home/cdjensen94/continuumwiki-validation/cw-m5-026b-query-diagnostic-<ID>/authorization.json \
     --report /home/cdjensen94/continuumwiki-validation/cw-m5-026b-query-diagnostic-<ID>/preflight.json
   ```

3. Continue only if it returns `RESTRICTED_QUERY_PREFLIGHT_VALID_NONCONNECTING`. It verifies schema, host, retained target bindings, credential-file metadata (without opening contents), evidence paths, approval freshness/uniqueness, and create-only state. It runs no Podman command and opens no database connection.
4. Within 600 seconds, execute once:

   ```sh
   python3 scripts/cw-m5-026b/run_restricted_query_diagnostic.py \
     --manifest /home/cdjensen94/continuumwiki-validation/cw-m5-026b-query-diagnostic-<ID>/authorization.json \
     --preflight-report /home/cdjensen94/continuumwiki-validation/cw-m5-026b-query-diagnostic-<ID>/preflight.json \
     --execute-authorized-diagnostic
   ```

5. The runner claims the approval once, verifies rootless Podman and both complete target identities before connecting, then invokes each fixed query at most once. Any identity failure stops before either query. Any query failure is reported without retry. The result contains only identity classification, ledger domain/version-match classification, safe error category, exit code, SQLSTATE/errno when parsed, and whether a server response was observed. Raw SQL output, stderr, credential contents, and version values are not persisted.

The report is diagnostic evidence only. It does not establish backup/restore certification. Production migration, repository activation, and backup/restore remain `NO_GO` / not authorized.
