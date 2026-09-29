# Application gateway overview

The gateway realizes an immutable application graph in dependency order, waits for each target's
readiness gate, applies its resource commands, and then admits dependents. Runtime stop preserves
owned declarations; teardown removes commands and realized resources in reverse order.

Out-of-process command delivery uses `ApplicationGatewayOptions.CommandClients`, which by default
holds one `ResourceControlPlaneCommandClient`. That client serves every resource kind
(`IGatewayResourceCommandClient.AnyKind`) over the standard control-plane `commands` route.
To customize one kind, add an `IGatewayResourceCommandClient` whose `ResourceKind` is that manifest
kind. An exact-kind client always wins over the catch-all. In-process hosts expose their
registered control plane directly; remote commands use the peer gateway client.
Custom clients must accept the `serverCertificateValidator` parameter on apply and delete and
use it for the target application's TLS trust; `null` preserves platform default trust.
Command outcomes are available through `IApplicationResourceStateManager.GetCommandObservations`
and exported with provider detail. Required rejection blocks dependent startup.

Stores, source-free endpoint certificates, trusted issuers, command-payload rewriting, and the
telemetry sink reach the gateway only through the providers the application registers in
`builder.Providers` and `Build()` freezes into `IApplicationModel.Providers`; this package
references no store client and knows no area kind. Unregistered, a `<source>:<key>` mount fails
`Build()`, as does a command whose target manifest requires an input resolver (SecretStore's
`secretstore.add-secret`); the development certificate authority and local trusted-issuers file are
Local-only, and no telemetry is injected. A model's registrations serve only its own application's resources.
A model imported from a document carries none and never inherits them: an application set registers
each member's providers explicitly in `AddApplication(Applications.X, member => member.UseSecretStore("secrets"))`,
attaches them to that member's model alone, and fails a store-backed member without one at start,
naming the member. A model reaching the gateway without the registration a mount needs gets an
unresolved input naming it, and a Local `--realize` closure's mounts are unresolved with the
cross-application limit named. Cross-application store sources are rejected until a peer-credential
design lands.

Every credential the gateway mints — each resource's bootstrap credential, its own calls to resource
control planes and stores, telemetry-emitter credentials, peer-gateway calls, and developer tokens —
goes through the application's registered `IApplicationCredentialIssuer` first and falls back to the
default ES256 application-key issuer, whose tokens are unchanged. Resources that must accept an
identity provider's credentials register a matching verifier with
`ResourceRuntime.RegisterCredentialVerifier`.

See [DESIGN.md](DESIGN.md) for dependency boundaries, ownership, credential restrictions, the
provider registrations, the identity seams, and the scope of commands-only model replacement.

## Command trust grants (O25 / item 31c)

TrustedIssuer.AllowedCommandKinds is an immutable normalized ordinal list. The original
constructor remains unrestricted. Absent or empty means every kind is allowed; a nonempty list
permits exactly those case-sensitive wire kinds. `--mode trust-add --peer peer --from peer.json
--allow rezolvr.add-a-record,identityhub.add-audience` accepts repeated --allow arguments and
comma-separated values. --allow is rejected for trust-issue and other modes. The CLI forwards
--allow but still rejects --against, whose endpoint-selection contract remains item #982.

Local trusted-issuers.json and a registered trust store both persist optional
allowedCommandKinds arrays; the gateway hands the store a `TrustedIssuer` that carries the list.
Old documents remain unrestricted. The SecretStore orchestration store writes restricted grants as
{trustKey,allowedCommandKinds}; unrestricted grants retain the bare JWK protocol. Its export returns
the array to the gateway. AddTrustedIssuerAsync accepts the new collection while preserving the
existing overload and its unrestricted behavior.

The serving gateway carries the matched issuer's list onto the authenticated principal and checks
both apply and delete. A forbidden kind returns the existing 409 Rejected observation with a detail
naming the issuer and kind. It does not dispatch the command or release existing ownership.
Manifest advertisement, command-token scope and owner-equals-issuer checks remain in force.

## Command delivery

`ResourceControlPlaneCommandClient` replaced the five internal per-area adapters (Database,
ConfigurationStore, IdentityHub, Rezolvr, SecretStore), so this package no longer references the
Database, IdentityHub or Rezolvr client packages. For the same address it sends the method, URL
and body they sent:

- `POST` or `DELETE` to `<control-plane address>/commands`.
- A bearer credential and a JSON body `{id,kind,owner,key,payload(base64)}`, with no `Accept`
  header.

It maps responses as they did. Success is Applied, and a JSON `{status,detail}` body can override
it. A refusal is Rejected with the body's detail, or otherwise the neutral
`Command '<kind>' was refused: HTTP <code> <reason>.` SecretStore's empty successful trust response
still maps to Applied. Kinds that had no adapter, such as Web, now receive commands through the
same client instead of being rejected for lack of one.

The client applies the supplied validator through a per-call transport that it disposes.
`IResourceTransportTrustProvider` supplies the same application trust to hosted control planes.

Before an apply, a command-input resolver the application registered for the command's kind
(`ApplicationProviders.CommandInputs`, such as the SecretStore orchestration package's
`secretstore.add-secret` resolver) rewrites the payload for delivery. When the target's manifest
marks the kind `requiresInputResolver`, as SecretStore's manifest marks `secretstore.add-secret`, a
missing resolver fails `Build()` (or the application set, for a member) with a message naming the
package and verb, so a validated model never delivers it unresolved. Any other kind with none
registered is delivered as declared; the store's rejection of an unresolved `secretstore.add-secret`
remains as defense in depth. The resolver resolves `parameter:` and `<source>:<key>`
expressions through the gateway's `IResourceSourceResolver`, over the same `Sources` registrations as
mounts and with the same explicit-target-dependency rule. Refusals become named Rejected details.
Resolved bytes exist only in the transient delivery envelope; model declarations, ids and the
applied-declaration ledger retain the original source reference. Literal secret sources are
prohibited in the descriptor verb.

## HTTPS transport identities (31t)

Certificate mounts remain ordinary single-file Secret inputs. Explicit `parameter:` or `<source>:<key>` sources are authoritative and unusable bundles return a named Unresolved result. A source-free certificate mount gets its leaf from the registered certificate authority (`ApplicationProviders.CertificateAuthority`, for example `UseSecretStore(store).AsCertificateAuthority()`) once its resource is Running. The authority resource's own leaf always comes from the gateway development authority. Without a registered authority only Local uses the development authority; every other environment fails loudly, naming the endpoint and the registration to add, and a registered authority that cannot issue falls back to the development authority in Local only. The development authority persists a P-256 root and protected PKCS#8 key under `<state>/<application>/.state/certs/`; leaf bundles cache by resource/endpoint with loopback and observed/declared host SANs. New bundles use leaf, intermediates if any, and one PKCS#8 key; readers preserve both existing producer orders and tolerate a supplied root.

Certificates-only transport anchors are carried in ResourceInputs, materialized beside bootstrap.token as a protected `.state/trust.pem`, and exposed through AppEnvironment.Variables.TrustBundlePath. Local and in-process probes use the shared CustomRootTrust validator while preserving hostname checks. Providers receive the same trust policy as `ResourceProviderConnection.ServerCertificateValidator`. This transport root is distinct from the ES256 application trust-signing key.
