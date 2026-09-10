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
plane advertises no commands until item 31c adds their gateway-command counterparts.

## Links

- [Design](DESIGN.md)
- [Public API](Assembly/Assimalign.Cohesion.IdentityHub.ApplicationModel/OVERVIEW.md)
