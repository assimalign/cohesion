# IdentityHub

IdentityHub is the L3 identity service platform intended to manage tenants, directories, applications, principals, credentials, sessions, token services, federation, and provisioning.

The application builder configures a minimal code-first OpenID Connect issuer with persisted ES256 keys, client credentials, a loopback Development device-authorization flow, and a Cohesion resource control plane. Production HTTPS consumes a gateway-materialized `tls` Secret mount; the self-signed fallback is Development-only.

## Projects

- `Assimalign.Cohesion.IdentityHub` defines the public area-root application and builder contracts alongside the existing identity domain contracts.
- `Assimalign.Cohesion.IdentityHub.ApplicationModel` supplies the typed manifest resource, singleton stateful planner, and default control-plane factory used by orchestration-enabled executables.
- `Assimalign.Cohesion.IdentityHub.Hosting` provides the concrete creation entry point, issuer endpoints, key persistence, control-plane integration, and host lifecycle.
- `Assimalign.Cohesion.IdentityHub.Models` retains the existing tenant-directory DTO shapes while linking principals and credentials to the canonical `Assimalign.Cohesion.IdentityModel` contracts.

## Layering and dependencies

As an L3 service platform, IdentityHub composes the L2 `Assimalign.Cohesion.Hosting` runtime, which is built on the L1 `Assimalign.Cohesion.Core` foundation. The root and Models projects consume the L1 `Assimalign.Cohesion.IdentityModel` contracts; Models also retains Core for its generated ULID identifiers. The Hosting project depends only on the area root and shared Hosting runtime. The AOT-safe ApplicationModel project references only shared ApplicationModel and Hosting.Resources seams and is guarded by COHAM001.

The IdentityHub SDK's orchestration defaults describe one private `https` endpoint with readiness and liveness probes, one `data` Volume, and a single-replica `StatefulSet`. The planner also preserves optional non-persistent inputs such as the Hosting convention's `tls` mount. Runtime `AddAudience` and `AddClient` composition ships now; the generated resource control plane advertises no command kinds until developer-experience item 31c adds their gateway-command counterparts.

## Project documentation

- [Root overview](./Assimalign.Cohesion.IdentityHub/docs/OVERVIEW.md)
- [Root design](./Assimalign.Cohesion.IdentityHub/docs/DESIGN.md)
- [ApplicationModel overview](./Assimalign.Cohesion.IdentityHub.ApplicationModel/docs/OVERVIEW.md)
- [ApplicationModel design](./Assimalign.Cohesion.IdentityHub.ApplicationModel/docs/DESIGN.md)
- [Hosting overview](./Assimalign.Cohesion.IdentityHub.Hosting/docs/OVERVIEW.md)
- [Hosting design](./Assimalign.Cohesion.IdentityHub.Hosting/docs/DESIGN.md)
- [Models overview](./Assimalign.Cohesion.IdentityHub.Models/docs/OVERVIEW.md)
- [Models design](./Assimalign.Cohesion.IdentityHub.Models/docs/DESIGN.md)
