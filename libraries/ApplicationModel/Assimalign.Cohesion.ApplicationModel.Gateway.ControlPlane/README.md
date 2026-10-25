# Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane

This package is the hosting-free HTTP control plane shared by Cohesion gateways. It serves the
gateway's canonical `ApplicationExportDocument`, live resource observations, and desired resource
commands and resource management endpoints over `Http.Connections` + TCP, with an internal route matcher. It
references nothing under `resources/**`, so it uses no Web package (not even `Web.Routing`) and no
platform Hosting package.

`GatewayControlPlane.Configure(options, runMode)` installs the resolver client for every gateway
mode and installs the server factory for `Run` and `Apply`. The Gateway SDK emits that call before
constructing the selected gateway. Manual compositions can assign
`ApplicationGatewayOptions.ControlPlane = GatewayControlPlane.CreateFactory(...)` directly.

The authenticated surface is:

- `GET /cohesion/v1/application`
- `GET /cohesion/v1/resources/{name}`
- `GET /cohesion/v1/resources/{name}/commands`
- `PUT /cohesion/v1/resources/{name}/commands/{id}`
- `DELETE /cohesion/v1/resources/{name}/commands/{id}`
- `GET /cohesion/v2/resources/{name}/state-contracts` and `/instances`
- `GET`, `PUT`, `DELETE` at `/cohesion/v2/resources/{name}/state/{state}/{key}`
- `GET /cohesion/v2/resources/{name}/operations/{id}`

Every request requires an ES256 Bearer token with audience `cohesion-export`. Verification uses
the served application's live `ITrustedIssuerProvider` snapshot. A missing credential is `401`;
an invalid or untrusted credential is `403`. Developer tokens are read-only. Every command route
requires the signed gateway token-use claim; GET returns only that issuer's observations, and PUT
and DELETE bind the command owner to the authenticated issuer.

The Local gateway persists its loopback port in the normal local port store. Once the first export
is publishable, the server atomically writes `.cohesion/<application>/control-plane.json` with the
canonical endpoint string and public trust JWK. Gateway stop, rollback, and uninstall withdraw it.

`GatewayControlPlane.CreateClient(token, trustedIssuer)` supports direct application-set and
resolver use. Gateway-owned external resolution uses the parameterless client and supplies a
short-lived application credential plus the caller's trusted peer snapshot for export-key
verification.

`Configure` turns the gateway's `CommandClients` into `IResourceCommandDispatcher` registrations.
By default that is the generic `ResourceControlPlaneCommandClient` (`AnyKind`). A command uses the
dispatcher for its exact manifest kind, and otherwise the catch-all. The serving gateway passes
the target's current resource-scoped `ResourceAccess` credential to the dispatcher. Callers are
authenticated by the built-in ES256 trusted-issuer check and then by the application's registered
`IApplicationCallerAuthenticator`s; authorization applies to the mapped caller. Identical PUT retries
are idempotent, and a command key already applied for another owner is rejected. A factory with
neither a matching nor a catch-all dispatcher still records commands for that kind as `Rejected`
with a named reason; they are never silently discarded.

State management requires explicit per-resource read/write grants in AllowedManagementOperations. Conditional writes and UUID retry identities are durably accepted before background reconciliation. The gateway supplies trusted instance targets, credentials and transport trust; it reports dated fleet convergence. Command grants do not imply management access. See [resource state management](../../../docs/libraries/ApplicationModel/RESOURCE_MANAGEMENT.md).