# CW-M5-026B — Targeted Query Diagnostic Blocker

**Authorization:** `CW-M5-026B-QUERY-DIAG-20261014-001`  
**Outcome:** NOT EXECUTED — existing authorization contracts cannot express the approved query-only scope  
**Database connections made:** 0  
**Authorization consumption:** No diagnostic preflight or execution was attempted

The two pasted requests are identical and contain the same approval ID and
scope.

## Queries in scope

- Source identity query, from `scripts/cw-m5-026b/readiness.py`:
  `SELECT DATABASE(), VERSION(), @@version_comment, CURRENT_USER();`
- Destination ledger query, from the same file: reads the six `pages`,
  `page-support`, `deleted-revisions`, `category`, `category-recovery`, and
  `category-admin-grants` version rows from
  `MySqlPageSchemaVersion` and `MySqlCategorySchemaVersion`.

The preceding sanitized inspection result records `QUERY_FAILED` for each.
That result does not retain a MariaDB SQLSTATE, server error code, or sanitized
client failure category, so the underlying causes remain undetermined.

## Contract incompatibility

The current read-only inspection authorization schema and preflight do not
support this narrowly scoped query selection:

- `docs/operations/cw-m5-026b-readonly-inspection-authorization.schema.json`
  has no query allowlist or per-query permission fields.
- `scripts/preflight-m5-026b-inspection.py` requires one exact permission map
  that grants both target connections and the broader source/destination
  metadata, grant, fixture, and emptiness inspections.
- `scripts/cw-m5-026b/inspect_readiness.py` has no target/query selection
  arguments. It always runs the complete target query sequence: source
  identity, grants, schema, ledger, and fixture queries; destination identity,
  grants, schema, ledger, and destination-object queries.
- The stopped-container diagnostic authorization contracts explicitly set
  `DatabaseConnections` to false and cannot authorize these SQL queries.
- No existing targeted query diagnostic schema or runner is present.

Using the full inspection contract would authorize and execute work broader
than this approval. The stopped-container diagnostic contract cannot be used
for database access. The request explicitly says to stop if no existing
contract supports the limited scope, so no manifest or preflight was created,
and no query was executed.

## Finding and next action

Both failures remain `UNKNOWN` as to cause. The smallest safe next step is to
provide a contract that binds the exact two query names, exact target per
query, existing option-file paths, sanitized exit/error fields, timeout, and
one-shot evidence paths. Then obtain a fresh operation-specific approval for
that contract. Do not infer credential, grant, missing-table, SQL, or
connectivity remediation from the current evidence.

The supplied diagnostic authorization remains unused because no diagnostic
manifest was validated and no diagnostic preflight or execution was attempted.
The prior readiness inspection authorization remains consumed.

## Evidence references

| Evidence | SHA-256 |
| --- | --- |
| Prior sanitized inspection result | `/home/cdjensen94/continuumwiki-validation/cw-m5-026b-inspection-CW-M5-026B-INSPECT-20261014-002/result.json` — `472df01b96bdf60423a9e343673407f1b0d1cd0487bde32caf18de8447615644` |
| Current inspection authorization schema | `docs/operations/cw-m5-026b-readonly-inspection-authorization.schema.json` — `a1d1e6c8d2c25a7f43446d87f88fd4dfa2c8731f3f778c3f822618d0f723d842` |
| Current inspection preflight | `scripts/preflight-m5-026b-inspection.py` — `a72f2beabe9483c119e604bf278aa7c8af1f9192be70d294b681cb81abfd8afd` |
| Current inspection runner | `scripts/cw-m5-026b/inspect_readiness.py` — `c845481aa2594cd2c53beaee71958e8176a9179c7e776401e12497e513a0551a` |
| Source diagnostic preflight | `scripts/preflight-m5-026b-stopped-source-diagnostic.py` — `51a13baad2d3b55182c94ea68e7b4be2e2179a9d0368ae76d7f1ae41653e0478` |
| Destination diagnostic preflight | `scripts/preflight-m5-026b-stopped-destination-diagnostic.py` — `1df59238bd0226369049bee5ceae2da19a551495e1212135755710a68c012534` |

No production access, database mutation, credential read, backup, restore, or
container lifecycle operation occurred. Production migration and repository
activation remain `NO_GO`; backup/restore rehearsal remains unauthorized.
