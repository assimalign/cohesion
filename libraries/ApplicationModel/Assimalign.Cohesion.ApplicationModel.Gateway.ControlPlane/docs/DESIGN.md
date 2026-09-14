# Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane — DESIGN

## Authority

This package implements design item 23a in `docs/DEVELOPER_EXPERIENCE_DESIGN.md`. That signed-off
document wins over older ApplicationModel notes that describe HTTP control-plane hosting as
future work.

## Boundaries

The assembly is NativeAOT-compatible and uses source-generated JSON. It composes
`Http.Connections`, `Connections.Tcp`, and `Web.Routing` directly and has no dependency on
`Assimalign.Cohesion.Web.Hosting` or a platform `*.Hosting` package. The reference to
`Hosting.Resources` is limited to the owner-approved `ResourceCommand` envelope used at the
dispatcher boundary.

## Lifecycle and publication

`ApplicationGatewayOptions.ControlPlane` is an additive factory seam. The guided gateway base
creates one server per active application after a successful reconcile has produced an export,
publishes that exact document, and stops every server during stop, uninstall, or rollback.
Reconcile replaces the in-memory export without replacing the listener.

The Local gateway asks its persisted port store for a stable loopback port. The server advertises
the actual bound `System.Uri` only after the initial export exists. Metadata is written atomically
as `<state-root>/<application>/control-plane.json` with `{ url, trustKey }`; `url` uses
`UriExtensions.ToEndpointString()`. Process exit performs a final best-effort withdrawal for
one-shot Apply invocations.

## Protocol and trust

All five `/cohesion/v1` routes are routed with `Web.Routing`. Export serialization delegates to
`ApplicationExportDocument.SaveAsync`. Resource reads query the application-scoped state manager
on every request and emit current lifecycle plus observed endpoint `System.Uri` values encoded
with `UriExtensions.ToEndpointString()`.

Authentication parses a compact JWT, selects an exact issuer from the application's current
`ITrustedIssuerProvider`, verifies the ES256 signature and key identifier, then separately checks
issuer, `cohesion-export` audience, typed identity/temporal claims, future issue time, and the
eight-hour profile ceiling. Missing credentials return `401`; supplied credentials that fail
trust or validation return `403`. Developer credentials remain read-only. Every command route
requires the gateway token-use claim issued to gateway-to-gateway clients. Command reads return
only the authenticated issuer's observations, and writes require the command owner to equal that
issuer.

Trust grants now carry an optional allowed-command-kinds list. The serving gateway enforces it
on apply and delete alongside the existing authenticated gateway-token and manifest validation.
An absent or empty list preserves unrestricted access; see the O25 contract below.

The HTTP resolver client implements `IAuthenticatedControlPlaneClient`. During gateway external
resolution, the base supplies an application-issued export token and the caller's trusted issuer
snapshot. A direct resolver instead uses the fixed-token factory. In either case, the client
validates the returned export and matches its public key to the trusted peer before the landed
manifest-hash/drift policy consumes it.

## Commands

PUT parses the `Hosting.Resources.ResourceCommand` wire envelope, checks that the resource
manifest declares the command kind, and resolves an `IResourceCommandDispatcher` by exact
resource kind. Dispatch uses the resource's live default control-plane endpoint. Each accepted
command remains visible to its owner through GET with `Applied` or `Rejected` status and detail.
The serving gateway supplies its current target-resource bootstrap credential to the dispatcher. Identical
PUT retries return the recorded result without redispatch, while id reuse for different content
and an already-applied key owned by another issuer are rejected. A missing dispatcher, unavailable
endpoint, undeclared command, or dispatcher exception is recorded as `Rejected`. DELETE applies
through the same dispatcher and removes the observation only after the area confirms deletion.
An observation whose apply never reserved ownership is removed with `204` without dispatching
an area deletion; a rejected deletion of an applied command retains its reservation.

Item 23b supplies the claiming client's PUT/DELETE operations and adapts the gateway's
`IGatewayResourceCommandClient` registrations into `IResourceCommandDispatcher` in
`GatewayControlPlane.Configure`. Local and peer-serving delivery therefore share one area-client
implementation. The existing five routes remain unchanged. Provider refusals preserve their
detail through the adapter and remote client. Ownership reservations are separate from the
last-operation observations, so a rejected deletion retains the applied key until the dispatcher
confirms removal.

The serving gateway continues forwarding its target bootstrap credential. ConfigurationStore's
landed owner-equals-issuer policy consequently refuses foreign-owned envelopes with that provider
credential. An owner-preserving delegation contract is required before claiming remote
ConfigurationStore mutation works against a real authenticated host; the boundary is not weakened
by the adapter. IdentityHub and SecretStore desired commands use the same issuer policy; an owner-preserving remote delegation contract remains separate work. Rezolvr uses the existing generic middleware owner policy.

## Command trust grants (O25 / item 31c)

TrustedIssuer.AllowedCommandKinds is an immutable normalized ordinal list. The original
constructor remains unrestricted. Absent or empty means every kind is allowed; a nonempty list
permits exactly those case-sensitive wire kinds. `--mode trust-add --peer peer --from peer.json
--allow rezolvr.add-a-record,identityhub.add-audience` accepts repeated --allow arguments and
comma-separated values. --allow is rejected for trust-issue and other modes. The CLI forwards
--allow but still rejects --against, whose endpoint-selection contract remains item #982.

Local trusted-issuers.json and the SecretStore protected trust store both persist optional
allowedCommandKinds arrays. Old documents remain unrestricted. Restricted SecretStore grants use
{trustKey,allowedCommandKinds}; unrestricted grants retain the bare JWK protocol. Export returns
the array to the gateway. AddTrustedIssuerAsync accepts the new collection while preserving the
existing overload and its unrestricted behavior.

The serving gateway carries the matched issuer's list onto the authenticated principal and checks
both apply and delete. A forbidden kind returns the existing 409 Rejected observation with a detail
naming the issuer and kind. It does not dispatch the command or release existing ownership.
Manifest advertisement, command-token scope and owner-equals-issuer checks remain in force.
