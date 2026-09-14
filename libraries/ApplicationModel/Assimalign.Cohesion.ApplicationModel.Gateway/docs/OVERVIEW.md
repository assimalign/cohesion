# Application gateway overview

The gateway realizes an immutable application graph in dependency order, waits for each target's
readiness gate, applies its resource commands, and then admits dependents. Runtime stop preserves
owned declarations; teardown removes commands and realized resources in reverse order.

Database, ConfigurationStore, Rezolvr, IdentityHub and SecretStore command delivery uses `ApplicationGatewayOptions.CommandClients`.
Replace a kind's registration to use another `IGatewayResourceCommandClient`. In-process hosts
expose their registered control plane directly; remote commands use the peer gateway client.
Command outcomes are available through `IApplicationResourceStateManager.GetCommandObservations`
and exported with provider detail. Required rejection blocks dependent startup.

See [DESIGN.md](DESIGN.md) for dependency boundaries, ownership, credential restrictions, and
the scope of commands-only model replacement.

## Command trust grants (O25 / item 31c)

TrustedIssuer.AllowedCommandKinds is an immutable normalized ordinal list. The original
constructor remains unrestricted. Absent or empty means every kind is allowed; a nonempty list
permits exactly those case-sensitive wire kinds. `--mode trust-add --peer peer --from peer.json
--allow rezolvr.add-a-record,identityhub.add-audience` accepts repeated --allow arguments and
comma-separated values. --allow is rejected for trust-issue and other modes. The CLI forwards
--allow but still rejects --against, whose endpoint-selection contract remains item #982.

Development trusted-issuers.json and the SecretStore protected trust store both persist optional
allowedCommandKinds arrays. Old documents remain unrestricted. Restricted SecretStore grants use
{trustKey,allowedCommandKinds}; unrestricted grants retain the bare JWK protocol. Export returns
the array to the gateway. AddTrustedIssuerAsync accepts the new collection while preserving the
existing overload and its unrestricted behavior.

The serving gateway carries the matched issuer's list onto the authenticated principal and checks
both apply and delete. A forbidden kind returns the existing 409 Rejected observation with a detail
naming the issuer and kind. It does not dispatch the command or release existing ownership.
Manifest advertisement, command-token scope and owner-equals-issuer checks remain in force.

## Area command delivery

Default command clients now cover Database, ConfigurationStore, Rezolvr, IdentityHub and
SecretStore. Each thin adapter uses a Core-only area client and preserves refusal details.
SecretStore's empty successful trust response maps to Applied.

Before delivering secretstore.add-secret, parameter sources use the application's parameter
provider. Resource:key sources reuse the existing mount/store resolution path and require an
explicit target dependency. Failures return named Rejected details. Resolved bytes exist only in
the transient delivery envelope; model declarations, ids and the applied-declaration ledger retain
the original source reference. Literal secret sources are prohibited in the descriptor verb.

## HTTPS transport identities (31t)

Certificate mounts remain ordinary single-file Secret inputs. Explicit parameter or resource sources are authoritative and unusable bundles return a named Unresolved result. A source-free certificate mount first requests `certs/<resource>-<endpoint>` from the application's Running SecretStore, excluding that store itself. Before the store is ready or while its CA is pending, the gateway issues a P-256 development transport identity from `<state>/<application>/.state/certs/`. Root certificate and protected PKCS#8 key persist; leaf bundles cache by resource/endpoint with loopback and observed/declared host SANs. New bundles use leaf, intermediates if any, and one PKCS#8 key; readers preserve both existing producer orders and tolerate a supplied root.

Certificates-only transport anchors are carried in ResourceInputs, materialized beside bootstrap.token as a protected `.state/trust.pem`, and exposed through ResourceEnvironment.TrustBundlePath. Local and in-process probes use the shared CustomRootTrust validator while preserving hostname checks. Thin store clients accept a caller-owned transport so the gateway can supply the same trust policy. This transport root is distinct from the ES256 application trust-signing key.
