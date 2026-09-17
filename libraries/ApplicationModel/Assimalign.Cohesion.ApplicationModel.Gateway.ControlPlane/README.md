# Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane

This package is the hosting-free HTTP control plane shared by Cohesion gateways. It serves the
gateway's canonical `ApplicationExportDocument`, live resource observations, and desired resource
commands over `Http.Connections` + TCP + `Web.Routing`; it does not reference a Web or platform
Hosting package.

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

Area packages register `IResourceCommandDispatcher` implementations when their protocol clients
land. The serving gateway passes the target's current resource-scoped bootstrap credential to the
dispatcher. Identical PUT retries are idempotent, and a command key already applied for another
owner is rejected. Until a dispatcher lands, commands for that resource kind remain queryable as
`Rejected` with a named reason; they are never silently discarded.
