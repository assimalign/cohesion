# Assimalign.Cohesion.SecretStore.Client Design

## Design intent

The client is the SecretStore protocol boundary: a program that reads secrets and certificates
references it, without referencing a runtime module, and the package takes no application-model
reference. A gateway does not use it: a managed SecretStore instance is reached through its
resource integration, `Assimalign.Cohesion.SecretStore.Hosting.ApplicationModel`, whose actions the
gateway runs through the platform the instance runs on. The public contract is interface-first and
the HTTP implementation remains internal.

## Dependency boundary

The package references only `Assimalign.Cohesion.Core` for the Cohesion endpoint guard on
`System.Uri`. The guard requires an absolute, host-bearing URI with a port from 1 through 65535 and
rejects user information, query strings, and fragments. The package deliberately does not reference
`Assimalign.Cohesion.SecretStore`, `Assimalign.Cohesion.SecretStore.Hosting`, shared Hosting, Web,
dependency injection, configuration, logging, or Microsoft.Extensions packages. It is a standalone
NuGet package and is not part of an `App.SecretStore` shared framework.

The stateless certificate-reference grammar in `shared/` is compiled into both this package and
Hosting through `CohesionSharedSource`. It carries no state or public contract and introduces no
assembly reference, so the client remains Core-only and Hosting does not acquire a NuGet-only
client dependency. The generated `SecretStoreCertificateReference.IsValid` and the runtime
retrieval boundary use this one predicate.

## Certificate references

`ISecretStoreClient.GetCertificateAsync` takes the generated `SecretStoreCertificateReference`.
The protocol accepts exact `ca/root`, or `certs/` followed by a nonblank segment with neither slash
nor backslash, excluding `.` and `..`. These are case-sensitive storage identifiers; ordinal
comparison preserves the protocol's spelling. The generated constructor and implicit string
conversion reject invalid input before HTTP, and `TryCreate` provides a non-throwing check.
The client explicitly rejects `default`, whose null value bypasses construction.

Compared with 10.0.0-preview.1, changing the parameter from `string` is a binary and implementer
source break. Ordinary string call sites retain the implicit conversion. Malformed nonblank
references now throw `ArgumentException` locally instead of producing a remote request and an
eventual HTTP refusal.

## Protocol

The client defines the client-side protocol under the endpoint's existing path prefix:

- `GET /cohesion/v1/secrets?path=<escaped-path>` returns secret bytes.
- `GET /cohesion/v1/certificates?name=<escaped-name>` returns a PEM string.

Every request carries `Authorization: Bearer <opaque-token>`. Redirects and cookies are disabled so
a credential is never forwarded to a different authority or mixed with ambient cookie state. Non-success status codes surface as
`HttpRequestException`; cancellation propagates unchanged.

`Assimalign.Cohesion.SecretStore.Hosting` serves the secret and certificate routes. The area's resource
integration is `Assimalign.Cohesion.SecretStore.Hosting.ApplicationModel`. Secret and certificate
responses are marked `no-store`.

### Read-only

The client reads; it changes no store state. A managed instance takes state changes as actions
through its platform (`SecretStore.Hosting.ApplicationModel`).

## Credential contract

`ClientCredential` is intentionally package-local: it is an opaque token holder, not a JWT model or
token provider. Its formatted representation is redacted.

## Transport lifecycle

The two-argument factory shares a process-lifetime `HttpMessageInvoker` backed by
`SocketsHttpHandler`, with redirects and cookies disabled. Creating clients is synchronous and
performs no I/O. The three-argument `Create` accepts a caller-owned transport, including its TLS
trust policy. The client never owns or disposes the supplied transport; the caller retains it until
requests finish, then disposes it. Trust discovery and protected-file reading stay outside this
Core-only package.

## AOT posture

The client serializes no JSON. The implementation uses no reflection, runtime type inspection,
dynamic code generation, assembly scanning, DI activation, or runtime configuration binding,
preserving trimming and NativeAOT compatibility.

## Non-goals

- Reading token files or acquiring and rotating credentials.
- Changing a store's state: a managed instance takes changes as actions through its platform.
- Hosting SecretStore endpoints or implementing persistence or certificate issuance.
- Defining SecretStore-specific verbs or handlers; those belong to SecretStore.Hosting and its
  resource integration.
