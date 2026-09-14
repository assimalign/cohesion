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

`Assimalign.Cohesion.SecretStore.Hosting` serves this protocol. A gateway-managed store validates
the ES256 bearer token against its persistent trusted-issuer set and distinguishes an invalid or
unknown credential (`401`) from a valid credential with the wrong resource audience (`403`). Secret
and certificate responses are marked `no-store`. Item 31c adds observed secret and certificate
mutations alongside the existing `cohesion.trust.add` bootstrap command.

## Credential and command contracts

`ClientCredential` is intentionally package-local: it is an opaque token holder, not a JWT model or
token provider. Its formatted representation is redacted. `ResourceCommand` is likewise a minimal,
package-local transport envelope whose payload bytes are base64-encoded by JSON so this package
does not depend on the shared Hosting seam. Gateway adapters translate between the separate
client-side and orchestration command contracts.

## Transport lifecycle

The two-argument factories share a process-lifetime `HttpMessageInvoker` backed by
`SocketsHttpHandler`, with redirects and cookies disabled. Creating clients is synchronous and
performs no I/O. Both `Create` and
`CreateForControlPlane(Uri controlPlaneAddress, ClientCredential credential, HttpMessageInvoker transport)`
accept a caller-owned transport, including its TLS trust policy. The latter preserves the exact
manifest control-plane path for command delivery. The client never owns or disposes the supplied
transport; the caller retains it until requests finish, then disposes it. Trust discovery and
protected-file reading stay outside this Core-only package.

## AOT posture

Command JSON uses a source-generated `JsonSerializerContext`. The implementation uses no
reflection, runtime type inspection, dynamic code generation, assembly scanning, DI activation, or
runtime configuration binding, preserving trimming and NativeAOT compatibility.

## Non-goals

- Reading bootstrap-token files or acquiring and rotating credentials.
- Resolving mount declarations in a gateway.
- Hosting SecretStore endpoints or implementing persistence, trust, enrollment, or certificate
  issuance.
- Defining SecretStore-specific verbs or handlers; those belong to ApplicationModel and Hosting.

## Observed command delivery

CreateForControlPlane accepts the full control-plane prefix. ObserveCommandAsync and
DeleteCommandAsync return ResourceCommandObservation; SendCommandAsync retains its original
Task-returning behavior. The new secret/certificate commands return 200 application/octet-stream
on success and JSON {status,detail} on refusal. Legacy cohesion.trust.add keeps empty 204/409/403
responses. The observation client treats an empty 2xx body as Applied, or Deleted for DELETE,
and supplies a named HTTP detail when a legacy refusal has no body. DELETE is for the new kinds;
trust grants remain POST-only. The package still has exactly one Core reference and no Hosting
or Gateway dependencies. Identity verification and grant policy belong to the endpoint.

The existing custom-transport Create overload is public for gateway-supplied TLS trust. The caller owns the HttpMessageInvoker and must retain it until requests complete, then dispose it. The client remains Core-only and does not discover trust or read protected files.
