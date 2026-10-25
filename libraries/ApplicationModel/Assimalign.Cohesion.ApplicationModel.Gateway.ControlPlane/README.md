# Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane

This package is the hosting-free HTTP control plane shared by Cohesion gateways. It serves the
gateway's canonical `ApplicationExportDocument`, live resource observations, and desired resource
commands over `Http.Connections` + TCP, and the deployment record's management API, with an
internal matcher for its fixed routes. It
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
- `GET /cohesion/v2/resources/{name}/configuration` and `…/configuration/status`
- `PATCH`, `PUT`, `DELETE /cohesion/v2/resources/{name}/configuration/{section}`
- `PUT /cohesion/v2/resources/{name}/claims/{owner}`
- `POST /cohesion/v2/resources/{name}/actions/{action}`
- `GET /cohesion/v2/resources/{name}/instances`
- `GET`, `PATCH /cohesion/v2/resources/{name}/platform/{platform}/{extension}`

Every request requires an ES256 Bearer token with audience `cohesion-export`. Verification uses
the served application's live `ITrustedIssuerProvider` snapshot. A missing credential is `401`;
an invalid or untrusted credential is `403`. Developer tokens cannot send commands. Every command
route requires the signed gateway token-use claim; GET returns only that issuer's observations, and
PUT and DELETE bind the command owner to the authenticated issuer.

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

The `/cohesion/v2` routes are the management API over each resource's deployment record. The
server authorizes them against `AllowedManagementOperations`, a grant list trusted issuers carry
beside their command kinds (`ResourceManagementOperations` names the grants), and hands the
operation to the gateway's `IResourceManagementProvider`. An empty list grants none, command grants
never imply it, and the application's own trust key grants all of them, so the developer tokens its
gateway issues can manage its resources. A caller writes claims only for its own application.
Routes, status mapping and `ETag`/`If-Match` handling are in [docs/DESIGN.md](docs/DESIGN.md).
Resource documents name the resource's `resourceGroup` when it has one.
