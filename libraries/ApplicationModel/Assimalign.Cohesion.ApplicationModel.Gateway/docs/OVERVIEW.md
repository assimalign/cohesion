# Application gateway overview

The gateway realizes an immutable application graph in dependency order, waits for each target's
readiness gate, applies its resource commands, and then admits dependents. Runtime stop preserves
owned declarations; teardown removes commands and realized resources in reverse order.

Configure local supervision with `LocalGatewayOptions.LivenessFailureThreshold` (positive,
default 3) and `MaximumRestartAttempts` (nonnegative, default 5; zero disables restarts). Both
properties throw `ArgumentOutOfRangeException` on invalid assignment and preserve their previous
value, including after gateway construction. Valid later assignments remain supported. This
changes 10.0.0-preview.1 behavior, where only gateway construction checked these limits.

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

Mount-source syntax is shared with manifest and provider validation through `ResourceMountSource`:
`literal:` may be empty or contain colons, `parameter:` needs a nonblank name, and other sources
need exactly one colon between nonblank source and key. Probe and control-plane path validation
uses `ResourceHttpPath`: paths begin with `/` and contain no query or fragment. These manifest DTO
properties retain their string types for the owner's Database freeze (#1168), with typed DTO
adoption deferred to #1470. Manifest validation checks those strings through the value objects'
`IsValid`/`TryCreate` rules and rejects invalid text with `InvalidDataException` before reconcile;
plan `MountBinding.Source` remains a value type.

Every credential the gateway mints — each resource's bootstrap credential, its own calls to resource
control planes and stores, telemetry-emitter credentials, peer-gateway calls, and developer tokens —
goes through the application's registered `IApplicationCredentialIssuer` first and falls back to the
default ES256 application-key issuer, whose tokens are unchanged. Resources that must accept an
identity provider's credentials register a matching verifier with
`ResourceRuntime.RegisterCredentialVerifier`.

Issuer results use `ApplicationCredentialScheme` and `ApplicationCredentialValue`. String inputs
still convert when constructing a credential, but schemes must be HTTP tokens and values must be
nonblank without CR or LF. Invalid replacements are also refused in `with` expressions. Credential
value displays are redacted; custom transports read `credential.Value.Value` explicitly to obtain
the secret. Bearer-only carriers continue to refuse another valid authentication scheme.

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

## Deployment records and management (P2, P3)

A gateway with an `IResourceRecordStore` (`RecordStore`; Local and in-process use
`FileResourceRecordStore` under their state root, and the Docker and Kubernetes gateways bring their
own) delivers each realized resource's deployment record. Before the resource starts, the gateway
writes the deployment layer from the plan's
`Configuration` (what `Program.cs` declared with `Configure(...)`), names the record directory in
the resource's inputs, and keys the record to a generation: a hash of the plan and the build it
deploys (`DescribeArtifact`). Re-applying the same generation keeps management overrides; a new
generation drops them and reports which sections reverted, on standard error and as a telemetry
event. Claims survive both. A gateway without a store refuses deployment configuration at
`Build()`.

The gateway also implements `IResourceManagementProvider`, which the control plane serves as
`/cohesion/v2/resources/{name}/…`: configuration with each value's provenance, override changes
validated against the resource's runtime surface, convergence status read from every instance,
claims, actions fanned out to every instance when declared so, and platform surfaces. A platform
gateway publishes its surfaces with `GetPlatformSurfaces` and puts an operator's override in force
with `ApplyPlatformSurfaceAsync`; the base stores the override with the record for the current
generation, and `OnRecordChangedAsync` lets the platform deliver a record change its instances
cannot read for themselves. Each write is conditional on the record version (`ETag`/`If-Match`)
and posted to the application's telemetry sink as an event that names the operation, never the
value. Wire contract:
[runtime contract](../../../../docs/RUNTIME_CONTRACT.md#deployment-record); routes and grants:
[control plane design](../../Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane/docs/DESIGN.md).
