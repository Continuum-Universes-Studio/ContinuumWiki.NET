# CW-M5-026B — Destination provisioning approval statement (draft)

**Review draft only.** This text records no approval unless the responsible
operator completes the blank fields and submits the statement. It does not
create or replace an authorization manifest, satisfy preflight, or authorize
execution by itself.

I, ______________________________, acting as ______________________________,
approve only the `ProvisionDestinationTarget` continuation operation for the
exact destination target below, contingent on successful source verification
and an immediate destination collision check. I confirm the operator is
______________________________ (`______________________________`) and the
target owner is ______________________________.

- Approval ID (new, unique, and different from the source-verification ID and
  original provisioning ID): `________________________________`
- ApprovedAtUtc (actual approval time, RFC 3339 UTC with `Z`):
  `________________________________`
- ExpiresAtUtc (after approval and no more than 24 hours later):
  `________________________________`
- Authorized host/account to be reverified: `christian-laptop`,
  `cdjensen94`, UID `1000`, rootless Podman required.
- Source-verification approval ID this authorization is paired with:
  `________________________________`

Exact permitted action: create only the fresh destination container
`continuumwiki-m5-026b-destination-ro1`, volume
`continuumwiki-m5-026b-destination-ro1-data`, database
`cw_m5_026b_destination_ro1`, identity
`continuumwiki_m5_026b_destination_app`, and loopback endpoint
`127.0.0.1:13327`, using the pinned MariaDB image
`docker.io/library/mariadb@sha256:6422478cb8e159f080fb1d8ccf65101e26fe51385787fde7d16c3b165a331f15`
and dedicated credential directory
`/home/cdjensen94/.local/state/continuumwiki/cw-m5-026b/destination-ro1`.
Credential values must not be read or logged. Sanitized evidence retention is
permitted.

This statement does not authorize source verification, migrations, backup,
restore, certification, or any other database operation. The live continuation
gate must verify source success and recheck destination absence before acting.

Production/shared access, rehearsal rights, cleanup, schema deletion, and
container/volume deletion are expressly excluded. Production migration and
repository activation remain `NO_GO`.

Operator submission/approval record reference: ______________________________
