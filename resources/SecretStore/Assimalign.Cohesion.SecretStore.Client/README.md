# Assimalign.Cohesion.SecretStore.Client

Thin, NativeAOT-safe protocol client used by gateways to resolve secret and certificate mounts
without referencing the SecretStore runtime.

The package exposes `ISecretStoreClient`, the `SecretStoreClient.Create(Uri,
ClientCredential)` factory, and `CreateForControlPlane` for a full control-plane address.
`ObserveCommandAsync` and `DeleteCommandAsync` return command observations while
`SendCommandAsync` preserves the existing trust-grant transport. It references Core only; it does not reference
`Assimalign.Cohesion.SecretStore.Hosting`, shared Hosting, Web, or Microsoft.Extensions packages.

Core supplies the Cohesion endpoint guard for `System.Uri`: the factory requires an absolute HTTP
or HTTPS URI with a host and valid port, without user information, a query, or a fragment.

See [the overview](./docs/OVERVIEW.md) and [design](./docs/DESIGN.md) for the wire contract and
scope boundaries.
