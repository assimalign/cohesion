# Assimalign.Cohesion.SecretStore Design

## Design intent

The area root owns the contracts that executable composition and feature packages target.
`ISecretStoreApplicationBuilder` is the contract-only builder seam, while
`ISecretStoreApplication` supplies the hosting-free application lifecycle. The
root also owns the two foundational SecretStore declarations needed by a `Program.cs`:
`AddSecret` and `AddCertificateAuthority`. They are builder members rather than Hosting-owned
verbs, so any implementation of the root seam can consume the same declaration data.

## Hosting isolation

The root contracts are hosting-free (O34): `ISecretStoreApplication` exposes `Context`, `StartAsync`, and `StopAsync`; `ISecretStoreApplicationContext` exposes `ContentRootPath`. `ISecretStoreApplicationBuilder` owns area declarations and `Build()`. The root and feature packages reference no `Assimalign.Cohesion.Hosting*` library; COHRES004 enforces the boundary.

The concrete application, builder, and context live in `Assimalign.Cohesion.SecretStore.Hosting`; options and supporting services remain internal.

`CertificateAuthorityOptions` is composition data: it introduces no network, certificate,
configuration, or service-container dependency into the root.

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

Background-work registration belongs to the concrete `SecretStoreApplicationBuilder`: `AddService(IHostService)` and `AddService(Func<SecretStoreApplicationContext, IHostService>)`. The factory deliberately receives the concrete context, unlike Web's AddService and Database's AddServer interface-context overloads, so hosting consumers can use environment, state, and hosted-service members beyond the small root contract. Factories run once per build against the same context retained by the application; the hosted-service snapshot is installed after factory evaluation. Services start in registration order and stop in reverse. No area-owned service abstraction is introduced.

Enabled resources retain their ambient context and registered control-plane behavior; plain application defaults are described by the Hosting design.

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

## Hosting-free application contract (O34)

`SecretStoreApplication.CreateBuilder(args)` returns the public concrete `SecretStoreApplicationBuilder`; its `Build()` returns the public `SecretStoreApplication : Host<SecretStoreApplicationContext>`. The public `SecretStoreApplicationContext` implements `ISecretStoreApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration (`AddService`) is available only on the concrete Hosting builder.
