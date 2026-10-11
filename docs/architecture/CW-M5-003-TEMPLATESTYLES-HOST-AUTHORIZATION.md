# CW-M5-003 — TemplateStyles Host Authorization and End-to-End Security Certification

## Current classification

`BLOCKED`

The repository and existing application path were inspected, but this
execution environment has no exact disposable MySQL 8.4 connection, approval
record, or `CONTINUUMWIKI_M5_003_HOST_AUTHORIZED=1` authorization. No database
was opened, no identity was created, and no production connection fallback was
attempted.

This is a host-certification block, not a reported authorization failure.

## Actual application contract

The MariaDB startup path in `TightWiki/MySqlDevelopmentRuntime.cs` uses:

1. `AddMySqlIdentity(...)` with the MySQL Entity Framework Identity store;
2. `AddMySqlApplicationPersistence(...)` for the application repositories;
3. the normal `SignInManager<IdentityUser>` and `UserManager<IdentityUser>`;
4. `app.UseRouting()` followed by `app.UseAuthentication()` and
   `app.UseAuthorization()`; and
5. the normal MVC controller route.

`PageController.TemplateStyles` is intentionally `[AllowAnonymous]` so that
the asset endpoint can make its own uniform fail-closed decision. Each
request sets `nosniff`, `private, no-store`, and `Pragma: no-cache`, validates
the bounded Data Protection token, resolves the live source page, checks the
current viewer's `Read` permission through `TwSessionState`, re-resolves the
recorded historical revision, re-sanitizes the CSS, and compares the exact
source and sanitized identities. Invalid, stale, deleted, suppressed,
restricted, and unauthorized identities return the same `404` result.

The existing authorization model is therefore two-layered: ASP.NET Core
middleware authenticates ordinary protected routes, while TemplateStyles
performs a per-request source-page permission check because the stylesheet
action itself is anonymous at the MVC authorization attribute level.

## Evidence completed without host authorization

The existing M5-002 suite provides repository and disposable loopback-MVC
evidence for the actual `PageController.TemplateStyles` action:

- signed and malformed asset identities fail closed;
- historical source and sanitizer identities are revalidated;
- unauthorized and deleted sources return `404` before CSS resolution;
- output is sanitized CSS only;
- `Content-Type` is `text/css; charset=utf-8`;
- `X-Content-Type-Options` is `nosniff`;
- `Cache-Control` is `private, no-store`; and
- no global CSP relaxation or `style-src 'unsafe-inline'` was added.

These tests do not create persisted users, issue real Identity cookies, or
prove application-host permission revocation. They must not be promoted to
host certification.

No CSP middleware or `Content-Security-Policy` response header is represented
in the repository host configuration inspected here. The report therefore
separates the static absence of an unsafe-inline relaxation from browser-level
CSP enforcement, which remains unexecuted.

The M5-003B integration gate and guarded host test record the stop condition
and reject a connection unless all of the following are explicit:

- `CONTINUUMWIKI_RUN_MYSQL_TESTS=1`;
- `CONTINUUMWIKI_M5_003_HOST_AUTHORIZED=1`;
- `CONTINUUMWIKI_MYSQL_TEST_CONNECTION` is present and names the exact
  approved `cw_m5_003_...` database;
- `CONTINUUMWIKI_M5_003_TARGET_DATABASE` and
  `CONTINUUMWIKI_M5_003_TARGET_IDENTITY` match the approved target; and
- `CONTINUUMWIKI_M5_003_APPROVAL_PATH` names an approved CW-M4-005 Track B
  scope file.

When authorized, `scripts/run-m5-003b-certification.sh` starts a fresh
rootless Podman Oracle MySQL 8.4 container with an immutable image digest,
binds it only to loopback, creates one exact disposable database through the
container Unix socket, and grants the non-root application identity only that
schema. `MediaWikiTemplateStylesMySqlHostTests` initializes the existing
schema, imports a private fixture, creates persisted Identity users and
page-scoped permissions, and exercises the real cookie and MVC pipeline. Its
assertions cover authorized and unauthorized stylesheet access, `nosniff` and
private no-store headers, conditional requests before and after revocation,
logout/session invalidation, concurrent identity isolation, suppressed and
deleted source revisions, and revocation using the same session and old signed
URL. The operator script removes only its own labelled container in its
`EXIT`, `INT`, and `TERM` cleanup path; no host-side root connection or
production target is accepted.

## Operator procedure after authorization

Follow the hardened procedure in
`docs/operations/CW-M5-003A-DISPOSABLE-MYSQL-CERTIFICATION.md` and run:

```bash
mysql_image="${CW_M5_003B_MYSQL_IMAGE:?set to the operator-verified immutable docker.io/library/mysql@sha256:<64 hex> reference}"
scripts/run-m5-003b-certification.sh \
  --execute \
  --approval "$HOME/continuumwiki-validation/approvals/cw-m5-003b.json" \
  --image "$mysql_image"
```

The operator approval must authorize the exact target identity and synthetic
database writes/cleanup. The script generates credentials privately, performs
container-local setup, verifies the effective loopback binding and MySQL
identity, then exports the application connection only to the child test
process. Credentials must never be copied into a report, commit, or chat
transcript.

The full required verification commands are:

```bash
dotnet test TightWiki.Tests/TightWiki.Tests.csproj \
  -c Debug --no-restore -m:1

dotnet build TightWiki.Tests/TightWiki.Tests.csproj \
  -c Release --no-restore -m:1

python3 -m json.tool \
  artifacts/validation/mediawiki-templatestyles-host-authorization-m5-003b-v1.json

git diff --check
```

## Required host scenarios still unexecuted

The following remain `UNAVAILABLE` because the authorized disposable host was
not supplied in this execution:

- persisted anonymous, authorized, unauthorized, and revocable identities;
- real cookie issuance, validation, expiry, and logout;
- page and historical-revision authorization through the application host;
- reuse of the same old stylesheet URL after permission revocation;
- deleted and suppressed source-revision requests;
- conditional GET after authorization changes;
- concurrent authorized/unauthorized requests and request isolation; and
- browser-level CSP enforcement.

The existing permission cache key is user/page/permission scoped. The guarded
host test intentionally verifies the live revocation result rather than
assuming cache invalidation behavior. No cache behavior was changed merely
because this host gate is incomplete.

## Repository-only verification executed

The existing focused renderer/delivery command passed 53 tests with no
failures or skips. The focused host command passed the authorization gate and
skipped the guarded real-MySQL test because the explicit disposable variables
were absent. The full Debug and Release results are recorded in the JSON
artifact. These are repository and loopback results until the operator runs
the authorized host command.

## Security boundaries

No production database, MediaWiki instance, identity record, Lua worker,
Cargo provider, systemd unit, bubblewrap restriction, or SELinux policy was
accessed or modified. The rejecting Lua executor and disabled production Lua
configuration remain unchanged. No raw CSS, cookies, credentials, session
tokens, or private source data are included in the validation artifact.

