# Assimalign.Cohesion.SecretStore.Client Design

## Design intent

The client is the narrow O13 exception that lets an orchestration gateway resolve SecretStore
mount sources without learning the store's protocol or referencing a runtime module. The public
contract is interface-first and the HTTP implementation remains internal.

## Dependency boundary

The package references only `Assimalign.Cohesion.Core` for the Cohesion endpoint guard on
`System.Uri`. The guard requires an absolute, host-bearing URI with a port from 1 through 65535 and
rejects user information, query strings, and fragments. The package deliberately does not reference
`Assimalign.Cohesion.SecretStore`, `Assimalign.Cohesion.SecretStore.Hosting`, shared Hosting, Web,
dependency injection, configuration, logging, or Microsoft.Extensions packages. It is a standalone
NuGet package and is not part of an `App.SecretStore` shared framework.

## Protocol

The client defines the initial client-side protocol under the endpoint's existing path prefix:

- `GET /cohesion/v1/secrets?path=<escaped-path>` returns secret bytes.
- `GET /cohesion/v1/certificates?name=<escaped-name>` returns a PEM string.
- `POST /cohesion/v1/commands` sends the source-generated JSON `ResourceCommand`
  envelope and succeeds on any 2xx response.

Every request carries `Authorization: Bearer <opaque-token>`. Redirects and cookies are disabled so
a credential is never forwarded to a different authority or mixed with ambient cookie state. Non-success status codes surface as
`HttpRequestException`; cancellation propagates unchanged.

The current SecretStore Hosting endpoint is a dormant filler and does not serve this protocol.
Implementing that server surface, mount-source resolution, typed commands, and command handlers is
outside item 25a. Item 31c fills the typed command layer over the generic command transport.

## Credential and command contracts

`ClientCredential` is intentionally package-local: it is an opaque token holder, not a JWT model or
token provider. Its formatted representation is redacted. `ResourceCommand` is likewise a minimal,
package-local transport envelope whose payload bytes are base64-encoded by JSON so this package
does not depend on the shared Hosting seam while
the Core-only orchestration command contract is still being delivered by item 23b.

## Transport lifecycle

Production clients share a process-lifetime `SocketsHttpHandler`; creating clients is synchronous
and performs no I/O. The internal test seam accepts an `HttpMessageInvoker`, allowing unit tests to
exercise the complete request/response protocol with an in-memory handler and no sockets.

## AOT posture

Command JSON uses a source-generated `JsonSerializerContext`. The implementation uses no
reflection, runtime type inspection, dynamic code generation, assembly scanning, DI activation, or
runtime configuration binding, preserving trimming and NativeAOT compatibility.

## Non-goals

- Reading bootstrap-token files or acquiring and rotating credentials.
- Resolving mount declarations in a gateway.
- Hosting SecretStore endpoints or implementing persistence, trust, enrollment, or certificate
  issuance.
- Defining SecretStore-specific command verbs or handlers before item 31c.
