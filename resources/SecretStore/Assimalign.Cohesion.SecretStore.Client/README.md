# Assimalign.Cohesion.SecretStore.Client

Thin, NativeAOT-safe protocol client for reading SecretStore secrets and certificates. A program
that reads the store's secrets or certificates references it directly; the client never references
the SecretStore runtime, and a gateway reaches a managed instance through the resource integration,
`Assimalign.Cohesion.SecretStore.Hosting.ApplicationModel`, instead.

The package exposes `ISecretStoreClient` and the `SecretStoreClient.Create(Uri, ClientCredential)`
factory, with an overload that takes a caller-owned transport. It references Core only; it does not
reference `Assimalign.Cohesion.SecretStore.Hosting`, shared Hosting, Web, or Microsoft.Extensions
packages.

Core supplies the Cohesion endpoint guard for `System.Uri`: the factory requires an absolute HTTP
or HTTPS URI with a host and valid port, without user information, a query, or a fragment.

See [the overview](./docs/OVERVIEW.md) and [design](./docs/DESIGN.md) for the wire contract and
scope boundaries.
