# Assimalign.Cohesion.SecretStore.Hosting

## Summary

Provides the SecretStore runtime: `SecretStoreApplication.CreateBuilder(args)`, protected durable
secret/trust/certificate storage, an ECDSA certificate authority, and the HTTP data and control
protocol served on the resource's `api` endpoint.

## Current behavior

- Captures the ambient `ResourceRuntime.Current` context when the builder is created and honors
  its `api` endpoint, `data` mount, identity, environment, application trust key, and registered
  control plane.
- Always appends an HTTP/1 endpoint service after caller-registered host services, so the endpoint
  starts last and drains first.
- Authenticates gateway-managed `/cohesion/v1/*` calls with trusted ES256 bootstrap JWTs and
  distinguishes unauthenticated (`401`) from wrong-audience (`403`) requests.
- Interoperates with `SecretStore.Client` for secret reads, durable leaf-certificate reads, and the
  `cohesion.trust.add` command.
- Protects secrets, issuer state, CA keys, pending enrollment keys, and leaf bundles at rest.
- Supports durable standalone CA self-seeding and explicit request/sign/complete intermediate-CA
  enrollment with optional Platform-root pinning.
- Renews named leaves on resolution and checks the HTTPS transport leaf at listener startup; a
  continuously running host must restart before transport expiry until the shared TLS listener
  supports asynchronous certificate selection per handshake.

## Configuration

Endpoint precedence is registered-control-plane observation, ambient `api`, `--endpoint`, then
`https://127.0.0.1:8443`. Data-path precedence is ambient `data` mount, `--data`, then
`<content-root>/data`. Plaintext HTTP is permitted only on loopback in
`Development`. A standalone host has no bootstrap credential and therefore may bind only to
loopback, including when it uses HTTPS.

Builder-declared secrets seed missing durable entries. `AddCertificateAuthority` controls the
standalone root common name, initial certificate/key material, pending Platform enrollment,
Platform root pin, and standalone self-seeding. Durable authority state takes precedence on
restart.

The persistent volume's file-system ACL protects the colocated DataProtection key ring; an
external KMS/wrapping-key integration and protected-record rewrap migration remain future work.

## Known gap

The configured Platform enrollment endpoint is not called automatically. A gateway/operator must
drive the three enrollment routes, deliver trust grants, and provide an out-of-band root for the
first HTTPS connection. A pending child also cannot start that HTTPS listener before it owns an
enrolled or provisional transport identity; the loopback HTTP enrollment path is Development-only.
`certs/public`/ACME issuance and gateway `parameter:` certificate mounts are also not implemented
by this host. See [DESIGN.md](./DESIGN.md) for the exact routes and bootstrap boundary.

## Public type

- `SecretStoreApplication` — static creation facade; runtime implementation types are internal.
