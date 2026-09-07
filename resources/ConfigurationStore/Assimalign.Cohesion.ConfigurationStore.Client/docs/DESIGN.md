# Assimalign.Cohesion.ConfigurationStore.Client Design

## Design intent

The client is the narrow gateway-side protocol boundary for ConfigurationStore. It reads named
configuration namespaces and submits control-plane commands without exposing or depending on the
store's hosting implementation. This keeps a gateway from acquiring a transitive dependency on
`Assimalign.Cohesion.ConfigurationStore.Hosting`.

## Dependency boundary

The package references only `Assimalign.Cohesion.Core`, solely for `EndpointAddress`. HTTP transport,
authentication headers, and JSON processing use BCL APIs. The package deliberately does not depend
on the ConfigurationStore area root, Hosting, DependencyInjection, Web, or ApplicationModel.

`ResourceCommand` and `ClientCredential` are package-local contracts. Their shapes match the store
protocol while avoiding a dependency on a runtime assembly merely to send a request.

## Public surface

`IConfigurationStoreClient` is the consumer contract. `ConfigurationStoreClient.Create` validates
that its endpoint uses HTTP or HTTPS and returns an internal implementation. Client creation performs
no network I/O.

The caller supplies an opaque `ClientCredential`. The client does not inspect JWT claims, load token
files, refresh credentials, or expose the token through formatting. It attaches the value as a Bearer
credential for every request.

## Wire protocol

The first protocol version has two operations:

| Operation | Request | Successful response |
| --- | --- | --- |
| Read namespace | `GET /cohesion/v1/namespaces?name=<escaped-name>` | A direct JSON object with string or `null` values |
| Submit command | `POST /cohesion/v1/commands` | Any success status with no required response body |

The endpoint's existing base path is prepended to both routes. Namespace names and command IDs are
query-escaped and otherwise treated as opaque values. Command bodies use camel-case JSON and include
the id, kind, owner, key, and base64-encoded payload properties.

## Transport and ownership

The public factory shares one process-lifetime `HttpMessageInvoker` backed by `SocketsHttpHandler`.
Redirect following and cookies are disabled so Authorization cannot cross authorities or mix with
ambient cookie state. Clients do not
own or dispose that shared transport. An internal overload accepts a caller-owned invoker for tests;
`InternalsVisibleTo` grants access only to the package's test assembly.

## Errors and cancellation

Blank namespace names and invalid endpoint schemes fail before transport use. HTTP failures retain
the BCL `HttpRequestException` shape through `EnsureSuccessStatusCode`. Invalid, empty, or JSON `null`
namespace documents fail with `JsonException`. Cancellation tokens flow unchanged through send and
content-read operations.

## AOT posture

All command serialization and namespace deserialization use `ConfigurationStoreClientJsonContext`,
an internal `JsonSerializerContext`. No reflection-based serializer overload, runtime assembly scan,
or dynamic code generation is used. The project inherits the repository's `net10.0`, trimming, and
NativeAOT settings.

## Non-goals

- Hosting or implementing ConfigurationStore endpoints.
- Discovering endpoints or credentials from process state.
- Parsing, validating, or refreshing the opaque Bearer credential.
- Following redirects or retrying failed requests.
- Providing dependency-injection registration or shared-framework delivery.
