# Assimalign.Cohesion.SecretStore.Hosting

## Summary

Provides the SecretStore runtime: `SecretStoreApplication.CreateBuilder(args)`, protected durable
secret/certificate storage, an ECDSA certificate authority, and the HTTP data
protocol served on the resource's `api` endpoint.

## Current behavior

- The retired resource integration (control plane, declarative commands, bootstrap-token
  authentication, trust store, endpoint certificates, telemetry) was removed on 2026-10-31 with the
  application-model restart (assimalign/cohesion#1759), and the area's resource integration
  returns as `Assimalign.Cohesion.SecretStore.Hosting.ApplicationModel`.
- Always appends an HTTP/1 endpoint service after caller-registered host services, so the endpoint
  starts last and drains first.
- Interoperates with `SecretStore.Client` for secret reads and durable leaf-certificate reads.
- Protects secrets, CA keys, pending enrollment keys, and leaf bundles at rest.
- Supports durable standalone CA self-seeding and explicit sign/complete intermediate-CA
  enrollment with optional Platform-root pinning.
- Renews named leaves on resolution and checks the HTTPS transport leaf at listener startup; a
  continuously running host must restart before transport expiry until the shared TLS listener
  supports asynchronous certificate selection per handshake.

## Configuration

Endpoint precedence is `--endpoint`, then
`https://127.0.0.1:8443`. Data-path precedence is `--data`, then
`<content-root>/data`. Plaintext HTTP is permitted only on loopback in
`Local`. A standalone host has no bootstrap credential and therefore may bind only to
loopback, including when it uses HTTPS. Its `/cohesion/v1` and `/cohesion/v1/*` routes accept only
transport peers at `127.0.0.1`, `::1`, or IPv4-mapped `127.0.0.1`; other or unknown peers receive
`403`. Forwarded headers do not affect this check. Bare `/healthz`, `/readyz`, and `/livez` remain
open for probes. This tightens standalone request authorization from 10.0.0-preview.1.

Builder-declared secrets seed missing durable entries. `AddCertificateAuthority` controls the
standalone root common name, initial certificate/key material, pending Platform enrollment,
Platform root pin, and standalone self-seeding. Durable authority state takes precedence on
restart.

The persistent volume's file-system ACL protects the colocated DataProtection key ring; an
external KMS/wrapping-key integration and protected-record rewrap migration remain future work.

## Known gap

A configured Platform enrollment endpoint needs the ambient application and resource names, which
a plain host does not have, so the host fails to start with it.
`certs/public`/ACME issuance is also not implemented
by this host. See [DESIGN.md](./DESIGN.md) for the exact routes.

## Public types

- `SecretStoreApplication` — concrete host and creation entry point.
- `SecretStoreApplicationBuilder` — composition and background-work registration.
- `SecretStoreApplicationContext` — host environment and runtime state.

## Concrete composition (T10 / O34)

`SecretStoreApplication.CreateBuilder(args)` returns the public concrete `SecretStoreApplicationBuilder`; its `Build()` returns the public `SecretStoreApplication : Host<SecretStoreApplicationContext>`. The public `SecretStoreApplicationContext` implements `ISecretStoreApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration belongs to the concrete `SecretStoreApplicationBuilder`: `AddService(IHostService)` and `AddService(Func<SecretStoreApplicationContext, IHostService>)`. The factory receives the same concrete context as Web's and Database's AddService, so hosting consumers can use environment, state, and hosted-service members beyond the small root contract. Database's root-level AddServer keeps the interface context. Factories run once per build against the same context retained by the application; the hosted-service snapshot is installed after factory evaluation. Services start in registration order and stop in reverse. No area-owned service abstraction is introduced.

The base host owns the already-cancelled run semantic: one complete start and graceful stop
with fresh lifecycle tokens, normal run-observer notifications, and a final Stopped state.
The concrete application and IHost route share it. Startup failures still roll back and propagate.

## Dependency injection composition

`SecretStoreApplicationBuilder.Services` holds every registration the application runs with. `AddService` registers an `IHostService`, and a direct `IHostService` registration joins the lifecycle in the same order. `Build` closes registration and resolves the services once. Disposing the application disposes every factory-created service. See [DESIGN.md](DESIGN.md#dependency-injection-composition).

`AddSecret` accepts the root's generated `SecretStoreSeedPath` (ordinary strings convert implicitly).
It refuses blank paths, exact `trusted-issuers.json`, and `default` during registration. Seeds may
still use `certs/`. Certificate reads share the client package's validation predicate through
internal shared source, so malformed values cannot bypass validation by supplying raw query strings.
