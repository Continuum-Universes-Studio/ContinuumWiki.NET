# CW-M5-019B — Operator Authorization Manifest and Certification Gate

## Classification

`CATEGORY_AUTHORIZATION_MANIFEST_CANDIDATE_SOURCE_VERIFIED`

Authorization is not established. The existing `operator4` disposable target
was not reprovisioned, migrated, or certified by this milestone. The
authoritative file `secure/evidence/cw-m5-019-authorization.json` remains
absent.

## Reconciled target

The target facts below are copied from the provisioning metadata and the
read-only inspection report; they are not inferred from the `operator4` label:

| Field | Evidence-backed value |
| --- | --- |
| Container | `continuumwiki-m5-019-operator4` |
| Volume | `continuumwiki-m5-019-operator4-data` |
| Database | `cw_m5_019_operator4` |
| Endpoint | `127.0.0.1:13321` |
| Image digest | `docker.io/library/mariadb@sha256:6422478cb8e159f080fb1d8ccf65101e26fe51385787fde7d16c3b165a331f15` |
| Observed engine | `11.8.9-MariaDB-ubu2404` |
| Application identity | `continuumwiki_m5_019_app` |
| Inspection | `PROVISIONED_TARGET_READY_NOT_AUTHORIZED` |

The exact instance identity is recorded in the candidate artifact and both
source evidence files. No credential file path or secret value is copied into
the candidate, report, or this document.

## Why the candidate is separate

The versioned v2 schema at
`docs/operations/cw-m5-019-authorization-manifest.schema.json` requires
approval timestamps, approver/operator identities, an owner, explicit `true`
values for certification operations, explicit boolean values for each
destructive operation, cleanup ownership, and the complete bounded test scope.
Legacy v1 manifests and ambiguous `DropDisposableSchema`/`Cleanup` keys are
rejected. It has no valid representation for a pending approval. Therefore the
review candidate is deliberately not schema-valid and is marked
`DoNotPassToPreflight`.

Candidate:

`artifacts/validation/mediawiki-category-authorization-candidate-operator4-v1.json`

Readiness report:

`artifacts/validation/mediawiki-category-authorization-readiness-v1.json`

## Operator approval checklist

The operator must independently review the candidate against both operator4
evidence files, then create the authoritative manifest at
`secure/evidence/cw-m5-019-authorization.json` using the actual schema. The
operator must supply real, current values for:

1. approval ID, UTC approval time, expiry time, approver name/role, operator
   name/role, and target owner;
2. the exact operator4 container/database identity and loopback endpoint;
3. explicit authorization for connection, V1 migration, V2 migration,
   category mutations, concurrency/crash tests, and sanitized evidence
   retention;
4. explicit `false` or `true` values for disposable schema creation,
   disposable schema deletion, database cleanup, and container/volume deletion;
5. isolation proof, provisioning/cleanup ownership, cleanup attestation path,
   and the complete bounded test scope.

Approval for migration or certification does not imply any destructive
permission. Credentials belong only in the existing owner-only environment
mechanism and must not be placed in the manifest.

After the operator has written the approved manifest, the only next gate is
the existing nonconnecting preflight:

```bash
scripts/preflight-m5-019.sh \
  --manifest "$PWD/secure/evidence/cw-m5-019-authorization.json" \
  --report "$PWD/secure/evidence/cw-m5-019-preflight.json"
```

A successful preflight is still only a proposed plan. Separately authorized
live certification must then use the existing M5-018 gated integration path;
no certification command is executed by CW-M5-019B.

## Preserved boundaries

This milestone performs no database connection or mutation, does not invoke
Podman, does not inspect production, and does not alter Lua, Cargo, worker
isolation, systemd, or the rejecting category/durability defaults. Production
migration remains `NO_GO`.
