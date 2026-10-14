# CW-M5-026B — Destination provisioning approval statement (proposal)

**Non-authorizing proposal.** The proposed ID below is not issued until the
operator confirms it is unused and submits an actual approval. Do not copy this
statement into an authoritative authorization manifest.

Proposed approval wording for Christian Daniel Jensen, acting as Project
Owner: approve only the `ProvisionDestinationTarget` operation, conditional
on successful `VerifyRetainedSourceIdentity` under its separate current
authorization. The proposed operator is Christian Daniel Jensen, System
Administrator, and the proposed target owner is Christian Daniel Jensen.
This approval has not been given by this draft.

- Proposed approval ID: `CW-M5-026B-DESTINATION-20261012-002`
- Paired source-verification approval ID: `CW-M5-026B-SOURCE-20261012-002`
- Operator must confirm both proposed IDs are unused and distinct from prior
  approvals.
- `ApprovedAtUtc`: **operator must enter the actual UTC approval time**.
- `ExpiresAtUtc`: **operator must set no later than 24 hours after approval**.
- Host binding: `christian-laptop`, Unix user `cdjensen94`, UID `1000`,
  rootless Podman required; current host/runtime must be checked by the runner.

Authorized destination identity:

- Container: `continuumwiki-m5-026b-destination-ro1`
- Volume: `continuumwiki-m5-026b-destination-ro1-data`
- Database: `cw_m5_026b_destination_ro1`
- Database identity: `continuumwiki_m5_026b_destination_app`
- Loopback endpoint: `127.0.0.1:13327`
- Immutable image: `docker.io/library/mariadb@sha256:6422478cb8e159f080fb1d8ccf65101e26fe51385787fde7d16c3b165a331f15`
- Dedicated credential directory: `/home/cdjensen94/.local/state/continuumwiki/cw-m5-026b/destination-ro1`.
  Credential values must not be read or recorded.

Provisioning is allowed only after Authorization A succeeds and the runner
rechecks that the exact destination container, volume, endpoint, and credential
directory are absent/available. This statement grants no source-query
permission and no database migration, backup, restore, or certification scope.

Production or shared access, rehearsal, cleanup, schema deletion, and
container/volume deletion are prohibited. Production migration and repository
activation remain `NO_GO`.
