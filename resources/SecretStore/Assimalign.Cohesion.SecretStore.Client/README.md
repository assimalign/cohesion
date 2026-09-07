# Assimalign.Cohesion.SecretStore.Client

Thin, NativeAOT-safe protocol client used by gateways to resolve secret and certificate mounts
without referencing the SecretStore runtime.

The package exposes `ISecretStoreClient`, the `SecretStoreClient.Create(EndpointAddress,
ClientCredential)` factory, and the generic command transport that design item 31c will extend
with SecretStore-specific commands. It references Core only; it does not reference
`Assimalign.Cohesion.SecretStore.Hosting`, shared Hosting, Web, or Microsoft.Extensions packages.

See [the overview](./docs/OVERVIEW.md) and [design](./docs/DESIGN.md) for the wire contract and
scope boundaries.
