# CW-M5-026B — Source identity verification approval statement (proposal)

**Non-authorizing proposal.** The proposed ID below is not issued until the
operator confirms it is unused and submits an actual approval. Do not copy this
statement into an authoritative authorization manifest.

Proposed approval wording for Christian Daniel Jensen, acting as Project
Owner: approve only the `VerifyRetainedSourceIdentity` operation for the exact
retained source target listed below. The proposed operator is Christian Daniel
Jensen, System Administrator, and the proposed target owner is Christian
Daniel Jensen. This approval has not been given by this draft.

- Proposed approval ID: `CW-M5-026B-SOURCE-20261012-002`
- Operator must confirm the proposed ID has not previously been issued or
  consumed outside the repository evidence.
- `ApprovedAtUtc`: **operator must enter the actual UTC approval time**.
- `ExpiresAtUtc`: **operator must set no later than 24 hours after approval**.
- Host binding: `christian-laptop`, Unix user `cdjensen94`, UID `1000`,
  rootless Podman required; current host/runtime must be checked by the runner.

Authorized source identity:

- Container: `continuumwiki-m5-026b-source-ro1`
- Container ID: `6a4d4f8707ee4f5b14dddfff183db6d26ce7453eaba377b7c9d081bc1157f914`
- Volume: `continuumwiki-m5-026b-source-ro1-data`
- Database: `cw_m5_026b_source_ro1`
- Database identity: `continuumwiki_m5_026b_source_app`
- Loopback endpoint: `127.0.0.1:13326`
- Immutable image: `docker.io/library/mariadb@sha256:6422478cb8e159f080fb1d8ccf65101e26fe51385787fde7d16c3b165a331f15`
- Credential directory metadata may be checked at
  `/home/cdjensen94/.local/state/continuumwiki/cw-m5-026b/source-ro1`; credential
  values must not be read or recorded.
- The only SQL authorized, once, after exact host/container/image/volume/
  loopback and credential-metadata checks:

  ```sql
  SELECT DATABASE(), VERSION(), @@version_comment;
  ```

The runner must select `cw_m5_026b_source_ro1` explicitly with MariaDB `-D`.
Database-name equality, the MariaDB `11.8.` version family, and the existing
MariaDB version-comment predicate remain mandatory. Sanitized predicate
diagnostics may be retained; raw SQL output and stderr may not.

This statement does not authorize destination provisioning. Production or
shared access, rehearsal, backup, restore, migrations, certification, cleanup,
schema deletion, and container/volume deletion are prohibited. Production
migration and repository activation remain `NO_GO`.
