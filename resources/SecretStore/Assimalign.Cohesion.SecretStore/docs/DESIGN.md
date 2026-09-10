# Assimalign.Cohesion.SecretStore Design

## Design intent

The area root owns the contracts that executable composition and feature packages target.
`ISecretStoreApplicationBuilder` is the contract-only builder seam, while
`ISecretStoreApplication` supplies the host lifecycle expected by an executable resource. The
root also owns the two foundational SecretStore declarations needed by a `Program.cs`:
`AddSecret` and `AddCertificateAuthority`. They are builder members rather than Hosting-owned
verbs, so any implementation of the root seam can consume the same declaration data.

## Hosting isolation

The root references only the shared Hosting foundation. The concrete builder, host, context, and
runtime options remain internal to `Assimalign.Cohesion.SecretStore.Hosting`; feature libraries
must not reference that runtime module. `CertificateAuthorityOptions` is composition data, not a
runtime service: it introduces no network, certificate, configuration, or service-container
dependency into the root.

## Code-first secret seeds

`AddSecret(path, value)` declares first-start seed data. An implementation snapshots the bytes at
registration and writes them only when the durable store has no version at that exact, ordinal
path. This distinction is deliberate: composition must not roll back a value later changed by
rotation or a command. Duplicate declarations are rejected instead of making call order an
implicit overwrite policy.

## Certificate-authority bootstrap

`AddCertificateAuthority` captures one `CertificateAuthorityOptions` declaration. Existing durable
authority state always wins. On first start, the decision order is:

1. Use the paired `InitialCertificate` and `InitialPrivateKey` PEM material when supplied. This is
   the explicit `parameter:`/operator-seeded route.
2. Otherwise, when `PlatformEnrollmentEndpoint` is configured, create durable pending state for
   gateway-mediated enrollment with the Platform SecretStore. `PlatformCertificate` optionally
   pins the Platform trust anchor when the response is completed.
3. Otherwise, when `SelfSeedWhenNoPlatform` is true (the default), generate a self-signed
   development root.
4. Otherwise, fail configuration because no authority source exists.

A configured or incomplete Platform enrollment never falls through to self-seeding. Silent fallback
would split the application's certificate hierarchy from the Platform hierarchy and hide an
operational trust failure. The default common name is stable for standalone use and can be
overridden for an application-specific root. Intermediate requests use the authenticated ambient
application/resource identity as their subject.

## Composition lifecycle

The builder also accepts existing `IHostService` instances and factories that receive the newly
created area `IHostContext`. Each factory is invoked once per `Build()`, and the resulting services
are retained in registration order so the shared host starts them in that order and stops them in
reverse. The collection is empty when callers register nothing, and the host environment remains
production.

## AOT posture

The contracts and option values require no reflection, dynamic code generation, runtime assembly
scanning, configuration binding, or container-based activation. Secret and certificate material is
carried as `ReadOnlyMemory<byte>`, leaving parsing and cryptographic ownership to the Hosting
implementation while the public root remains trimming- and NativeAOT-safe.

## Non-goals

This project does not persist or encrypt secrets, generate or enroll certificates, contact the
Platform service, verify bootstrap credentials, or expose a protocol endpoint. Those are runtime
responsibilities. Cross-resource command verbs (`AddSecret`, `IssueCertificate`, and `Enroll` on a
SecretStore resource descriptor) are a separate ApplicationModel surface and are not builder-time
seed declarations.
