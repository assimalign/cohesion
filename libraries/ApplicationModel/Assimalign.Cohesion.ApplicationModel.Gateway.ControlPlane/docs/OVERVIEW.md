# Gateway Control Plane overview

`Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane` turns the gateway lifecycle seams into
a small NativeAOT-safe HTTP/1 server and client. The server always exposes the same
`ApplicationExportDocument` that the gateway writes to `export.json`; there is no second model or
serializer.

The server is application-scoped. That keeps model, observed state, trusted issuers, command
observations, listener lifetime, and Local discovery metadata isolated when an
`IMultiModelApplicationGateway` owns more than one application.

The resolver client normalizes any configured gateway authority to
`/cohesion/v1/application`, sends an explicit Bearer credential, rejects credential-bearing
plaintext traffic except loopback HTTP, validates the export, and requires its
`trustKey` to match the expected trusted application.

See [DESIGN.md](DESIGN.md) for lifecycle, authentication, and command-dispatch details.

The authenticated client also applies and deletes owned declarations through the served command
routes. `GatewayControlPlane.Configure` adapts Database and ConfigurationStore gateway command
clients into the server's dispatcher seam. Both paths retain `Applied`/`Rejected` and provider
detail, and a failed deletion retains the key's ownership reservation.

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
