# CW-M5-026B bounded host-connectivity diagnostic

This capability is limited to the two disposable MariaDB containers and the existing loopback endpoints. It does not authenticate to MariaDB, send application data, execute SQL, inspect credentials, enter containers, or change runtime/network state.

## Authorization boundary

A fresh operation-specific approval must authorize one execution of `cw-m5-026b-host-connectivity-v1`, on `christian-laptop` as `cdjensen94` (UID 1000), using rootless Podman. The approval must bind the exact source and destination names, IDs, distinct provisioning RunIds, and endpoints in the schema. It must permit read-only Podman metadata inspection and one payload-free TCP connect to each endpoint, with a three-second timeout, and deny all lifecycle, database, credential, and network-configuration operations. The prior approval `CW-M5-026B-CONNECTIVITY-20261014-001` is not executable through this tooling; obtain fresh approval before any live run.

## Fixed scope

- Source: `continuumwiki-m5-026b-source-ro1`, ID `6a4d4f8707ee4f5b14dddfff183db6d26ce7453eaba377b7c9d081bc1157f914`, RunId `20261012T093746Z-86380`, endpoint `127.0.0.1:13326`.
- Destination: `continuumwiki-m5-026b-destination-ro1`, ID `1c58a2f370d36af443e53283d4212604e997188e9fbf81175655446b7c3ea1f5`, RunId `20261012T123438Z-282573-599fff18456ea0cf36ef987019b3efef`, endpoint `127.0.0.1:13327`.

The schema rejects extra targets, commands, endpoints, probe types, attempts, or operation properties. The nonconnecting preflight verifies schema, live authorization window, host/operator binding, retained provisioning-derived target identity, distinct RunIds, private create-only evidence paths, and preflight/result freshness requirements. It does not call Podman or open sockets.

## One-shot procedure

The operator prepares a new authorization manifest in a private mode-0700 directory under `/home/cdjensen94/continuumwiki-validation/cw-m5-026b-connectivity-<ApprovalId>/`, with the manifest mode 0600 and fresh `preflight.json` and `result.json` paths. Use actual UTC approval/expiry timestamps and the exact approved ID and target bindings. Validate and create the nonconnecting preflight:

```sh
python3 scripts/preflight-m5-026b-host-connectivity.py \
  --manifest /home/cdjensen94/continuumwiki-validation/cw-m5-026b-connectivity-<ApprovalId>/authorization.json \
  --report /home/cdjensen94/continuumwiki-validation/cw-m5-026b-connectivity-<ApprovalId>/preflight.json
```

Only after fresh operation-specific approval and successful preflight, invoke exactly once:

```sh
python3 scripts/cw-m5-026b/diagnose_host_connectivity.py \
  --manifest /home/cdjensen94/continuumwiki-validation/cw-m5-026b-connectivity-<ApprovalId>/authorization.json
```

The runner rechecks manifest hash, authorization expiry, target bindings, and preflight age (maximum 600 seconds), claims the authorization create-only, verifies rootless Podman and both complete target identities, and only then makes one TCP connect attempt per endpoint. An identity mismatch suppresses both probes. Each attempt has a three-second timeout and sends zero bytes. The authorization is consumed on attempt; no automatic retry is permitted.

## Sanitized Podman failure classifications

Podman subprocess failures are reported separately from output interpretation and target comparison:

| Classification | Meaning |
| --- | --- |
| `PODMAN_EXECUTION_UNAVAILABLE` | The executable could not be launched; a safe category may distinguish not-found or permission-denied. |
| `PODMAN_COMMAND_FAILED` | Podman ran and returned nonzero; the report includes the exit code and a safe, allowlisted error category. |
| `PODMAN_TIMEOUT` | The fixed Podman subprocess exceeded its bound. |
| `PODMAN_OUTPUT_EMPTY` | The command succeeded but emitted no expected output. |
| `PODMAN_OUTPUT_INVALID` | Fixed delimited output had an invalid row or field shape. |
| `PODMAN_OUTPUT_SCHEMA_MISMATCH` | Required output fields or process-result types were absent or invalid. |
| `PODMAN_IDENTITY_MISMATCH` | Observed fixed identity fields did not match the approved target. |

The fixed Podman commands use Go-template delimited text, not JSON, so JSON parse failure is recorded as not applicable. Evidence records a fixed operation ID, classification, exit code where available, timeout flag, and safe error category. Raw stdout, stderr, host paths, and observed identity values are not retained. Any observation failure ends the run before TCP probing.

## Evidence and outcomes

Retain the exact authorization manifest, preflight report, and sanitized `result.json` at the three paths bound in the manifest. The result records verified identity checks, running/health observations, configured and published ports, network mode, probe outcome, zero payload bytes, and zero retries. Raw subprocess output is never copied to evidence; only selected, validated fields are retained.

Each endpoint is classified as `TCP_ACCEPTED`, `CONNECTION_REFUSED`, `CONNECTION_TIMEOUT`, or `NETWORK_ERROR`. `TCP_ACCEPTED` establishes only that a TCP listener accepted a connection; it does not establish MariaDB readiness, authentication, authorization, or database readiness. A stopped container is reported as stopped and may still receive its single authorized endpoint probe; this is not permission to start it.

Production migration and repository activation remain `NO_GO`; backup/restore remains not authorized and not certified.
