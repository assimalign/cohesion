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
