# Assimalign.Cohesion.ConfigurationStore.Client Design

## Design intent

The client is the narrow protocol boundary for ConfigurationStore. It reads named configuration
namespaces without exposing or depending on the store's hosting implementation, so no consumer
acquires a transitive dependency on `Assimalign.Cohesion.ConfigurationStore.Hosting`. A program that
reads the store's namespaces references the package directly. A gateway does not use it: a managed
ConfigurationStore instance is reached through its resource integration,
`Assimalign.Cohesion.ConfigurationStore.Hosting.ApplicationModel`, whose actions the gateway runs
through the platform the instance runs on.

## Dependency boundary

The package references only `Assimalign.Cohesion.Core`, solely for the Cohesion endpoint guard on
`System.Uri`. The guard requires an absolute, host-bearing URI with a port from 1 through 65535 and
rejects user information, query strings, and fragments. HTTP transport, authentication headers,
and JSON processing use BCL APIs. The package deliberately does not depend on the ConfigurationStore
area root, Hosting, DependencyInjection, Web, or ApplicationModel.

`ClientCredential` is a package-local contract, so a request needs no runtime assembly.

## Public surface

`IConfigurationStoreClient` is the consumer contract. It lists namespace names and reads a named
namespace. `ConfigurationStoreClient.Create` validates that its endpoint uses HTTP or HTTPS and
returns an internal implementation. Client creation performs no network I/O.

The caller supplies an opaque `ClientCredential`. The client does not inspect JWT claims, load token
files, refresh credentials, or expose the token through formatting. It attaches the value as a Bearer
credential for every request.

## Wire protocol

The protocol has two operations:

| Operation | Request | Successful response |
| --- | --- | --- |
| List namespaces | `GET /cohesion/v1/namespaces` | A direct JSON array of namespace-name strings |
| Read namespace | `GET /cohesion/v1/namespaces?name=<escaped-name>` | A direct JSON object with string or `null` values |

The endpoint's existing base path is prepended to every route. Namespace lookup names are
query-escaped and otherwise treated as opaque values. Listing preserves the server's array order and
duplicates.

### Removed: the command surface (2026-11-01)

The client used to submit resource commands: `SendCommandAsync`, `ObserveCommandAsync`,
`DeleteCommandAsync`, the `ResourceCommand` and `ResourceCommandObservation` types, and
`CreateForControlPlane`, which pointed the command routes at a manifest's control-plane address. All
of it targeted `/cohesion/v1/commands`, a route of the old resource control plane. The application
model restart removed that control plane, and ConfigurationStore.Hosting no longer serves the route,
so the surface called an endpoint that does not exist. A managed instance's state changes now arrive
as actions through its platform (`ConfigurationStore.Hosting.ApplicationModel`).

## Transport and ownership

The two-argument factory shares a process-lifetime `HttpMessageInvoker` backed by
`SocketsHttpHandler`, with redirects and cookies disabled. Creating clients is synchronous and
performs no I/O. The three-argument `Create` accepts a caller-owned transport, including its TLS trust
policy. The client never owns or disposes the supplied transport; the caller retains it until
requests finish, then disposes it. Trust discovery and protected-file reading stay outside this
Core-only package.

## Errors and cancellation

Blank namespace names and invalid endpoint schemes fail before transport use. HTTP failures retain
the BCL `HttpRequestException` shape through `EnsureSuccessStatusCode`. Invalid, empty, or JSON `null`
namespace-list and namespace-value documents fail with `JsonException`. Cancellation tokens flow
unchanged through send and content-read operations.

## AOT posture

Namespace-list and namespace-value deserialization use `ConfigurationStoreClientJsonContext`, an
internal `JsonSerializerContext`. No reflection-based serializer overload, runtime assembly scan, or
dynamic code generation is used. The project inherits the repository's `net10.0`, trimming, and
NativeAOT settings.

## Non-goals

- Hosting or implementing ConfigurationStore endpoints.
- Changing a store's state: a managed instance takes changes as actions through its platform.
- Discovering endpoints or credentials from process state.
- Parsing, validating, or refreshing the opaque Bearer credential.
- Following redirects or retrying failed requests.
- Providing dependency-injection registration or shared-framework delivery.
