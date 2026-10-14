# CW-M5-026B — Read-Only Host Inspection and Target Proposal

## Classification

`APPROVAL_RECORDED_MANIFEST_INCOMPLETE`

Inspection substatus: `READONLY_INSPECTION_PARTIAL_ENVIRONMENT_BLOCKED`.

This inspection used only local, nonmutating commands. It did not create an
authoritative manifest, contact MariaDB, read credentials, pull an image,
create or change a Podman resource, or run provisioning, migration, backup,
restore, certification, or cleanup.

## Verified read-only facts

| Fact | Result | Evidence / limitation |
| --- | --- | --- |
| Hostname | `christian-laptop` | `hostname`; verified locally |
| Invoking UID | `1000` | `id -u`; verified locally |
| Invoking user | `cdjensen94` | `id -un`; verified locally |
| TCP listeners visible through procfs | None returned | `/proc/net/tcp` and `/proc/net/tcp6`; this is not a reservation or future availability guarantee |
| Authoritative manifest | Absent | No `secure/evidence/cw-m5-026b-provisioning-authorization.json` found |
| Rootless Podman status | `VERIFIED_OPERATOR_EVIDENCE` | Operator supplied Podman 6.1.2 rootless under UID 1000 |
| Podman version | `VERIFIED_OPERATOR_EVIDENCE` | Operator supplied version `6.1.2` |
| Existing containers and volumes | `PARTIAL` | Exact proposed volume checks returned `NOT_FOUND`; complete inventory remains unavailable |
| Local MariaDB image/digest | `VERIFIED_OPERATOR_EVIDENCE` | `docker.io/library/mariadb@sha256:6422478cb8e159f080fb1d8ccf65101e26fe51385787fde7d16c3b165a331f15`; operator supplied local MariaDB 11.8.x evidence; no image was pulled |

Podman reported a stale pause PID and an internal cleanup attempt during its
failed initialization. The inspection did not issue a mutating Podman
command; the pause marker remained present when checked afterward. No
resource state was obtained and no successful infrastructure mutation was
observed.

## Proposed targets — not created, not reserved, not approved

These proposals satisfy the repository naming patterns and avoid the known
historical M5-019, M5-025, and operator4 naming families. They are not proven
collision-free because Podman state could not be read, and the ports must be
revalidated immediately before any separately authorized provisioning.

### Proposed source

```text
ContainerName:      continuumwiki-m5-026b-source-ro1
VolumeName:         continuumwiki-m5-026b-source-ro1-data
DatabaseName:       cw_m5_026b_source_ro1
Endpoint:           127.0.0.1:13326
DatabaseIdentity:   continuumwiki_m5_026b_source_app
CredentialDirectory:/home/cdjensen94/.local/state/continuumwiki/cw-m5-026b/source-ro1
IdentityPrefix:     milestone=CW-M5-026B;role=source;container=continuumwiki-m5-026b-source-ro1;volume=continuumwiki-m5-026b-source-ro1-data;database=cw_m5_026b_source_ro1;endpoint=127.0.0.1:13326;
Status:             PROPOSED_NOT_CREATED
```

### Proposed destination

```text
ContainerName:      continuumwiki-m5-026b-destination-ro1
VolumeName:         continuumwiki-m5-026b-destination-ro1-data
DatabaseName:       cw_m5_026b_destination_ro1
Endpoint:           127.0.0.1:13327
DatabaseIdentity:   continuumwiki_m5_026b_destination_app
CredentialDirectory:/home/cdjensen94/.local/state/continuumwiki/cw-m5-026b/destination-ro1
IdentityPrefix:     milestone=CW-M5-026B;role=destination;container=continuumwiki-m5-026b-destination-ro1;volume=continuumwiki-m5-026b-destination-ro1-data;database=cw_m5_026b_destination_ro1;endpoint=127.0.0.1:13327;
Status:             PROPOSED_NOT_CREATED
```

The exact proposed volume names were checked read-only and returned
`NOT_FOUND` at inspection time. No volume is reserved by that observation.
No listener was observed on ports `13326` or `13327` at inspection time; the
ports were not reserved and must be revalidated immediately before any live
operation. The proposed credential directories do not exist, are outside the
repository, and were not created or read; their parent path is also absent.

## Decision register

### Verified without authorization

- Repository schema and preflight require the exact v1 manifest contract,
  immutable MariaDB digest, loopback endpoints, distinct role-scoped targets,
  dedicated credentials, and explicit false cleanup/deletion permissions.
- The current source preparation and approval state remain separate from
  rehearsal authorization.
- Hostname and invoking UID/user are locally observed.

### Proposed and awaiting explicit target approval

- The complete source and destination values listed above.
- The proposed host port range `13326`–`13327`.
- Use of the proposed credential-directory paths.

### Still operator-approval-required or independently unresolved

- Approver, operator, target owner, roles, approval ID, and actual finite UTC
  validity timestamps.
- Whether this host and the proposed image/targets are authorized.
- Complete collision checks against existing containers, volumes, images,
  databases, ports, and credential directories.
- Final exclusions and the complete permitted/prohibited permission object.

## Exact inspection commands and outcomes

The following read-only commands were run:

```text
hostname
id -u
id -un
podman version --format '{{.Client.Version}}'
podman info --format 'rootless={{.Host.Security.Rootless}}'
podman ps -a --format '{{.Names}}\t{{.Image}}\t{{.Ports}}'
podman volume ls --format '{{.Name}}'
podman images --digests --format '{{.Repository}}\t{{.Tag}}\t{{.Digest}}'
ss -ltn
```

The broad Podman inventory commands previously failed before returning state
because the execution environment could not modify/read its rootless runtime
directory. The exact `podman volume exists` checks for the two proposed
volumes later returned `NOT_FOUND`. `ss` returned no matching listener for
the proposed ports; the result is not a reservation. Procfs listener checks
remain only a limited fallback.

## Required next decision

The operator must explicitly approve or replace the proposed source and
destination values and provide the missing manifest facts. Only then may the
authoritative manifest be created at the designated private path and passed
to `scripts/preflight-m5-026b-provisioning.sh`. A successful preflight is
required before the documented live command with
`--provision --confirm-provisioning`.

`RehearsalRightsGranted=false`; backup, restore, migrations, certification,
cleanup, schema deletion, and container/volume deletion remain prohibited.
Production migration and repository activation remain `NO_GO`.
