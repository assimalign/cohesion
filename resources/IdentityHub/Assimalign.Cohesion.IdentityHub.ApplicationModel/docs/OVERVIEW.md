# Assimalign.Cohesion.IdentityHub.ApplicationModel — Overview

This package supplies the manifest-backed `IdentityHubResource`, typed
`IdentityHubResourceOptions`, `AddIdentityHub(...)`, the IdentityHub planner, and the
default `IdentityHubResourceControlPlane` factory.

The planner requires a singleton `StatefulSet`, an HTTPS-over-TCP endpoint named `https`,
and exactly one persistent, sized `data` Volume. The SDK defaults that endpoint to private
HTTPS and supplies readiness and liveness probes. Additional endpoints and non-persistent
Configuration or Secret mounts pass through unchanged, including a gateway-resolved `tls`
mount. The planner emits only `cohesion/plan/v1` records; platform compilers realize the
workload later.

## Dependencies

- `Assimalign.Cohesion.ApplicationModel`
- `Assimalign.Cohesion.Hosting.Resources`

COHAM001 guards the full production dependency closure. The package never references
IdentityHub.Hosting, a gateway assembly, or a platform SDK.

The runtime `AddAudience` and `AddClient` verbs ship in the area root. The default control
plane advertises identityhub.add-audience and identityhub.add-client for the typed descriptor command counterparts.

## Links

- [Design](DESIGN.md)
- [Public API](Assembly/Assimalign.Cohesion.IdentityHub.ApplicationModel/OVERVIEW.md)

## Commands

| Wire kind | Descriptor verb | Ownership key |
|---|---|---|
| `identityhub.add-audience` | `AddAudience` | audience name |
| `identityhub.add-client` | `AddClient` | client id |

The typed IIdentityHubResourceDescriptor retains its command surface through DependsOn chaining.
Audience and client commands are registered on the default control plane and served by the existing
authenticated API endpoint, with POST/DELETE and JSON refusal observations. Owner must equal the
authenticated application issuer; transport and bootstrap-token verification are unchanged.

The command registry is written atomically to `registry.json` beside IdentitySigningKey under the
resource data path. Startup restores the registry and control-plane ownership before serving.
Token issuance reads the live combined registry of builder-declared and command-declared clients;
discovery and JWKS remain the same issuer surface. Audience removal is rejected while a client uses it.

Client credentialSource names a resource Secret mount, optionally prefixed with `mount:`. The
declaration stores the mount name, never credential bytes. Hosting reads and hashes the mounted
credential when initializing or updating the registry, so restart requires the mount again.
Clients must reference existing audiences; add the audience before the client. Conflicting resource
seeds or changed client declarations are rejected until the owning declaration is deleted.
