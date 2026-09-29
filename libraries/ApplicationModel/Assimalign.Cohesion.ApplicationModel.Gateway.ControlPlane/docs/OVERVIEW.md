# Gateway Control Plane overview

`Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane` turns the gateway lifecycle seams into
a small NativeAOT-safe HTTP/1 server and client. The server always exposes the same
`ApplicationExportDocument` that the gateway writes to `export.json`; there is no second model or
serializer.

The server is application-scoped. That keeps model, observed state, trusted issuers, command
observations, listener lifetime, and Local discovery metadata isolated when an
`IMultiModelApplicationGateway` owns more than one application.

Requests are matched by a small internal router for the five fixed `/cohesion/v1` templates.
The package used to route with `Web.Routing` and dropped it because a library may not reference a
`resources/**` package. Matching, `404`/`405` handling, `Allow` values, and route values did not
change. DESIGN.md lists the exact rules.

The resolver client normalizes any configured gateway authority to
`/cohesion/v1/application`, sends an explicit Bearer credential, rejects credential-bearing
plaintext traffic except loopback HTTP, validates the export, and requires its
`trustKey` to match the expected trusted application.

Callers are authenticated by the built-in ES256 trusted-issuer check first and then by the
`IApplicationCallerAuthenticator`s the application registered in `builder.Providers.Callers` (for an
application-set member, in that member's `AddApplication(..., configure)` callback), so a
peer or developer can present an identity provider's credential. Discovery and command
authorization apply to the mapped `ApplicationCaller`; with nothing registered, every response is
unchanged.

See [DESIGN.md](DESIGN.md) for lifecycle, authentication, and command-dispatch details.

The authenticated client also applies and deletes owned declarations through the served command
routes. `GatewayControlPlane.Configure` adapts the gateway's `CommandClients` into the server's
dispatcher seam. By default that list holds the generic `ResourceControlPlaneCommandClient`,
registered as `IGatewayResourceCommandClient.AnyKind`. A served command selects its dispatcher the
same way local delivery does: an exact manifest kind first, then the catch-all. Both paths retain
`Applied`/`Rejected` and provider detail, and a failed deletion retains the key's ownership
reservation.

The serving gateway's `IResourceTransportTrustProvider` supplies its application's TLS validator
to the required `serverCertificateValidator` parameter on dispatcher apply and delete. HTTPS
resource delivery uses the same anchors as probes and store reads; `null` keeps default trust.

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
