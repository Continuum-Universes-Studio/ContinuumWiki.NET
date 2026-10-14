# CW-M5-026B — Source identity verification approval statement (draft)

**Review draft only.** This text records no approval unless the responsible
operator completes the blank fields and submits the statement. It does not
create or replace an authorization manifest, satisfy preflight, or authorize
execution by itself.

I, ______________________________, acting as ______________________________,
approve only the `VerifyRetainedSourceIdentity` continuation operation for the
exact retained source target below. I confirm the operator is
______________________________ (`______________________________`) and the
target owner is ______________________________.

- Approval ID (new and unique; not the original provisioning ID):
  `________________________________`
- ApprovedAtUtc (actual approval time, RFC 3339 UTC with `Z`):
  `________________________________`
- ExpiresAtUtc (after approval and no more than 24 hours later):
  `________________________________`
- Authorized host/account to be reverified: `christian-laptop`,
  `cdjensen94`, UID `1000`, rootless Podman required.

Exact permitted action: read-only checks of source container
`continuumwiki-m5-026b-source-ro1` (ID
`6a4d4f8707ee4f5b14dddfff183db6d26ce7453eaba377b7c9d081bc1157f914`), volume
`continuumwiki-m5-026b-source-ro1-data`, pinned MariaDB image
`docker.io/library/mariadb@sha256:6422478cb8e159f080fb1d8ccf65101e26fe51385787fde7d16c3b165a331f15`,
database `cw_m5_026b_source_ro1`, identity
`continuumwiki_m5_026b_source_app`, and loopback endpoint `127.0.0.1:13326`,
followed by exactly one query:

```sql
SELECT DATABASE(), VERSION(), @@version_comment;
```

Credential values must not be read or logged. Sanitized evidence retention is
permitted. Destination provisioning is not authorized by this statement.

Production/shared access, rehearsal rights, backup, restore, migrations,
certification, cleanup, schema deletion, and container/volume deletion are
expressly excluded. Production migration and repository activation remain
`NO_GO`.

Operator submission/approval record reference: ______________________________
